using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;
using Csms.Application;

namespace Csms.Infrastructure;

/// <summary>
/// Каталог схем ЕИС на диске: <c>schemas/&lt;версия&gt;/*.xsd</c>.
///
/// Пока официального Альбома форматов у нас нет, каталог честно сообщает
/// <c>HasSchema == false</c> и не делает вид, что XML проверен. Когда схемы
/// появятся, достаточно положить их в папку версии — код менять не нужно.
/// </summary>
public sealed class EisSchemaCatalog : IEisSchemaCatalog
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Lazy<SchemaBundle>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public EisSchemaCatalog(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "schemas")
            : Path.GetFullPath(rootDirectory);
    }

    /// <summary>
    /// Сбросить кеш версии после автообновления: каталог мог прочитать старые
    /// файлы до подмены каталога и продолжал бы выдавать их до перезапуска.
    /// </summary>
    public void Invalidate(string schemaVersion)
    {
        if (!string.IsNullOrWhiteSpace(schemaVersion))
        {
            _cache.TryRemove(schemaVersion.Trim(), out _);
        }
    }

    /// <summary>
    /// Проверить, компилируется ли каталог XSD целиком. Используется
    /// автообновлением до подмены активных схем: бракованный архив не должен
    /// выбить шлюз из рабочего состояния.
    /// </summary>
    public static IReadOnlyList<string> CheckDirectory(string directory) => Check(directory).Errors;

    public bool HasSchema(string schemaVersion)
    {
        var directory = GetDirectory(schemaVersion);

        return directory is not null &&
            Directory.EnumerateFiles(directory, "*.xsd", SearchOption.AllDirectories).Any();
    }

    public IReadOnlyList<string> Validate(string xml, string schemaVersion)
    {
        var directory = GetDirectory(schemaVersion);

        if (directory is null)
        {
            return [];
        }

        var bundle = _cache.GetOrAdd(
            schemaVersion.Trim(),
            _ => new Lazy<SchemaBundle>(() => Load(directory), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        if (bundle.Errors.Count > 0 || bundle.Set is null)
        {
            return bundle.Errors;
        }

        var errors = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = bundle.Set,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        settings.ValidationFlags |= XmlSchemaValidationFlags.ReportValidationWarnings;
        settings.ValidationEventHandler += (_, args) => errors.Add(Describe(args));

        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);

            while (reader.Read())
            {
            }
        }
        catch (XmlException error)
        {
            errors.Add($"Некорректный XML: {error.Message}");
        }

        return errors;
    }

    /// <summary>Путь к папке версии. Не даём версии выйти за пределы каталога.</summary>
    private string? GetDirectory(string schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return null;
        }

        var version = schemaVersion.Trim();

        if (version.Any(symbol => !(char.IsLetterOrDigit(symbol) || symbol is '.' or '-' or '_')))
        {
            return null;
        }

        var root = Path.GetFullPath(_root);
        var path = Path.GetFullPath(Path.Combine(root, version));

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(path)
                ? path
                : null;
    }

    private static SchemaBundle Load(string directory) => Check(directory);

    private static SchemaBundle Check(string directory)
    {
        var errors = new List<string>();
        var set = new XmlSchemaSet { XmlResolver = null };

        foreach (var file in Directory.EnumerateFiles(directory, "*.xsd", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });

                set.Add(null, reader);
            }
            catch (XmlSchemaException error)
            {
                errors.Add($"{Path.GetFileName(file)}: {error.Message}");
            }
            catch (XmlException error)
            {
                errors.Add($"{Path.GetFileName(file)}: {error.Message}");
            }
        }

        if (errors.Count == 0)
        {
            try
            {
                set.Compile();
            }
            catch (XmlSchemaException error)
            {
                errors.Add($"Ошибка компиляции XSD: {error.Message}");
            }
        }

        return new SchemaBundle(errors.Count == 0 ? set : null, errors);
    }

    private static string Describe(ValidationEventArgs args)
    {
        var position = args.Exception.LineNumber > 0
            ? $" [строка {args.Exception.LineNumber}, позиция {args.Exception.LinePosition}]"
            : string.Empty;

        return $"XSD ({args.Severity}): {args.Message}{position}";
    }

    private sealed record SchemaBundle(XmlSchemaSet? Set, IReadOnlyList<string> Errors);
}
