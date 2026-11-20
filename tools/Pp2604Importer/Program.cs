using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csms.Infrastructure.Npa;
using Npgsql;

namespace Pp2604Importer;

/// <summary>
/// Загрузчик приложения N 2 ПП РФ N 2604 в схему legal. Источник — только
/// официальный портал опубликования: API даёт реквизиты документа и PDF,
/// из PDF извлечены 20 позиций приложения (OCR + сверка с изображением).
/// Кодов ОКПД2 в приложении нет, поэтому каждая связь несёт basis, источник
/// и признак ручной проверки. Повторный запуск идемпотентен: версия считается
/// по хешу документа и правил, старые версии остаются в БД неактивными.
/// </summary>
internal static class Program
{
    private const string Portal = "http://publication.pravo.gov.ru";
    private const string ApiUrl = Portal +
        "/api/Documents?Block=government&Number=2604&NumberSearchType=0&PageSize=30";
    private const string PdfUrlTemplate = Portal + "/file/pdf?eoNumber={0}";
    private const string ExtractionMethod =
        "OCR PDF официального опубликования (Docnet 300 dpi, Tesseract rus), стр. 27–29; сверено с изображением страниц";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            SnapshotDto snapshot;

            if (options.LoadOnly && File.Exists(options.SnapshotPath))
            {
                snapshot = await ReadAsync<SnapshotDto>(options.SnapshotPath);
            }
            else
            {
                snapshot = await BuildSnapshotAsync(options);
                await WriteAsync(options.SnapshotPath, snapshot);
                Console.WriteLine($"Снимок: {options.SnapshotPath}");

                if (!options.ParseOnly)
                {
                    await LoadAsync(snapshot, options);
                }
            }

            if (options.LoadOnly)
            {
                if (!File.Exists(options.SnapshotPath))
                {
                    Console.Error.WriteLine($"Снимок не найден: {options.SnapshotPath}");
                    return 2;
                }

                await LoadAsync(snapshot, options);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка: {exception}");
            return 1;
        }
    }

    private static async Task<SnapshotDto> BuildSnapshotAsync(Options options)
    {
        var seed = await ReadAsync<SeedDto>(options.SeedPath);
        Console.WriteLine(
            $"Снимок приложения N 2: {seed.Categories.Count} позиций, {seed.Rules.Count} связей, {seed.RulesVersion}");

        var eoNumber = seed.Source.EoNumber;
        var apiUrl = ApiUrl;
        var pdfUrl = string.Format(CultureInfo.InvariantCulture, PdfUrlTemplate, eoNumber);
        string title = seed.Source.Act;
        DateOnly? documentDate = null;
        DateOnly? publishDate = null;
        int? pagesCount = null;
        byte[] pdf;

        if (options.Offline)
        {
            if (string.IsNullOrWhiteSpace(options.PdfPath) || !File.Exists(options.PdfPath))
            {
                throw new InvalidOperationException("Для --offline нужен --pdf с локальной копией официального PDF");
            }

            pdf = await File.ReadAllBytesAsync(options.PdfPath);
            Console.WriteLine("Офлайн-режим: реквизиты взяты из снимка, PDF — из локального файла");
        }
        else
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(Portal),
                Timeout = TimeSpan.FromSeconds(180),
            };
            http.DefaultRequestHeaders.AcceptEncoding.Clear();
            http.DefaultRequestHeaders.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("identity"));
            var client = new PravoDocumentClient(http);

            var published = await client.FindAsync("2604", "government");
            var document = published.FirstOrDefault(item =>
                               item.EoNumber == eoNumber && item.Number == "2604")
                           ?? published.FirstOrDefault(item => item.Number == "2604")
                           ?? throw new InvalidOperationException(
                               "ПП РФ N 2604 не найдено в API официального опубликования");

            eoNumber = document.EoNumber;
            pdfUrl = document.PdfUrl.StartsWith('/')
                ? Portal + document.PdfUrl
                : document.PdfUrl;
            title = document.Title;
            documentDate = document.DocumentDate;
            publishDate = document.PublishDate;
            pagesCount = document.PagesCount;
            pdf = string.IsNullOrWhiteSpace(options.PdfPath)
                ? await client.DownloadPdfAsync(eoNumber)
                : await File.ReadAllBytesAsync(options.PdfPath);
            Console.WriteLine(
                $"Официальный документ: eoNumber={eoNumber}, страниц={pagesCount}, PDF={pdf.Length} байт");
        }

        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        Console.WriteLine($"PDF SHA-256: {sha}");

        var source = seed.Source with { EoNumber = eoNumber };
        var documentInfo = new SnapshotDocumentDto(
            eoNumber, apiUrl, pdfUrl, sha, title,
            documentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            publishDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            pagesCount, seed.Source.PdfPages, ExtractionMethod);

        return new SnapshotDto(
            documentInfo, seed.RulesVersion, source, seed.Categories, seed.Rules);
    }

    private static async Task LoadAsync(SnapshotDto snapshot, Options options)
    {
        var version = BuildVersion(snapshot);
        Console.WriteLine(
            $"Загрузка версии {version}: {snapshot.Categories.Count} позиций, {snapshot.Rules.Count} связей");

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var document = new NpgsqlCommand(
            """
            INSERT INTO legal.pp2604_documents
                (version, rules_version, eo_number, api_url, pdf_url, pdf_sha256,
                 document_title, document_date, publish_date, pages_count,
                 appendix_pages, extraction_method, source_act, source_appendix,
                 source_portal, source_classifier, row_count, link_count,
                 is_active, imported_at)
            VALUES
                (@version, @rulesVersion, @eoNumber, @apiUrl, @pdfUrl, @sha,
                 @title, CAST(@documentDate AS date), CAST(@publishDate AS date), @pagesCount,
                 @appendixPages, @extractionMethod, @sourceAct, @sourceAppendix,
                 @sourcePortal, @sourceClassifier, @rowCount, @linkCount,
                 true, now())
            ON CONFLICT (version) DO UPDATE SET
                rules_version = EXCLUDED.rules_version,
                pdf_sha256 = EXCLUDED.pdf_sha256,
                document_title = EXCLUDED.document_title,
                document_date = EXCLUDED.document_date,
                publish_date = EXCLUDED.publish_date,
                pages_count = EXCLUDED.pages_count,
                row_count = EXCLUDED.row_count,
                link_count = EXCLUDED.link_count,
                is_active = true,
                imported_at = now()
            """, connection, transaction))
        {
            Add(document, "version", version);
            Add(document, "rulesVersion", snapshot.RulesVersion);
            Add(document, "eoNumber", snapshot.Document.EoNumber);
            Add(document, "apiUrl", snapshot.Document.ApiUrl);
            Add(document, "pdfUrl", snapshot.Document.PdfUrl);
            Add(document, "sha", snapshot.Document.PdfSha256);
            Add(document, "title", snapshot.Document.Title);
            Add(document, "documentDate",
                string.IsNullOrWhiteSpace(snapshot.Document.DocumentDate)
                    ? DBNull.Value
                    : snapshot.Document.DocumentDate);
            Add(document, "publishDate",
                string.IsNullOrWhiteSpace(snapshot.Document.PublishDate)
                    ? DBNull.Value
                    : snapshot.Document.PublishDate);
            Add(document, "pagesCount", snapshot.Document.PagesCount ?? 0);
            Add(document, "appendixPages", snapshot.Document.AppendixPages);
            Add(document, "extractionMethod", snapshot.Document.ExtractionMethod);
            Add(document, "sourceAct", snapshot.Source.Act);
            Add(document, "sourceAppendix", snapshot.Source.Appendix);
            Add(document, "sourcePortal", snapshot.Source.Portal);
            Add(document, "sourceClassifier", snapshot.Source.Classifier);
            Add(document, "rowCount", snapshot.Categories.Count);
            Add(document, "linkCount", snapshot.Rules.Count);
            await document.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(
            "UPDATE legal.pp2604_documents SET is_active = false WHERE version <> @version",
            connection, transaction, ("version", version));
        await ExecuteAsync(
            "DELETE FROM legal.pp2604_categories WHERE document_version = @version",
            connection, transaction, ("version", version));
        await ExecuteAsync(
            "DELETE FROM legal.pp2604_okpd2_links WHERE document_version = @version",
            connection, transaction, ("version", version));

        foreach (var category in snapshot.Categories)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO legal.pp2604_categories
                    (document_version, row_no, category_id, label, appendix_name,
                     min_price, max_expenses, max_non_cost, source_page)
                VALUES
                    (@version, @row, @id, @label, @appendixName,
                     @minPrice, @maxExpenses, @maxNonCost, @sourcePage)
                """, connection, transaction);
            Add(command, "version", version);
            Add(command, "row", category.AppendixRow);
            Add(command, "id", category.Id);
            Add(command, "label", category.Label);
            Add(command, "appendixName", category.AppendixName);
            Add(command, "minPrice", category.MinPrice);
            Add(command, "maxExpenses", (object?)category.MaxExpenses ?? DBNull.Value);
            Add(command, "maxNonCost", category.MaxNonCost);
            Add(command, "sourcePage", 27);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var rule in snapshot.Rules)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO legal.pp2604_okpd2_links
                    (document_version, okpd2_prefix, category_id, row_no, basis,
                     classifier_name, condition_text, needs_review, purpose_key,
                     source_text, sort_order)
                VALUES
                    (@version, @prefix, @category, @row, @basis,
                     @classifierName, @condition, @needsReview, @purpose,
                     @sourceText, @sortOrder)
                """, connection, transaction);
            Add(command, "version", version);
            Add(command, "prefix", rule.Prefix);
            Add(command, "category", rule.Category);
            Add(command, "row", rule.Row);
            Add(command, "basis", rule.Basis);
            Add(command, "classifierName", rule.ClassifierName);
            Add(command, "condition", (object?)rule.Condition ?? DBNull.Value);
            Add(command, "needsReview", rule.NeedsReview ?? false);
            Add(command, "purpose", (object?)rule.Purpose ?? DBNull.Value);
            Add(command, "sourceText",
                $"{snapshot.Source.Act}, {snapshot.Source.Appendix}, поз. {rule.Row}; ОКПД2 {rule.Prefix} — {rule.ClassifierName}");
            Add(command, "sortOrder", rule.SortOrder);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        Console.WriteLine("Загрузка в legal.pp2604_* завершена");
    }

    private static string BuildVersion(SnapshotDto snapshot)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                snapshot.Document.EoNumber,
                snapshot.Document.PdfSha256,
                snapshot.RulesVersion,
                snapshot.Categories,
                snapshot.Rules,
            },
            JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "pp-2604-" + Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }

    private static async Task ExecuteAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static void Add(NpgsqlCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static async Task<T> ReadAsync<T>(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Не удалось прочитать {path}");
    }

    private static async Task WriteAsync<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions) + "\n",
            new UTF8Encoding(false));
    }

    private sealed record SeedDto(
        string RulesVersion,
        SeedSourceDto Source,
        List<CategoryDto> Categories,
        List<RuleDto> Rules);

    private sealed record SeedSourceDto(
        string Act,
        string Appendix,
        string Portal,
        string EoNumber,
        string PdfPages,
        string Classifier);

    private sealed record CategoryDto(
        string Id,
        string Label,
        int AppendixRow,
        string AppendixName,
        int MinPrice,
        int? MaxExpenses,
        int MaxNonCost);

    private sealed record RuleDto(
        string Prefix,
        string Category,
        int Row,
        string Basis,
        string ClassifierName,
        string? Condition,
        bool? NeedsReview,
        string? Purpose,
        int SortOrder);

    private sealed record SnapshotDocumentDto(
        string EoNumber,
        string ApiUrl,
        string PdfUrl,
        string PdfSha256,
        string Title,
        string? DocumentDate,
        string? PublishDate,
        int? PagesCount,
        string AppendixPages,
        string ExtractionMethod);

    private sealed record SnapshotDto(
        SnapshotDocumentDto Document,
        string RulesVersion,
        SeedSourceDto Source,
        List<CategoryDto> Categories,
        List<RuleDto> Rules);

    private sealed class Options
    {
        public string ConnectionString { get; private init; } =
            "Host=localhost;Port=5433;Database=csms_eis;Username=postgres";

        public string SeedPath { get; private init; } = DefaultSeedPath();
        public string SnapshotPath { get; private init; } = DefaultSnapshotPath();
        public string? PdfPath { get; private init; }
        public bool ParseOnly { get; private init; }
        public bool LoadOnly { get; private init; }
        public bool Offline { get; private init; }

        public static Options Parse(string[] args)
        {
            var connection = Environment.GetEnvironmentVariable("CSMS_DB_CONNECTION");
            var seed = DefaultSeedPath();
            var snapshot = DefaultSnapshotPath();
            string? pdf = null;
            var parseOnly = false;
            var loadOnly = false;
            var offline = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--connection" when index + 1 < args.Length:
                        connection = args[++index];
                        break;
                    case "--seed" when index + 1 < args.Length:
                        seed = args[++index];
                        break;
                    case "--snapshot" when index + 1 < args.Length:
                        snapshot = args[++index];
                        break;
                    case "--pdf" when index + 1 < args.Length:
                        pdf = args[++index];
                        break;
                    case "--parse-only":
                        parseOnly = true;
                        break;
                    case "--load-only":
                        loadOnly = true;
                        break;
                    case "--offline":
                        offline = true;
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный аргумент: {args[index]}");
                }
            }

            return new Options
            {
                ConnectionString = string.IsNullOrWhiteSpace(connection)
                    ? "Host=localhost;Port=5433;Database=csms_eis;Username=postgres"
                    : connection,
                SeedPath = seed,
                SnapshotPath = snapshot,
                PdfPath = pdf,
                ParseOnly = parseOnly,
                LoadOnly = loadOnly,
                Offline = offline,
            };
        }

        private static string DefaultSeedPath() =>
            Path.Combine(AppContext.BaseDirectory, "pp2604-seed.json");

        private static string DefaultSnapshotPath() =>
            Path.Combine(Path.GetTempPath(), "opencode", "pp2604-snapshot.json");
    }
}
