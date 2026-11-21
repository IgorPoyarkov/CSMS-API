using System.Text.Json;
using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Индекс установленной выгрузки ст. 19.28 КоАП в памяти процесса. Файлы лежат
/// в &lt;root&gt;/current, а результат поиска не зависит от PostgreSQL: снимок
/// небольшой (около тысячи строк) и переживает перезапуск через entries.json.
/// </summary>
public sealed class EisCounterpartyCatalog : ICounterpartyScreeningCatalog
{
    private const string ManifestName = ".manifest.json";
    private const string EntriesName = "entries.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private CounterpartyScreeningSource? _source;
    private IReadOnlyList<CounterpartyScreeningEntry> _entries = [];

    public EisCounterpartyCatalog(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "counterparties")
            : Path.GetFullPath(rootDirectory);

        Load(root);
    }

    public CounterpartyScreeningSource? Source
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    public void Replace(
        CounterpartyScreeningSource source,
        IReadOnlyList<CounterpartyScreeningEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _source = source;
            _entries = entries.ToArray();
        }
    }

    public CounterpartyScreeningResult Check(string? inn, string? ogrn, string? kpp)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var normalizedInn = Normalize(inn);
        var normalizedOgrn = Normalize(ogrn);
        var normalizedKpp = Normalize(kpp);

        lock (_gate)
        {
            var source = _source;
            if (_entries.Count == 0)
            {
                return new CounterpartyScreeningResult(false, [], source, checkedAt);
            }

            // ИНН и ОГРН — сильные реквизиты: если они заданы, КПП не должен
            // раздувать выдачу совпадениями по общей функции многих юрлиц.
            var hasStrong = normalizedInn.Length > 0 || normalizedOgrn.Length > 0;

            var matches = _entries
                .Where(entry =>
                    (hasStrong &&
                        ((normalizedInn.Length > 0 && Normalize(entry.Inn) == normalizedInn) ||
                         (normalizedOgrn.Length > 0 && Normalize(entry.Ogrn) == normalizedOgrn))) ||
                    (!hasStrong && normalizedKpp.Length > 0 && Normalize(entry.Kpp) == normalizedKpp))
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();

            return new CounterpartyScreeningResult(matches.Length > 0, matches, source, checkedAt);
        }
    }

    private void Load(string root)
    {
        var current = Path.Combine(root, "current");
        var manifestPath = Path.Combine(current, ManifestName);
        var entriesPath = Path.Combine(current, EntriesName);

        if (!File.Exists(manifestPath) || !File.Exists(entriesPath))
        {
            return;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<EisCounterpartyManifest>(
                File.ReadAllText(manifestPath), JsonOptions);
            var entries = JsonSerializer.Deserialize<List<CounterpartyScreeningEntry>>(
                File.ReadAllText(entriesPath), JsonOptions);

            if (manifest is null || entries is null || entries.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                _source = manifest.ToSource();
                _entries = entries;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException or ArgumentException)
        {
            // Битый локальный снимок не должен мешать старту API: загрузчик
            // установит свежую выгрузку при первой же успешной проверке.
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}
