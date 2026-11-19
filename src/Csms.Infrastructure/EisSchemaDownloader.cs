using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure;

/// <summary>
/// Автообновление XSD Альбома форматов ЕИС.
///
/// ЕИС публикует архивы «Интеграционные схемы ЕИС версии X.Y» на публичной
/// странице документов. Ссылка на файл содержит id, который меняется с каждой
/// версией, поэтому сначала читается страница, затем скачивается нужный ZIP.
///
/// Архив не считается установленным, пока не пройдёт три проверки: размер,
/// безопасность путей и компиляция всех XSD в XmlSchemaSet. Старая версия
/// остаётся на месте до последнего момента — сеть и чужие файлы не должны
/// оставлять шлюз без схем.
/// </summary>
public sealed class EisSchemaDownloader(
    HttpClient http,
    EisSchemaSourceOptions options,
    EisSchemaUpdateStatus status,
    ILogger<EisSchemaDownloader> logger,
    string? rootDirectory = null,
    Func<string, CancellationToken, Task>? onInstalled = null)
{
    private const string ManifestName = ".manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Ссылка и заголовок в разметке ЕИС идут в одном теге <a>, между ними
    // могут быть переносы строк и лишние пробелы.
    private static readonly Regex ArchiveLink = new(
        "<a\\b[^>]*href=\"(?<href>[^\"]*downloadDocument\\.html\\?id=(?<id>\\d+)[^\"]*)\"[^>]*>" +
        "(?<text>[\\s\\S]{0,400}?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VersionText = new(
        "Интеграционные\\s+схемы\\s+ЕИС\\s+версии\\s+(?<version>[\\d.]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _root = string.IsNullOrWhiteSpace(rootDirectory)
        ? Path.Combine(AppContext.BaseDirectory, "schemas")
        : Path.GetFullPath(rootDirectory);

    // Один обновлятор на процесс: параллельные проверки могли бы одновременно
    // распаковать и подменить одну и ту же папку версии.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Проверить страницу ЕИС и, если вышла новая версия, установить её.</summary>
    public async Task<EisSchemaStatusSnapshot> CheckAndInstallAsync(
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
            });

            var listing = await http.GetStringAsync(options.ListingUrl, cancellationToken)
                .ConfigureAwait(false);
            var archive = FindLatestArchive(listing);

            if (archive is null)
            {
                throw new InvalidOperationException(
                    "На странице документов ЕИС не найден архив «Интеграционные схемы ЕИС версии X.Y»: " +
                    "проверьте ListingUrl и разметку страницы.");
            }

            var version = NormalizeVersion(archive.Value.Version);

            // id документа в ЕИС меняется при выходе новой редакции. Если он
            // совпадает с установленным, архив не скачиваем повторно — это
            // экономит трафик и убирает лишнюю зависимость от сети.
            var installed = FindManifest(version, archive.Value.Id);
            if (installed is not null)
            {
                var upToDate = status.Snapshot() with
                {
                    State = options.AutoPromote ? "UpToDate" : "Staged",
                    Version = installed.Version,
                    SourceId = installed.SourceId,
                    Sha256 = installed.Sha256,
                    FileCount = installed.FileCount,
                    LastCheckAt = DateTimeOffset.UtcNow,
                    LastError = null,
                };
                status.Set(upToDate);

                return upToDate;
            }

            var downloadUrl = options.DownloadUrlPrefix + archive.Value.Id;
            var bytes = await DownloadAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));

            var fileCount = await Install(
                bytes, version, archive.Value.Id, downloadUrl, sha256, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var state = options.AutoPromote ? "Installed" : "Staged";
            var done = status.Snapshot() with
            {
                State = state,
                Version = version,
                SourceId = archive.Value.Id.ToString(),
                Sha256 = sha256,
                FileCount = fileCount,
                LastCheckAt = now,
                LastSuccessAt = now,
                LastError = null,
            };
            status.Set(done);
            logger.LogInformation(
                "XSD ЕИС версии {Version}: {State} (id={SourceId}, файлов {FileCount}, SHA-256 {Sha256})",
                version, state, archive.Value.Id, fileCount, sha256[..12]);

            return done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Ошибка сети или чужого архива не выбрасывается наружу: фоновый
            // воркер продолжает работать, а активные схемы остаются прежними.
            var failed = status.Snapshot() with
            {
                State = "Error",
                LastCheckAt = DateTimeOffset.UtcNow,
                LastError = exception.Message,
            };
            status.Set(failed);
            logger.LogWarning(exception, "Не удалось обновить XSD ЕИС; остаётся предыдущая версия схем");

            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Выбрать самую новую версию из страницы документов.</summary>
    private static (long Id, string Version)? FindLatestArchive(string html)
    {
        (long Id, string Version)? best = null;
        Version? bestNumber = null;

        foreach (Match link in ArchiveLink.Matches(html))
        {
            var title = VersionText.Match(link.Groups["text"].Value);
            if (!title.Success ||
                !long.TryParse(link.Groups["id"].Value, out var id))
            {
                continue;
            }

            var version = NormalizeVersion(title.Groups["version"].Value);
            var number = ParseVersion(version);

            // У одной версии могут быть несколько редакций с разными id —
            // берём самый свежий документ (у ЕИС id растёт со временем).
            if (best is null || IsNewer(number, id, bestNumber, best.Value.Id, best.Value.Version, version))
            {
                best = (id, version);
                bestNumber = number;
            }
        }

        return best;
    }

    private static bool IsNewer(
        Version? candidateNumber,
        long candidateId,
        Version? currentNumber,
        long currentId,
        string currentVersion,
        string candidateVersion)
    {
        if (candidateNumber is not null && currentNumber is not null)
        {
            var byVersion = candidateNumber.CompareTo(currentNumber);

            return byVersion != 0 ? byVersion > 0 : candidateId > currentId;
        }

        var order = string.CompareOrdinal(candidateVersion, currentVersion);

        return order != 0 ? order > 0 : candidateId > currentId;
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value.Trim().TrimEnd('.'), out var version) ? version : null;

    private static string NormalizeVersion(string value)
    {
        var version = value.Trim().TrimEnd('.');

        if (version.Length == 0 || version.Length > 40 ||
            version.Any(symbol => !(char.IsLetterOrDigit(symbol) || symbol is '.' or '-' or '_')))
        {
            throw new InvalidOperationException($"Небезопасный номер версии XSD ЕИС: «{value}».");
        }

        return version;
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(options.ListingUrl);

        using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ЕИС вернула {(int)response.StatusCode} при скачивании архива схем: {url}");
        }

        if (response.Content.Headers.ContentLength is > 0 &&
            response.Content.Headers.ContentLength > options.MaxArchiveBytes)
        {
            throw new InvalidOperationException(
                $"Архив схем слишком большой: {response.Content.Headers.ContentLength} байт " +
                $"(лимит {options.MaxArchiveBytes}).");
        }

        using var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0 || bytes.Length > options.MaxArchiveBytes)
        {
            throw new InvalidOperationException("Скачанный архив схем пуст или превышает лимит размера.");
        }

        // ZIP начинается с "PK". Раньше, чем распаковывать, убеждаемся, что
        // ЕИС (или прокси) не вернул HTML-страницу с ошибкой.
        if (bytes.Length < 2 || bytes[0] != 0x50 || bytes[1] != 0x4B)
        {
            throw new InvalidOperationException("ЕИС вернула не ZIP-архив — обновление пропущено.");
        }

        return bytes;
    }

    /// <summary>Безопасная распаковка и атомарная подмена каталога версии.</summary>
    private async Task<int> Install(
        byte[] archiveBytes,
        string version,
        long sourceId,
        string sourceUrl,
        string sha256,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(
            _root, ".incoming", version + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var count = 0;
            long total = 0;

            using (var stream = new MemoryStream(archiveBytes))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entry.FullName.EndsWith('/') ||
                        !entry.FullName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.Length > options.MaxExtractedBytes - total)
                    {
                        throw new InvalidOperationException("Распакованные XSD превышают лимит размера.");
                    }

                    // Первый сегмент — папка архива с названием версии; она
                    // может быть в cp866, поэтому берём только вложенные пути.
                    var relative = RelativePath(entry.FullName);
                    if (relative is null)
                    {
                        continue;
                    }

                    if (++count > options.MaxEntries)
                    {
                        throw new InvalidOperationException(
                            $"В архиве слишком много XSD (больше {options.MaxEntries}) — обновление пропущено.");
                    }

                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Архив содержит выход за каталог схем: {entry.FullName}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var source = entry.Open();
                    using var buffer = new MemoryStream();
                    source.CopyTo(buffer);
                    var (content, patched) = FixKnownEisDefect(buffer.ToArray());

                    if (patched)
                    {
                        logger.LogWarning(
                            "В XSD {File} из архива ЕИС убран лишний '>' после XML-декларации",
                            entry.Name);
                    }

                    using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                    file.Write(content);
                    total += content.Length;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (count == 0)
            {
                throw new InvalidOperationException("В архиве ЕИС не найдено ни одной XSD.");
            }

            var errors = EisSchemaCatalog.CheckDirectory(staging);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Скачанные XSD не компилируются: " + string.Join("; ", errors.Take(3)));
            }

            var manifest = new EisSchemaManifest(
                version, sourceId.ToString(), sha256, count, DateTimeOffset.UtcNow, sourceUrl);
            File.WriteAllText(
                Path.Combine(staging, ManifestName),
                JsonSerializer.Serialize(manifest, JsonOptions));

            if (options.AutoPromote)
            {
                Promote(staging, version);

                // Каталог документации и SSE — надстройка над XSD. Если она
                // упала, схемы уже установлены и остаются активными: не надо
                // превращать ошибку генератора в «обновление не удалось».
                if (onInstalled is not null)
                {
                    try
                    {
                        await onInstalled(version, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            exception,
                            "XSD версии {Version} установлены, но каталог документации не обновлён",
                            version);
                    }
                }
            }
            else
            {
                CleanupStaged(version, staging);
            }

            return count;
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

    /// <summary>
    /// В официальном архиве 17.0 файл getDocsLE-ws-api.xsd начинается с
    /// «&lt;?xml … ?&gt;&gt;» — лишний символ ломает XmlReader. Убираем ровно
    /// этот символ в начале файла и громко пишем в лог: молча править схемы
    /// нельзя, даже если дефект известен.
    /// </summary>
    private static (byte[] Content, bool Patched) FixKnownEisDefect(byte[] content)
    {
        var limit = Math.Min(content.Length - 2, 128);

        for (var index = 0; index < limit; index++)
        {
            if (content[index] == (byte)'?' &&
                content[index + 1] == (byte)'>' &&
                content[index + 2] == (byte)'>')
            {
                var fixedContent = new byte[content.Length - 1];
                Array.Copy(content, 0, fixedContent, 0, index + 2);
                Array.Copy(content, index + 3, fixedContent, index + 2, content.Length - index - 3);

                return (fixedContent, true);
            }
        }

        return (content, false);
    }

    /// <summary>Относительный путь внутри каталога версии или null для мусора.</summary>
    private static string? RelativePath(string entryName)
    {
        var parts = entryName.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2 || parts.Any(part => part == ".." || part == "." || part.Contains(':')))
        {
            return null;
        }

        return Path.Combine(parts.Skip(1).ToArray());
    }

    private void Promote(string staging, string version)
    {
        var target = Path.Combine(_root, version);
        var backup = Path.Combine(_root, version + ".previous");

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

        CleanupStaged(version, target);
    }

    /// <summary>Манифест активной или подготовленной версии схем.</summary>
    private EisSchemaManifest? FindManifest(string version, long? sourceId = null)
    {
        var candidates = new List<string> { Path.Combine(_root, version, ManifestName) };
        var incoming = Path.Combine(_root, ".incoming");

        if (Directory.Exists(incoming))
        {
            candidates.AddRange(Directory.EnumerateDirectories(incoming, version + "-*")
                .Select(directory => Path.Combine(directory, ManifestName)));
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<EisSchemaManifest>(
                    File.ReadAllText(path), JsonOptions);

                if (manifest is not null &&
                    (sourceId is null ||
                     string.Equals(manifest.SourceId, sourceId.Value.ToString(), StringComparison.Ordinal)))
                {
                    return manifest;
                }
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Манифест XSD {Path} повреждён — скачаем заново", path);
            }
        }

        return null;
    }

    /// <summary>Убрать устаревшие подготовленные архивы той же версии.</summary>
    private void CleanupStaged(string version, string keep)
    {
        var incoming = Path.Combine(_root, ".incoming");
        if (!Directory.Exists(incoming))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(incoming, version + "-*"))
        {
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Не удалось удалить старую подготовку XSD {Path}", directory);
            }
        }
    }
}
