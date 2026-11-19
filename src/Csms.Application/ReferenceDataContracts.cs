using System.Text.Json;

namespace Csms.Application;

/// <summary>
/// Активная версия одного справочника (ОКПД2, КТРУ, адреса и т.д.).
/// Версии не перезаписываются: фронт может узнать, какой именно набор
/// данных действовал на дату документа.
/// </summary>
public sealed record ReferenceDatasetInfo(
    string Code,
    string Version,
    string? SourceDocument,
    string? SourceUrl,
    string? SourceSha256,
    DateTimeOffset? PublishedAt,
    DateTimeOffset ImportedAt,
    int ItemCount,
    string Hash,
    bool IsActive);

/// <summary>Одна строка справочника. Payload — полный объект из официальной выгрузки.</summary>
public sealed record ReferenceItemInfo(
    string Code,
    string? ParentCode,
    string? Name,
    bool Actual,
    string PayloadJson);

/// <summary>Запись импорта: ключевые поля дублируются из payload для быстрого поиска.</summary>
public sealed record ReferenceImportItem(
    string? Code,
    string? ParentCode,
    string? Name,
    bool? Actual,
    JsonElement Payload);

/// <summary>
/// Что frontend/парсер присылает после разбора официальной выгрузки НСИ.
/// Dataset — код справочника из nsiAllList (например, nsiOKPD2).
/// </summary>
public sealed record ReferenceImportRequest(
    string Dataset,
    string Version,
    string? SourceDocument,
    string? SourceUrl,
    string? SourceSha256,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReferenceImportItem> Items);

/// <summary>Событие об установке новой версии справочника.</summary>
public sealed record ReferenceDataUpdateEvent(
    string Dataset,
    string FromVersion,
    string ToVersion,
    int ItemCount,
    string Hash,
    DateTimeOffset ImportedAt);

/// <summary>Хранилище версионируемых справочников.</summary>
public interface IReferenceDataStore
{
    Task<IReadOnlyList<ReferenceDatasetInfo>> GetDatasetsAsync(CancellationToken cancellationToken = default);

    Task<ReferenceDatasetInfo?> GetActiveAsync(string dataset, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReferenceItemInfo>> QueryAsync(
        string dataset,
        string? search,
        string? parentCode,
        int take,
        CancellationToken cancellationToken = default);

    Task<ReferenceDataUpdateEvent> PublishAsync(
        ReferenceImportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Приводит «сырой» импорт к контракту хранилища: поднимает code/name из
/// вложенного payload, если парсер не продублировал их в ключевые поля.
/// </summary>
public static class ReferenceImportNormalizer
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static ReferenceImportRequest Normalize(ReferenceImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Dataset))
        {
            throw new ArgumentException("Не задан код справочника (dataset).");
        }

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            throw new ArgumentException("Не задана версия выгрузки (version).");
        }

        if (request.Items.Count == 0)
        {
            throw new ArgumentException("В выгрузке нет записей.");
        }

        var items = new List<ReferenceImportItem>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var payload = item.Payload.ValueKind == JsonValueKind.Undefined
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : item.Payload.Clone();

            var code = FirstText(item.Code, payload, "code", "OKPD2", "ktruCode", "kladrCode")
                ?? throw new ArgumentException("У записи выгрузки нет поля code.");
            var parent = FirstText(
                item.ParentCode, payload, "parentCode", "okpd2Code", "OKPD2Code", "OKPD2");
            var name = FirstText(item.Name, payload, "name", "fullName", "shortName");

            items.Add(new ReferenceImportItem(
                code,
                parent,
                name,
                item.Actual ?? ReadBool(payload, "actual"),
                payload));
        }

        return request with
        {
            Dataset = request.Dataset.Trim(),
            Version = request.Version.Trim(),
            Items = items,
        };
    }

    /// <summary>Хеш активного набора: меняется при любой новой версии импорта.</summary>
    public static string ComputeHash(string dataset, string version, int itemCount, DateTimeOffset importedAt) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{dataset}|{version}|{itemCount}|{importedAt:O}")));

    /// <summary>Сериализация payload без BOM и с теми же именами полей, что пришли.</summary>
    public static string SerializePayload(JsonElement payload) => JsonSerializer.Serialize(payload, PayloadOptions);

    private static string? FirstText(string? explicitValue, JsonElement payload, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return explicitValue.Trim();
        }

        foreach (var name in names)
        {
            var found = FindLeaf(payload, name, depth: 0);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindLeaf(JsonElement element, string name, int depth)
    {
        if (depth > 5)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) ||
                    property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var direct = LeafText(property.Value);
                    if (direct is not null)
                    {
                        return direct;
                    }

                    // В КТРУ код ОКПД2 лежит объектом OKPD2/code — он не должен
                    // теряться при импорте, даже если парсер не развернул его.
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        var nestedCode = FindLeaf(property.Value, "code", depth + 1);
                        if (nestedCode is not null)
                        {
                            return nestedCode;
                        }
                    }
                }

                var nested = FindLeaf(property.Value, name, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindLeaf(child, name, depth + 1);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? LeafText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static bool ReadBool(JsonElement payload, string name)
    {
        var found = FindLeaf(payload, name, depth: 0);
        return bool.TryParse(found, out var value) && value;
    }
}
