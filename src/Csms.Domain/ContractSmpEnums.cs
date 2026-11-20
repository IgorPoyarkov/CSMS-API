using System.Text.Json;
using System.Text.Json.Serialization;

namespace Csms.Domain;

/// <summary>
/// Переводит enum-ы в те же строки, что ждёт фронт: "subContract",
/// "pushedToEis" и так далее. Без конвертера System.Text.Json отдал бы
/// "SubContract"/"PushedToEis" и схема zod на клиенте не приняла бы ответ.
/// </summary>
public sealed class CamelCaseEnumJsonConverter : JsonStringEnumConverter
{
    public CamelCaseEnumJsonConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>Вид документа, которым подтверждается привлечение СМП/СОНКО.</summary>
[JsonConverter(typeof(CamelCaseEnumJsonConverter))]
public enum ContractSmpDocumentType
{
    /// <summary>Договор с субподрядчиком соисполнителем.</summary>
    SubContract = 0,

    /// <summary>Декларация о принадлежности к СМП/СОНКО.</summary>
    Declaration = 1,

    /// <summary>Акт приёмки выполненных работ (оказанных услуг).</summary>
    Acceptance = 2,

    /// <summary>Платёжное поручение об оплате субподрядчику.</summary>
    Payment = 3,
}

/// <summary>
/// Статус сведений. Набор повторяет frontend-схему: запись подана подрядчиком,
/// принята заказчиком, отклонена или уже выгружена в карточку контракта ЕИС.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumJsonConverter))]
public enum ContractSmpStatus
{
    Submitted = 0,
    Accepted = 1,
    Rejected = 2,
    PushedToEis = 3,
}

/// <summary>Вид цены в карточке реестра контрактов ЕИС.</summary>
[JsonConverter(typeof(CamelCaseEnumJsonConverter))]
public enum ContractSmpPriceType
{
    /// <summary>Цена всего договора.</summary>
    Contract = 0,

    /// <summary>Цена единицы товара, работы или услуги.</summary>
    Unit = 1,
}

public static class ContractSmpStatusText
{
    public static string Ru(this ContractSmpStatus status) => status switch
    {
        ContractSmpStatus.Submitted => "Поданы подрядчиком",
        ContractSmpStatus.Accepted => "Приняты заказчиком",
        ContractSmpStatus.Rejected => "Отклонены",
        ContractSmpStatus.PushedToEis => "Выгружены в ЕИС",
        _ => status.ToString(),
    };
}

public static class ContractSmpDocumentTypeText
{
    public static string Ru(this ContractSmpDocumentType type) => type switch
    {
        ContractSmpDocumentType.SubContract => "Договор с субподрядчиком",
        ContractSmpDocumentType.Declaration => "Декларация о принадлежности к СМП/СОНКО",
        ContractSmpDocumentType.Acceptance => "Акт приёмки",
        ContractSmpDocumentType.Payment => "Платёжное поручение",
        _ => type.ToString(),
    };
}

public static class ContractSmpPriceTypeText
{
    public static string Ru(this ContractSmpPriceType priceType) => priceType switch
    {
        ContractSmpPriceType.Contract => "Цена контракта (договора)",
        ContractSmpPriceType.Unit => "Цена единицы товара (работы, услуги)",
        _ => priceType.ToString(),
    };
}
