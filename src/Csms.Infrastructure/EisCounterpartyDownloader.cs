using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Csms.Application;
using Csms.Domain;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure;

/// <summary>
/// Ежедневная проверка публичной страницы ЕИС (sectionId=2369) и установка
/// выгрузки о привлечении по ст. 19.28 КоАП. Скачиваем файл при каждой проверке,
/// чтобы сравнивать и id документа, и SHA-256; парсим только при изменении
/// любого из них. Активный снимок подменяется atomic-переносом каталога.
/// </summary>
public sealed class EisCounterpartyDownloader(
    HttpClient http,
    EisCounterpartySourceOptions options,
    EisCounterpartyUpdateStatus status,
    ICounterpartyScreeningCatalog catalog,
    ILogger<EisCounterpartyDownloader> logger,
    string? rootDirectory = null)
{
    private const string ManifestName = ".manifest.json";
    private const string EntriesName = "entries.json";
    private const string DocumentName = "document.xlsx";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Карточка выгрузки ЕИС: дата, иконка XLSX и ссылка на скачивание лежат
    // в одном <article class="docs-group">. Проверка заголовка со ст. 19.28
    // не даёт установить случайный документ из другой секции.
    private static readonly Regex DocumentBlock = new(
        "<article\\s+class=\"docs-group\"[^>]*>(?<body>[\\s\\S]*?)</article>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DownloadLink = new(
        "downloadDocument\\.html\\?id=(?<id>\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DateText = new(
        "class=\"docs-date\"[^>]*>(?<date>[^<]+)</span>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LiabilityTitle = new(
        "19\\.28\\s+КоАП", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant);

    private readonly string _root = string.IsNullOrWhiteSpace(rootDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "counterparties")
        : Path.GetFullPath(rootDirectory);

    // Один обновлятор на процесс: параллельные проверки могли бы одновременно
    // писать в .incoming и подменять каталог current.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Проверить ЕИС и, при изменении выгрузки, установить её.</summary>
    public async Task<EisCounterpartyStatusSnapshot> CheckAndInstallAsync(
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            var disabled = status.Snapshot() with { State = "Disabled" };
            status.Set(disabled);

            return disabled;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            status.Set(status.Snapshot() with
            {
                State = "Checking",
                LastCheckAt = DateTimeOffset.UtcNow,
                LastError = null,
            });

            var listing = await http.GetStringAsync(options.ListingUrl, cancellationToken)
                .ConfigureAwait(false);
            var document = FindLatestDocument(listing)
                ?? throw new InvalidOperationException(
                    "На странице документов ЕИС не найдена выгрузка «19.28 КоАП» " +
                    "в формате XLSX: проверьте EisCounterparties:Update:ListingUrl.");

            var bytes = await DownloadAsync(document.Id, cancellationToken)
                .ConfigureAwait(false);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var active = catalog.Source;

            if (active is not null &&
                string.Equals(active.SourceId, document.Id, StringComparison.Ordinal) &&
                string.Equals(active.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                var upToDate = status.Snapshot() with
                {
                    State = "UpToDate",
                    SourceId = document.Id,
                    PublishedDate = document.PublishedDate,
                    Sha256 = sha256,
                    RowCount = active.RowCount,
                    LastCheckAt = DateTimeOffset.UtcNow,
                    LastError = null,
                    ParsedNow = false,
                };
                status.Set(upToDate);

                return upToDate;
            }

            var entries = EisCounterpartyXlsxParser.Parse(bytes);
            if (entries.Count < options.MinRowCount)
            {
                throw new InvalidOperationException(
                    $"Выгрузка ЕИС содержит {entries.Count} записей — меньше порога " +
                    $"{options.MinRowCount}. Похоже, файл урезан, установка пропущена.");
            }

            var downloadUrl = options.DownloadUrlPrefix + document.Id;
            var manifest = new EisCounterpartyManifest(
                document.Id,
                document.PublishedDate,
                sha256,
                bytes.Length,
                entries.Count,
                DateTimeOffset.UtcNow,
                downloadUrl);

            Install(bytes, entries, manifest);
            catalog.Replace(manifest.ToSource(), entries);

            var installed = status.Snapshot() with
            {
                State = "Installed",
                SourceId = document.Id,
                PublishedDate = document.PublishedDate,
                Sha256 = sha256,
                RowCount = entries.Count,
                LastCheckAt = DateTimeOffset.UtcNow,
                LastSuccessAt = DateTimeOffset.UtcNow,
                LastError = null,
                ParsedNow = true,
            };
            status.Set(installed);

            logger.LogInformation(
                "Выгрузка ст. 19.28 КоАП (id={SourceId}, {RowCount} записей, SHA-256 {Sha256}) установлена",
                document.Id, entries.Count, sha256[..12]);

            return installed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Ошибка сети или чужого файла не выбрасывается наружу: воркер живёт,
            // а активный снимок в каталоге остаётся прежним.
            var failed = status.Snapshot() with
            {
                State = "Error",
                LastCheckAt = DateTimeOffset.UtcNow,
                LastError = exception.Message,
                ParsedNow = false,
            };
            status.Set(failed);
            logger.LogWarning(
                exception, "Не удалось обновить выгрузку ст. 19.28 КоАП; остаётся предыдущий снимок");

            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> DownloadAsync(string sourceId, CancellationToken cancellationToken)
    {
        var url = options.DownloadUrlPrefix + sourceId;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(options.ListingUrl);

        using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ЕИС вернула {(int)response.StatusCode} при скачивании выгрузки ст. 19.28 КоАП: {url}");
        }

        if (response.Content.Headers.ContentLength is > 0 &&
            response.Content.Headers.ContentLength > options.MaxFileBytes)
        {
            throw new InvalidOperationException(
                $"Выгрузка ЕИС слишком большая: {response.Content.Headers.ContentLength} байт " +
                $"(лимит {options.MaxFileBytes}).");
        }

        using var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0 || bytes.Length > options.MaxFileBytes)
        {
            throw new InvalidOperationException(
                "Скачанная выгрузка ЕИС пуста или превышает лимит размера.");
        }

        return bytes;
    }

    private static (string Id, string PublishedDate)? FindLatestDocument(string html)
    {
        foreach (Match block in DocumentBlock.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var id = DownloadLink.Match(body);
            var date = DateText.Match(body);

            if (!id.Success || !date.Success ||
                !body.Contains("xlsx.svg", StringComparison.OrdinalIgnoreCase) ||
                !LiabilityTitle.IsMatch(body))
            {
                continue;
            }

            var published = WhiteSpace.Replace(date.Groups["date"].Value.Trim(), " ");
            if (published.Length is 0 or > 80)
            {
                continue;
            }

            return (id.Groups["id"].Value, published);
        }

        return null;
    }

    private void Install(
        byte[] bytes,
        IReadOnlyList<CounterpartyScreeningEntry> entries,
        EisCounterpartyManifest manifest)
    {
        var staging = Path.Combine(
            _root, ".incoming", manifest.SourceId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            File.WriteAllBytes(Path.Combine(staging, DocumentName), bytes);
            File.WriteAllText(
                Path.Combine(staging, EntriesName), JsonSerializer.Serialize(entries, JsonOptions));
            File.WriteAllText(
                Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));

            Promote(staging);
            CleanupIncoming();
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    /// <summary>Атомарная подмена current; при сбое возвращается backup.</summary>
    private void Promote(string staging)
    {
        var target = Path.Combine(_root, "current");
        var backup = Path.Combine(_root, "current.previous");

        Directory.CreateDirectory(_root);

        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        if (Directory.Exists(target))
        {
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }

        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }
    }

    private void CleanupIncoming()
    {
        var incoming = Path.Combine(_root, ".incoming");
        if (!Directory.Exists(incoming))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(incoming))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Оставшийся мусор .incoming не влияет на активный снимок.
            }
        }
    }
}
