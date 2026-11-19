using EisDocGenerator;

namespace Csms.Api;

/// <summary>
/// То, что бэкенд сообщает фронту после обновления XSD: версия, хеш каталога
/// и список определений, которые появились, исчезли или изменились.
/// </summary>
public sealed record EisSchemaUpdateEvent(
    string FromHash,
    string ToHash,
    string Version,
    DateTimeOffset GeneratedAt,
    int TypesAdded,
    int TypesRemoved,
    int TypesModified,
    IReadOnlyList<string> AddedTypes,
    IReadOnlyList<string> RemovedTypes,
    IReadOnlyList<string> ModifiedTypes,
    string CatalogUrl)
{
    /// <summary>Сравнить два каталога по именам определений и подписи полей.</summary>
    public static EisSchemaUpdateEvent Diff(
        SchemaCatalog? previous,
        SchemaCatalog current,
        string fromHash,
        string toHash)
    {
        var before = previous?.Types.ToDictionary(type => type.Name, StringComparer.Ordinal)
                     ?? new Dictionary<string, SchemaDefinition>(StringComparer.Ordinal);
        var after = current.Types.ToDictionary(type => type.Name, StringComparer.Ordinal);

        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var removed = before.Keys.Except(after.Keys, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var modified = after.Keys.Intersect(before.Keys, StringComparer.Ordinal)
            .Where(name => Signature(before[name]) != Signature(after[name]))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();

        return new EisSchemaUpdateEvent(
            fromHash,
            toHash,
            current.Version,
            current.GeneratedAt,
            added.Length,
            removed.Length,
            modified.Length,
            added,
            removed,
            modified,
            "/api/eis/schemas/catalog");
    }

    /// <summary>
    /// Подпись определения: всё, что влияет на форму XML. Имена полей сортируем,
    /// чтобы refactoring в генераторе не выглядел как изменение схемы.
    /// </summary>
    private static string Signature(SchemaDefinition definition)
    {
        var fields = definition.Fields
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => string.Join(
                ':',
                field.Name,
                field.Type,
                field.Optional ? "?" : "",
                field.Array ? "[]" : "",
                field.Documentation ?? ""));

        return string.Join(
            '|',
            definition.Kind,
            definition.Base ?? "",
            definition.Documentation ?? "",
            string.Join(';', fields),
            string.Join(';', definition.EnumValues.OrderBy(value => value, StringComparer.Ordinal)));
    }
}
