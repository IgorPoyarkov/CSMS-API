using System.Text.Json.Serialization;

namespace EisDocGenerator;

/// <summary>Одно описание типа/элемента для TS и обозревателя.</summary>
public sealed record SchemaDefinition(
    string Name,
    string Namespace,
    string Kind,
    string? Base,
    string? Documentation,
    List<SchemaField> Fields,
    List<string> EnumValues)
{
    [JsonIgnore]
    public string TsName { get; init; } = Name;
}

public sealed record SchemaField(
    string Name,
    string Type,
    bool Optional,
    bool Array,
    string? Documentation);

public sealed record NamespaceInfo(string Uri, string Alias, int TypeCount);

public sealed record SchemaCatalog(
    string Version,
    string SourceId,
    string Sha256,
    DateTimeOffset GeneratedAt,
    int XsdFileCount,
    List<NamespaceInfo> Namespaces,
    List<SchemaDefinition> Types);
