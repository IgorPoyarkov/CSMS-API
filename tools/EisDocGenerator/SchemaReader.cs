using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace EisDocGenerator;

/// <summary>
/// Читает Альбом XSD через XmlSchemaSet и строит плоский каталог определений.
/// Никаких генерированных C#-классов: только описания, из которых потом
/// получаются TypeScript-типы и HTML-обозреватель.
/// </summary>
public sealed class SchemaReader
{
    private readonly XmlSchemaSet _set = new() { XmlResolver = null };
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    // Ключ — тот же объект XmlSchema*, что вернул XmlSchemaSet после Compile().
    // Это важно: ссылки из полей типов указывают именно на эти экземпляры.
    private readonly Dictionary<XmlSchemaType, string> _typeNames = new();
    private readonly Dictionary<XmlSchemaElement, string> _elementNames = new();
    private readonly Dictionary<XmlQualifiedName, XmlSchemaAttributeGroup> _attributeGroups = new();
    private readonly Dictionary<XmlQualifiedName, XmlSchemaGroup> _groups = new();
    private readonly List<SchemaDefinition> _definitions = [];
    private readonly Queue<(XmlSchemaType Type, string Name)> _pending = new();

    public SchemaCatalog Read(
        string xsdDirectory,
        string version,
        string sourceId,
        string sha256)
    {
        var files = Directory
            .EnumerateFiles(xsdDirectory, "*.xsd", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });

            _set.Add(null, reader);
        }

        _set.Compile();

        // XmlSchemaSet не публикует сводные таблицы групп — собираем их сами
        // из отдельных схем, уже разрешивших refs при компиляции.
        foreach (XmlSchema schema in _set.Schemas())
        {
            foreach (DictionaryEntry entry in schema.AttributeGroups)
            {
                if (entry.Value is XmlSchemaAttributeGroup group)
                {
                    _attributeGroups[(XmlQualifiedName)entry.Key] = group;
                }
            }

            foreach (DictionaryEntry entry in schema.Groups)
            {
                if (entry.Value is XmlSchemaGroup group)
                {
                    _groups[(XmlQualifiedName)entry.Key] = group;
                }
            }
        }

        // Сначала резервируем ВСЕ имена. Иначе поле, ссылающееся на тип,
        // который встретится позже, получит другое имя, чем само определение.
        foreach (DictionaryEntry entry in _set.GlobalTypes)
        {
            if (entry.Value is XmlSchemaType type && !IsBuiltIn(type.QualifiedName))
            {
                _typeNames[type] = Reserve(type.QualifiedName);
            }
        }

        foreach (DictionaryEntry entry in _set.GlobalElements)
        {
            if (entry.Value is XmlSchemaElement element)
            {
                _elementNames[element] = Reserve(element.QualifiedName, "El_");
            }
        }

        foreach (var (type, name) in _typeNames.ToArray())
        {
            _definitions.Add(Build(type, name));
        }

        foreach (var (element, name) in _elementNames)
        {
            _definitions.Add(BuildElement(element, name));
        }

        while (_pending.Count > 0)
        {
            var (type, name) = _pending.Dequeue();
            _definitions.Add(Build(type, name));
        }

        var ordered = _definitions
            .OrderBy(definition => definition.Namespace, StringComparer.Ordinal)
            .ThenBy(definition => definition.Name, StringComparer.Ordinal)
            .ToList();

        var namespaces = ordered
            .GroupBy(definition => definition.Namespace)
            .Select(group => new NamespaceInfo(
                group.Key,
                AliasFor(group.Key),
                group.Count()))
            .OrderBy(info => info.Uri, StringComparer.Ordinal)
            .ToList();

        return new SchemaCatalog(
            version, sourceId, sha256, DateTimeOffset.UtcNow, files.Length,
            namespaces, ordered);
    }

    private static bool IsBuiltIn(XmlQualifiedName name) =>
        name.Namespace == XmlSchema.Namespace;

    private string AliasFor(string ns)
    {
        if (_aliases.TryGetValue(ns, out var existing))
        {
            return existing;
        }

        var withoutHost = ns
            .Replace("http://", string.Empty, StringComparison.Ordinal)
            .Replace("https://", string.Empty, StringComparison.Ordinal);

        var parts = withoutHost.Split(
            '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var alias = string.Concat(parts
            .Where(part => !part.Contains('.'))
            .Select(Sanitize));

        if (alias.Length == 0 && parts.Length > 0)
        {
            alias = Sanitize(parts[^1]);
        }

        if (alias.Length == 0)
        {
            alias = ns.Length == 0 ? "NoNs" : "Ns";
        }

        if (char.IsDigit(alias[0]))
        {
            alias = "Ns" + alias;
        }

        var candidate = alias;
        var suffix = 2;

        while (_usedAliases.Contains(candidate))
        {
            candidate = alias + suffix++;
        }

        _usedAliases.Add(candidate);
        _aliases[ns] = candidate;

        return candidate;
    }

    private string Reserve(XmlQualifiedName name, string prefix = "")
    {
        var candidate = AliasFor(name.Namespace) + "_" + prefix + Sanitize(name.Name);

        if (_usedNames.Add(candidate))
        {
            return candidate;
        }

        var suffix = 2;

        while (!_usedNames.Add(candidate + "_" + suffix))
        {
            suffix++;
        }

        return candidate + "_" + suffix;
    }

    private string ReserveAnonymous(string preferred)
    {
        var candidate = Sanitize(preferred);

        if (_usedNames.Add(candidate))
        {
            return candidate;
        }

        var suffix = 2;

        while (!_usedNames.Add(candidate + "_" + suffix))
        {
            suffix++;
        }

        return candidate + "_" + suffix;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var symbol in value)
        {
            builder.Append(char.IsLetterOrDigit(symbol) ? symbol : '_');
        }

        var result = Regex.Replace(builder.ToString(), "_+", "_").Trim('_');

        if (result.Length == 0)
        {
            result = "X";
        }

        if (char.IsDigit(result[0]))
        {
            result = "X" + result;
        }

        return result;
    }

    private SchemaDefinition Build(XmlSchemaType type, string tsName) => type switch
    {
        XmlSchemaComplexType complex => new SchemaDefinition(
            tsName,
            type.QualifiedName.Namespace,
            "complex",
            SameName(tsName, BaseName(complex.BaseXmlSchemaType)),
            Documentation(type),
            BuildFields(complex, tsName),
            []),
        XmlSchemaSimpleType simple => BuildSimple(simple, tsName),
        _ => new SchemaDefinition(tsName, type.QualifiedName.Namespace, "type", null, null, [], []),
    };

    private SchemaDefinition BuildElement(XmlSchemaElement element, string tsName)
    {
        var type = element.ElementSchemaType;

        if (type is null)
        {
            return new SchemaDefinition(
                tsName, element.QualifiedName.Namespace, "element", null,
                Documentation(element), [], []);
        }

        return new SchemaDefinition(
            tsName, element.QualifiedName.Namespace, "element", TypeRef(type, tsName),
            Documentation(element), [], []);
    }

    private SchemaDefinition BuildSimple(XmlSchemaSimpleType simple, string tsName)
    {
        switch (simple.Content)
        {
            case XmlSchemaSimpleTypeRestriction restriction:
            {
                var enums = restriction.Facets
                    .OfType<XmlSchemaEnumerationFacet>()
                    .Select(facet => facet.Value ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .Take(400)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new SchemaDefinition(
                    tsName,
                    simple.QualifiedName.Namespace,
                    "enum",
                    enums.Count > 0 ? null : BaseName(simple.BaseXmlSchemaType),
                    Documentation(simple),
                    [],
                    enums);
            }

            case XmlSchemaSimpleTypeList list:
            {
                var itemType = list.ItemType is null
                    ? "string"
                    : TypeRef(list.ItemType, tsName + "_item");

                return new SchemaDefinition(
                    tsName, simple.QualifiedName.Namespace, "list", itemType,
                    Documentation(simple), [], []);
            }

            case XmlSchemaSimpleTypeUnion union:
            {
                var members = union.BaseMemberTypes
                    .Select(member => TypeRef(member, tsName + "_member"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new SchemaDefinition(
                    tsName, simple.QualifiedName.Namespace, "union",
                    string.Join(" | ", members), Documentation(simple), [], []);
            }

            default:
                return new SchemaDefinition(
                    tsName, simple.QualifiedName.Namespace, "simple", "string",
                    Documentation(simple), [], []);
        }
    }

    /// <summary>XmlSchemaSet иногда возвращает сам тип как базовый — это не связь.</summary>
    private static string? SameName(string owner, string? candidate) =>
        candidate is null or "unknown" || candidate == owner ? null : candidate;

    private string? BaseName(XmlSchemaType? type)
    {
        if (type is null)
        {
            return null;
        }

        if (IsBuiltIn(type.QualifiedName))
        {
            return Primitive(type.QualifiedName.Name);
        }

        return TypeRef(type, Sanitize(type.QualifiedName.Name));
    }

    /// <summary>Ссылка на тип в TS. Анонимные типы получают своё имя.</summary>
    private string TypeRef(XmlSchemaType? type, string preferred)
    {
        if (type is null)
        {
            return "unknown";
        }

        if (IsBuiltIn(type.QualifiedName))
        {
            return Primitive(type.QualifiedName.Name);
        }

        if (_typeNames.TryGetValue(type, out var known))
        {
            return known;
        }

        if (!string.IsNullOrEmpty(type.QualifiedName.Name))
        {
            // Тип из внешнего пространства, не попавшего в XmlSchemaSet.
            var name = Reserve(type.QualifiedName);
            _typeNames[type] = name;
            _pending.Enqueue((type, name));

            return name;
        }

        var anonymousName = ReserveAnonymous(preferred);
        _typeNames[type] = anonymousName;
        _pending.Enqueue((type, anonymousName));

        return anonymousName;
    }

    private static string Primitive(string name) => name switch
    {
        "boolean" => "boolean",
        "decimal" or "double" or "float" or "integer" or "int" or "long" or "short" or "byte" or
        "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger" or "negativeInteger" or
        "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" => "number",
        "anyType" or "anySimpleType" => "unknown",
        _ => "string",
    };

    private List<SchemaField> BuildFields(XmlSchemaComplexType complex, string owner)
    {
        var fields = new Dictionary<string, SchemaField>(StringComparer.Ordinal);
        var visited = new HashSet<XmlSchemaComplexType>();

        CollectBase(complex, fields, owner, visited);

        foreach (XmlSchemaObject attribute in complex.Attributes)
        {
            switch (attribute)
            {
                case XmlSchemaAttribute attr:
                {
                    var name = string.IsNullOrEmpty(attr.Name) ? attr.RefName.Name : attr.Name;
                    fields[name] = new SchemaField(
                        name,
                        TypeRef(attr.AttributeSchemaType, owner + "_" + Sanitize(name) + "_attr"),
                        attr.Use != XmlSchemaUse.Required,
                        false,
                        Documentation(attr));
                    break;
                }

                case XmlSchemaAttributeGroupRef groupRef
                    when _attributeGroups.TryGetValue(groupRef.RefName, out var group):
                {
                    foreach (XmlSchemaObject inner in group.Attributes)
                    {
                        if (inner is not XmlSchemaAttribute attr)
                        {
                            continue;
                        }

                        var name = string.IsNullOrEmpty(attr.Name) ? attr.RefName.Name : attr.Name;
                        fields[name] = new SchemaField(
                            name,
                            TypeRef(attr.AttributeSchemaType, owner + "_" + Sanitize(name) + "_attr"),
                            attr.Use != XmlSchemaUse.Required,
                            false,
                            Documentation(attr));
                    }

                    break;
                }
            }
        }

        CollectParticle(complex.Particle, false, fields, owner);

        if (complex.ContentType == XmlSchemaContentType.TextOnly)
        {
            fields["#text"] = new SchemaField("#text", "string", true, false, null);
        }

        return fields.Values.ToList();
    }

    private void CollectBase(
        XmlSchemaComplexType complex,
        Dictionary<string, SchemaField> fields,
        string owner,
        HashSet<XmlSchemaComplexType> visited)
    {
        if (complex.BaseXmlSchemaType is not XmlSchemaComplexType baseType ||
            IsBuiltIn(baseType.QualifiedName) ||
            !visited.Add(baseType))
        {
            return;
        }

        CollectBase(baseType, fields, owner, visited);

        foreach (XmlSchemaObject attribute in baseType.Attributes)
        {
            if (attribute is XmlSchemaAttribute attr)
            {
                var name = string.IsNullOrEmpty(attr.Name) ? attr.RefName.Name : attr.Name;
                fields[name] = new SchemaField(
                    name,
                    TypeRef(attr.AttributeSchemaType, owner + "_" + Sanitize(name) + "_attr"),
                    attr.Use != XmlSchemaUse.Required,
                    false,
                    Documentation(attr));
            }
        }

        CollectParticle(baseType.Particle, false, fields, owner);
    }

    private void CollectParticle(
        XmlSchemaObject? particle,
        bool optionalContext,
        Dictionary<string, SchemaField> fields,
        string owner)
    {
        switch (particle)
        {
            case XmlSchemaSequence sequence:
                foreach (XmlSchemaObject item in sequence.Items)
                {
                    CollectParticle(item, optionalContext || sequence.MinOccurs == 0, fields, owner);
                }

                break;

            case XmlSchemaAll all:
                foreach (XmlSchemaObject item in all.Items)
                {
                    CollectParticle(item, optionalContext || all.MinOccurs == 0, fields, owner);
                }

                break;

            case XmlSchemaChoice choice:
                foreach (XmlSchemaObject item in choice.Items)
                {
                    CollectParticle(item, true, fields, owner);
                }

                break;

            case XmlSchemaGroupRef groupRef
                when _groups.TryGetValue(groupRef.RefName, out var group):
                CollectParticle(group.Particle, optionalContext || groupRef.MinOccurs == 0, fields, owner);
                break;

            case XmlSchemaElement element:
            {
                var name = string.IsNullOrEmpty(element.Name) ? element.RefName.Name : element.Name;
                var optional = optionalContext || element.MinOccurs == 0;
                var array = element.MaxOccursString == "unbounded" || element.MaxOccurs > 1;
                fields[name] = new SchemaField(
                    name,
                    TypeRef(element.ElementSchemaType, owner + "_" + Sanitize(name)),
                    optional,
                    array,
                    Documentation(element));
                break;
            }
        }
    }

    private static string? Documentation(XmlSchemaAnnotated? item)
    {
        var text = item?.Annotation?.Items
            ?.OfType<XmlSchemaDocumentation>()
            .SelectMany(documentation => documentation.Markup ?? [])
            .Select(node => node.InnerText)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = Regex.Replace(text, "\\s+", " ").Trim();

        return text.Length > 400 ? text[..400] + "…" : text;
    }
}
