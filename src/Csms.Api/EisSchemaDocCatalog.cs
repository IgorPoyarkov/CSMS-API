using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Csms.Infrastructure;
using EisDocGenerator;

namespace Csms.Api;

/// <summary>
/// Каталог документации, который бэкенд держит в памяти и отдаёт фронту.
///
/// Строится из установленных XSD тем же <see cref="SchemaReader"/>, что и
/// генератор для репозитория фронтенда. После автообновления схем каталог
/// пересобирается, хеш меняется, и фронт получает SSE-событие с diff.
/// </summary>
public sealed class EisSchemaDocCatalog
{
    private const string ManifestName = ".manifest.json";

    // Те же настройки, что и в tools/EisDocGenerator: фронт читает JSON как есть.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, SchemaCatalog> _history = new(StringComparer.Ordinal);
    private SchemaCatalog? _current;
    private byte[] _json = [];
    private string _hash = string.Empty;

    public EisSchemaDocCatalog(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "schemas")
            : Path.GetFullPath(rootDirectory);
    }

    public string Hash
    {
        get { lock (_gate) { return _hash; } }
    }

    public string? Version
    {
        get { lock (_gate) { return _current?.Version; } }
    }

    public DateTimeOffset? GeneratedAt
    {
        get { lock (_gate) { return _current?.GeneratedAt; } }
    }

    public byte[] Json
    {
        get { lock (_gate) { return _json; } }
    }

    /// <summary>Загрузить каталог последней установленной версии при старте API.</summary>
    public void Initialize()
    {
        var version = FindLatestVersion();
        if (version is null)
        {
            return;
        }

        lock (_gate)
        {
            Apply(Read(version));
        }
    }

    /// <summary>Пересобрать каталог после установки новой версии XSD.</summary>
    public EisSchemaUpdateEvent? Regenerate(string version)
    {
        var next = Read(version);

        lock (_gate)
        {
            var previous = _current;
            var previousHash = _hash;
            Apply(next);

            return EisSchemaUpdateEvent.Diff(previous, next, previousHash, _hash);
        }
    }

    /// <summary>
    /// Изменения относительно присланного фронтом хеша. Если хеш неизвестен
    /// (например, после перезапуска API), отдаём полный список как «добавленные».
    /// </summary>
    public EisSchemaUpdateEvent Changes(string? fromHash)
    {
        lock (_gate)
        {
            var requested = string.IsNullOrEmpty(fromHash) ? "unknown" : fromHash;

            if (_current is null)
            {
                return EisSchemaUpdateEvent.Diff(null, EmptyCatalog(), requested, _hash);
            }

            var previous = _history.TryGetValue(requested, out var found) ? found : null;

            return EisSchemaUpdateEvent.Diff(previous, _current, requested, _hash);
        }
    }

    private SchemaCatalog Read(string version)
    {
        var directory = Path.Combine(_root, version);
        var manifestPath = Path.Combine(directory, ManifestName);
        var manifest = JsonSerializer.Deserialize<EisSchemaManifest>(
            File.ReadAllText(manifestPath), ManifestOptions)
            ?? throw new InvalidOperationException($"Не удалось прочитать манифест XSD: {manifestPath}");

        return new SchemaReader().Read(directory, version, manifest.SourceId, manifest.Sha256);
    }

    private void Apply(SchemaCatalog catalog)
    {
        var bytes = Utf8.GetBytes(JsonSerializer.Serialize(catalog, JsonOptions));
        _json = bytes;
        _hash = Convert.ToHexString(SHA256.HashData(bytes));
        _current = catalog;
        _history[_hash] = catalog;

        // Держим немного истории, чтобы фронт после перезапуска API всё ещё
        // мог получить осмысленный diff по старому хешу.
        while (_history.Count > 5)
        {
            var oldest = _history.Keys.FirstOrDefault(key => key != _hash);
            if (oldest is null)
            {
                break;
            }

            _history.TryRemove(oldest, out _);
        }
    }

    private string? FindLatestVersion()
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(_root)
            .Where(directory => File.Exists(Path.Combine(directory, ManifestName)))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static SchemaCatalog EmptyCatalog() => new(
        "unknown", "unknown", "unknown", DateTimeOffset.UtcNow, 0, [], []);
}
