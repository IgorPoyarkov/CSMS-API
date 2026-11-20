using System.Security.Cryptography;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure.Npa;

/// <summary>Итог одного прогона агента по акту.</summary>
public sealed record NpaSyncResult(
    string Code,
    string Status,
    string Version,
    int RuleCount,
    int PageCount,
    int UnresolvedCount,
    string? OcrEngine,
    string Message,
    int LlmSuggestions = 0);

/// <summary>Источник официального текста акта.</summary>
internal sealed record NpaSource(
    byte[] Bytes,
    bool IsDocx,
    string? EoNumber,
    string? Number,
    string Title,
    DateOnly? DocumentDate,
    DateOnly? PublishDate,
    string? Url);

/// <summary>
/// Оркестратор агента: скачать официальный текст, понять, что он изменился,
/// распознать, собрать правила, протянуть их по дереву ОКПД2 и опубликовать
/// новой версией. Старая версия остаётся в истории.
/// </summary>
public sealed class NpaSyncService(
    NpaOptions options,
    PravoDocumentClient pravo,
    NpaPdfParser pdfParser,
    INpaOcrEngine ocr,
    INpaRuleStore store,
    IReferenceDataStore references,
    NpaLlmAnalyzer analyzer,
    ILogger<NpaSyncService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NpaSyncResult> SyncAsync(
        string code,
        bool force = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var document = options.Find(code)
            ?? throw new ArgumentException($"Акт {code} не описан в Npa:Documents.");

        try
        {
            var source = await LoadAsync(document, cancellationToken);
            var result = await ProcessAsync(document, source, force, dryRun, cancellationToken);

            if (!dryRun)
            {
                await store.MarkCheckedAsync(
                    code, result.Status, result.Message, cancellationToken: cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !dryRun)
        {
            // Ошибка тоже является проверкой: в реестре должно быть видно,
            // что акт проверялся, даже если скачивание или разбор не удались.
            await store.MarkCheckedAsync(
                code,
                NpaCheckStatuses.Error,
                exception.Message,
                cancellationToken: CancellationToken.None);
            throw;
        }
    }

    /// <summary>Разбор загруженного файла: когда у заказчика есть своя редакция в Word.</summary>
    public async Task<NpaSyncResult> ImportFileAsync(
        string code,
        byte[] content,
        string fileName,
        bool force = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var document = options.Find(code)
            ?? throw new ArgumentException($"Акт {code} не описан в Npa:Documents.");

        var isDocx = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
        if (!isDocx && !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Поддерживаются только .pdf и .docx.");
        }

        var source = new NpaSource(
            content,
            isDocx,
            document.EoNumber,
            document.Number,
            document.Title,
            null,
            null,
            document.File);

        try
        {
            var result = await ProcessAsync(document, source, force, dryRun, cancellationToken);

            if (!dryRun)
            {
                await store.MarkCheckedAsync(
                    code, result.Status, result.Message, cancellationToken: cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !dryRun)
        {
            await store.MarkCheckedAsync(
                code,
                NpaCheckStatuses.Error,
                exception.Message,
                cancellationToken: CancellationToken.None);
            throw;
        }
    }

    private async Task<NpaSyncResult> ProcessAsync(
        NpaDocumentOptions document,
        NpaSource source,
        bool force,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(source.Bytes));
        var baseVersion = source.EoNumber is null
            ? $"file-{sha256[..16]}"
            : $"pravo-{source.EoNumber}";

        var active = await store.GetActiveAsync(document.Code, cancellationToken);
        if (!force &&
            active is not null &&
            string.Equals(active.PdfSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new NpaSyncResult(
                document.Code,
                NpaCheckStatuses.UpToDate,
                active.Version,
                active.ItemCount,
                active.PagesCount,
                active.UnresolvedCount,
                active.OcrEngine,
                "Официальный текст не изменился: SHA-256 совпадает с активной версией.");
        }

        // Переразметка того же официального текста (например, после исправления
        // парсера) не перезаписывает историю: публикуется следующая версия.
        var version = baseVersion;
        if (force)
        {
            var attempt = 2;
            while (await store.VersionExistsAsync(document.Code, version, cancellationToken))
            {
                if (attempt > 99)
                {
                    throw new InvalidOperationException(
                        $"Не удалось подобрать свободную версию для акта {document.Code}.");
                }

                version = $"{baseVersion}-r{attempt}";
                attempt++;
            }
        }

        var catalog = await LoadCatalogAsync(cancellationToken);
        var resolver = new Okpd2CodeResolver(catalog);

        var parsed = source.IsDocx
            ? NpaDocxParser.Parse(new MemoryStream(source.Bytes), resolver)
            : await pdfParser.ParseAsync(source.Bytes, document, resolver, cancellationToken);

        var title = string.IsNullOrWhiteSpace(document.Title) ? source.Title : document.Title;
        var direct = NpaRuleBuilder.Build(parsed, options.MinAutoConfidence, catalog).ToList();
        var llmSuggestions = 0;

        // Второй агент подключается только к спорным строкам и только по явному
        // включению. Его предложения никогда не становятся авто-правилами.
        if (options.Llm.Enabled && parsed.Unresolved.Count > 0)
        {
            var request = NpaLlmRuleAdapter.BuildRequest(
                document.Code,
                title,
                document.Number,
                parsed.Unresolved,
                catalog,
                options.Llm.MaxCandidates);

            if (request.Rows.Count > 0)
            {
                var analysis = await analyzer.AnalyzeAsync(request, cancellationToken);
                if (analysis.Error is not null)
                {
                    logger.LogWarning(
                        "Акт {Code}: LLM-аналит завершился с ошибкой: {Error}",
                        document.Code,
                        analysis.Error);
                }

                var known = new HashSet<string>(
                    direct.Select(rule => rule.Okpd2Code), StringComparer.Ordinal);
                foreach (var rule in NpaLlmRuleAdapter.ToRules(analysis, options.Llm.Model))
                {
                    // Явное правило первого агента сильнее предложения модели.
                    if (known.Add(rule.Okpd2Code))
                    {
                        direct.Add(rule);
                        llmSuggestions++;
                    }
                }
            }
        }

        var rules = NpaRuleHierarchy.Propagate(direct, catalog);
        var unresolvedJson = JsonSerializer.Serialize(parsed.Unresolved, JsonOptions);

        var import = new NpaDocumentImport(
            document.Code,
            version,
            source.EoNumber,
            source.Number ?? document.Number,
            title,
            source.DocumentDate,
            source.PublishDate,
            parsed.PageCount,
            source.Url,
            sha256,
            ocr.Name,
            rules,
            unresolvedJson,
            options.AutoPromote);

        if (dryRun)
        {
            return new NpaSyncResult(
                document.Code,
                "dry_run",
                version,
                rules.Count,
                parsed.PageCount,
                parsed.Unresolved.Count,
                ocr.Name,
                $"Разобрано без записи: прямых правил {direct.Count}, с учётом дерева {rules.Count}, предложений LLM {llmSuggestions}.",
                llmSuggestions);
        }

        var change = await store.PublishAsync(import, cancellationToken);

        logger.LogInformation(
            "Акт {Code}: версия {Version}, правил {Rules} (прямых {Direct}, от LLM {Llm}), страниц {Pages}, не разобрано {Unresolved}",
            document.Code,
            change.ToVersion,
            change.RuleCount,
            direct.Count,
            llmSuggestions,
            parsed.PageCount,
            parsed.Unresolved.Count);

        return new NpaSyncResult(
            document.Code,
            options.AutoPromote ? NpaCheckStatuses.Published : NpaCheckStatuses.Prepared,
            change.ToVersion,
            change.RuleCount,
            parsed.PageCount,
            parsed.Unresolved.Count,
            ocr.Name,
            options.AutoPromote
                ? "Опубликована новая активная версия правил."
                : "Версия подготовлена, активной осталась прежняя (Npa:AutoPromote=false).",
            llmSuggestions);
    }

    private async Task<NpaSource> LoadAsync(
        NpaDocumentOptions document,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(document.File))
        {
            var path = Path.IsPathRooted(document.File)
                ? document.File
                : Path.Combine(Directory.GetCurrentDirectory(), document.File);

            if (!File.Exists(path))
            {
                throw new ArgumentException($"Файл акта не найден: {path}.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return new NpaSource(
                bytes,
                path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase),
                document.EoNumber,
                document.Number,
                document.Title,
                null,
                null,
                path);
        }

        if (string.IsNullOrWhiteSpace(document.Number))
        {
            throw new ArgumentException(
                $"Npa:Documents:{document.Code}: для поиска на портале нужен Number.");
        }

        var publications = await pravo.FindAsync(document.Number, document.Block, cancellationToken);
        var match = publications.FirstOrDefault(item =>
            item.EoNumber.Equals(document.EoNumber, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new InvalidOperationException(
                $"Публикация {document.EoNumber} не найдена среди актов № {document.Number} " +
                $"(найдено {publications.Count}). Проверьте Npa:Documents:{document.Code}:EoNumber.");
        }

        var pdf = await pravo.DownloadPdfAsync(match.EoNumber, cancellationToken);

        return new NpaSource(
            pdf,
            IsDocx: false,
            match.EoNumber,
            match.Number,
            match.Title,
            match.DocumentDate,
            match.PublishDate,
            match.PdfUrl);
    }

    private async Task<Okpd2Catalog> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        var items = await references.GetAllAsync("nsiOKPD2", cancellationToken);
        var catalog = Okpd2Catalog.FromItems(items);

        if (catalog.IsEmpty)
        {
            logger.LogWarning(
                "Активный справочник nsiOKPD2 пуст: коды принимаются по структуре, все правила уйдут на проверку.");
        }

        return catalog;
    }
}
