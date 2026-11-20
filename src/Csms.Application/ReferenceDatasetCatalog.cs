namespace Csms.Application;

/// <summary>
/// Описание справочника, который участвует в цепочке ЕИС.
///
/// Catalog не обещает, что данные уже загружены: он фиксирует коды из
/// <c>nsiAllList</c> и имена полей официальной выгрузки, чтобы импорт не
/// терял код/наименование и чтобы фронт видел ожидаемые датасеты до импорта.
/// </summary>
public sealed record ReferenceDatasetDefinition(
    string Code,
    string Title,
    IReadOnlyList<string> CodeFields,
    IReadOnlyList<string> NameFields,
    string? ParentCodeField = null);

/// <summary>Коды справочников, используемые мапперами ЕИС 17.0.</summary>
public static class ReferenceDatasetCatalog
{
    public static IReadOnlyList<ReferenceDatasetDefinition> All { get; } =
    [
        new("nsiOKPD2", "ОКПД2", ["code", "OKPD2"], ["name", "fullName"]),
        new("nsiKTRU", "КТРУ", ["ktruCode", "code"], ["name", "fullName", "shortName"], "okpd2Code"),
        new("nsiKLADR", "Адреса (КЛАДР/ФИАС)", ["kladrCode", "code", "guid"], ["name", "addressName"], "parentGuid"),
        new("nsiOKSM", "Страны (ОКСМ)", ["OKSM", "code", "numericCode"], ["name", "shortName"]),
        new("nsiOKTMO", "Муниципальные образования (ОКТМО)", ["OKTMO", "code"], ["name", "fullName"], "parentCode"),
        new("nsiKBK", "КБК", ["KBK", "code"], ["name", "fullName"], "parentCode"),
        new("nsiOKEI", "Единицы измерения (ОКЕИ)", ["OKEI", "code", "numericCode"], ["name", "shortName"]),
        new("nsiCurrency", "Валюты", ["currencyCode", "code", "digitalCode"], ["name", "shortName"]),
    ];

    /// <summary>Найти описание по коду из nsiAllList.</summary>
    public static ReferenceDatasetDefinition? Find(string dataset) =>
        All.FirstOrDefault(item =>
            item.Code.Equals(dataset?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Имена полей, из которых можно взять код конкретного справочника.</summary>
    public static IReadOnlyList<string> CodeFields(string dataset) =>
        Find(dataset)?.CodeFields ?? ["code"];

    /// <summary>Имена полей, из которых можно взять наименование.</summary>
    public static IReadOnlyList<string> NameFields(string dataset) =>
        Find(dataset)?.NameFields ?? ["name", "fullName", "shortName"];
}
