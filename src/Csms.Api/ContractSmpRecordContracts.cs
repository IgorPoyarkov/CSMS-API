using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Csms.Domain;

namespace Csms.Api;

/// <summary>
/// Идентификаторы приходят и строкой, и числом: в json-server ключи числовые,
/// в PostgreSQL — строки. System.Text.Json не превращает число в <see cref="string"/>
/// сам, поэтому нормализуем на границе API, не меняя домен.
/// </summary>
public sealed class ContractSmpIdJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()?.Trim(),
            JsonTokenType.Number => reader.TryGetInt64(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Ожидается строка или число."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// Входные данные записи о привлечении СМП. Даты и деньги приходят строками —
/// ровно так, как их отдаёт frontend-схема; obligatory проверяет домен, а не
/// привязчик JSON, чтобы ошибка была одинаковой в форме и при сохранении.
/// </summary>
public sealed record ContractSmpRecordInputRequest(
    [property: JsonConverter(typeof(ContractSmpIdJsonConverter))] string? OrganizationId,
    [property: JsonConverter(typeof(ContractSmpIdJsonConverter))] string? ContractId,
    string? ContractNumber,
    string? ContractName,
    string? ContractPrice,
    string? RequiredPercent,
    string? EisRegistryNumber,
    ContractSmpDocumentType DocumentType,
    string? DocumentNumber,
    string? DocumentDate,
    string? Amount,
    string? Okpd2Code,
    ContractSmpPriceType PriceType,
    string? UnitPrice,
    string? Subject,
    string? Period,
    string? SupplierName,
    string? SupplierInn,
    string? SupplierKpp,
    bool IsSmpSubject,
    [property: JsonConverter(typeof(ContractSmpIdJsonConverter))] string? SubContractRecordId,
    bool IsReplacementSubContract,
    bool PaidEarlierThanContractSchedule,
    string? PaidToSupplierDate,
    string? AcceptanceSignedDate,
    string? ReceivedAt,
    string? SourceFileName,
    string? SupplierConfirmedAt,
    string? SupplierConfirmedBy,
    string? SupplierConfirmedDocument,
    string? CustomerConfirmedAt,
    string? CustomerConfirmedBy,
    string? CustomerConfirmedDocument,
    string? EisPublishDate,
    string? EisRecordNumber,
    string? Comment,
    string? Actor)
{
    public ContractSmpRecordInput ToDomain() => new(
        OrganizationId?.Trim() ?? string.Empty,
        ContractId?.Trim() ?? string.Empty,
        ContractNumber?.Trim() ?? string.Empty,
        ContractName?.Trim() ?? string.Empty,
        ParseMoney(ContractPrice, "contractPrice") ?? 0m,
        ParseMoney(RequiredPercent, "requiredPercent"),
        EisRegistryNumber?.Trim(),
        DocumentType,
        DocumentNumber?.Trim() ?? string.Empty,
        ParseRequiredDate(DocumentDate, "documentDate"),
        ParseMoney(Amount, "amount"),
        Okpd2Code?.Trim(),
        PriceType,
        ParseMoney(UnitPrice, "unitPrice"),
        Subject?.Trim(),
        Period?.Trim(),
        SupplierName?.Trim() ?? string.Empty,
        SupplierInn?.Trim(),
        SupplierKpp?.Trim(),
        IsSmpSubject,
        NormalizeId(SubContractRecordId),
        IsReplacementSubContract,
        PaidEarlierThanContractSchedule,
        ParseDate(PaidToSupplierDate, "paidToSupplierDate"),
        ParseDate(AcceptanceSignedDate, "acceptanceSignedDate"),
        ParseRequiredDateTime(ReceivedAt, "receivedAt"),
        SourceFileName?.Trim(),
        ParseDateTime(SupplierConfirmedAt, "supplierConfirmedAt"),
        SupplierConfirmedBy?.Trim(),
        SupplierConfirmedDocument?.Trim(),
        ParseDateTime(CustomerConfirmedAt, "customerConfirmedAt"),
        CustomerConfirmedBy?.Trim(),
        CustomerConfirmedDocument?.Trim(),
        ParseDate(EisPublishDate, "eisPublishDate"),
        EisRecordNumber?.Trim(),
        Comment?.Trim(),
        Actor?.Trim() ?? string.Empty);

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly ParseRequiredDate(string? value, string field) =>
        ParseDate(value, field) ?? throw new DomainException($"Укажите поле {field}.");

    private static DateOnly? ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new DomainException($"Поле {field}: ожидается дата в формате ГГГГ-ММ-ДД.");
    }

    private static DateTimeOffset ParseRequiredDateTime(string? value, string field) =>
        ParseDateTime(value, field) ?? throw new DomainException($"Укажите поле {field}.");

    private static DateTimeOffset? ParseDateTime(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.Contains(':'))
        {
            throw new DomainException($"Поле {field}: ожидается дата и время (ГГГГ-ММ-ДДТЧЧ:ММ).");
        }

        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var moment))
        {
            return moment;
        }

        throw new DomainException($"Поле {field}: неверный формат даты и времени.");
    }

    private static decimal? ParseMoney(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace(" ", string.Empty).Replace("\u00A0", string.Empty);

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return amount;
        }

        throw new DomainException($"Поле {field}: ожидается число с точностью до копеек.");
    }
}

/// <summary>
/// Форматирование полей под frontend-схему: даты без смещения, деньги строкой.
/// </summary>
public static class ContractSmpFormat
{
    public static string Date(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string DateTime(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string Money(decimal? value) =>
        value?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>Действие по статусу: принять, отклонить или вернуть в работу.</summary>
public sealed record ContractSmpActionRequest(string? Actor, string? Comment);

/// <summary>Зафиксировать выгрузку сведений в карточку контракта ЕИС.</summary>
public sealed record PushContractSmpRecordRequest(
    string? Actor,
    string? PublishDate,
    string? RecordNumber);

public sealed record ContractSmpDocumentResponse(
    Guid Id,
    ContractSmpDocumentType Type,
    string Title,
    string Content,
    int Version,
    string Sha256,
    bool MatchesHash,
    string CreatedBy,
    string CreatedAt)
{
    public static ContractSmpDocumentResponse From(ContractSmpDocument document) =>
        new(
            document.Id,
            document.Type,
            document.Title,
            document.Content,
            document.Version,
            document.Sha256,
            document.MatchesHash(),
            document.CreatedBy,
            ContractSmpFormat.DateTime(document.CreatedAt));
}

public sealed record ContractSmpEventResponse(
    string At,
    ContractSmpStatus From,
    ContractSmpStatus To,
    string Action,
    string Comment,
    string Actor)
{
    public static ContractSmpEventResponse FromEvent(ContractSmpEvent item) =>
        new(ContractSmpFormat.DateTime(item.At), item.From, item.To, item.Action, item.Comment, item.Actor);
}

/// <summary>
/// Запись в том виде, в каком её читает frontend: плоские поля, строки для
/// дат и денег, строковые перечисления. Дополнительные поля (журнал, статус,
/// замечания) схемой клиента игнорируются.
/// </summary>
public sealed record ContractSmpRecordResponse(
    Guid Id,
    string OrganizationId,
    string ContractId,
    string ContractNumber,
    string ContractName,
    decimal ContractPrice,
    decimal? RequiredPercent,
    string? EisRegistryNumber,
    ContractSmpDocumentType DocumentType,
    string DocumentNumber,
    string DocumentDate,
    string Amount,
    string Okpd2Code,
    ContractSmpPriceType PriceType,
    string UnitPrice,
    string Subject,
    string Period,
    string SupplierName,
    string SupplierInn,
    string SupplierKpp,
    bool IsSmpSubject,
    string? SubContractRecordId,
    bool IsReplacementSubContract,
    bool PaidEarlierThanContractSchedule,
    string PaidToSupplierDate,
    string AcceptanceSignedDate,
    string ReceivedAt,
    string SourceFileName,
    string SupplierConfirmedAt,
    string SupplierConfirmedBy,
    string SupplierConfirmedDocument,
    string CustomerConfirmedAt,
    string CustomerConfirmedBy,
    string CustomerConfirmedDocument,
    ContractSmpStatus Status,
    string EisPublishDate,
    string EisRecordNumber,
    string Comment,
    string CreatedAt,
    int Revision,
    IReadOnlyList<ContractSmpDocumentResponse> Documents,
    IReadOnlyList<ContractSmpEventResponse> Events,
    IReadOnlyList<ContractSmpIssue> Issues)
{
    public static ContractSmpRecordResponse From(ContractSmpRecord record)
    {
        var input = record.Input;

        return new ContractSmpRecordResponse(
            record.Id,
            input.OrganizationId,
            input.ContractId,
            input.ContractNumber,
            input.ContractName,
            input.ContractPrice,
            input.RequiredPercent,
            input.EisRegistryNumber,
            input.DocumentType,
            input.DocumentNumber,
            ContractSmpFormat.Date(input.DocumentDate),
            ContractSmpFormat.Money(input.Amount),
            input.Okpd2Code ?? string.Empty,
            input.PriceType,
            ContractSmpFormat.Money(input.UnitPrice),
            input.Subject ?? string.Empty,
            input.Period ?? string.Empty,
            input.SupplierName,
            input.SupplierInn ?? string.Empty,
            input.SupplierKpp ?? string.Empty,
            input.IsSmpSubject,
            input.SubContractRecordId,
            input.IsReplacementSubContract,
            input.PaidEarlierThanContractSchedule,
            ContractSmpFormat.Date(input.PaidToSupplierDate),
            ContractSmpFormat.Date(input.AcceptanceSignedDate),
            ContractSmpFormat.DateTime(input.ReceivedAt),
            input.SourceFileName ?? string.Empty,
            ContractSmpFormat.DateTime(input.SupplierConfirmedAt),
            input.SupplierConfirmedBy ?? string.Empty,
            input.SupplierConfirmedDocument ?? string.Empty,
            ContractSmpFormat.DateTime(input.CustomerConfirmedAt),
            input.CustomerConfirmedBy ?? string.Empty,
            input.CustomerConfirmedDocument ?? string.Empty,
            record.Status,
            ContractSmpFormat.Date(record.EisPublishDate),
            record.EisRecordNumber ?? string.Empty,
            input.Comment ?? string.Empty,
            ContractSmpFormat.DateTime(record.CreatedAt),
            record.Revision,
            record.Documents.Select(ContractSmpDocumentResponse.From).ToList(),
            record.Events.Select(ContractSmpEventResponse.FromEvent).ToList(),
            record.Validate());
    }
}

/// <summary>Справочные значения формы: статусы, типы документов, сроки.</summary>
public sealed record ContractSmpMetaResponse(
    IReadOnlyList<ContractSmpOption<ContractSmpStatus>> Statuses,
    IReadOnlyList<ContractSmpOption<ContractSmpDocumentType>> DocumentTypes,
    IReadOnlyList<ContractSmpOption<ContractSmpPriceType>> PriceTypes,
    IReadOnlyList<SmpDeadlineRule> DeadlineRules,
    SmpDeadlineRule CustomerEisRule,
    int MinimumRequiredPercent,
    int EarlyPaymentWorkingDays);

public sealed record ContractSmpOption<T>(T Value, string Label);

/// <summary>Объём attracting и неустойка за недобор.</summary>
public sealed record ContractSmpVolumeResponse(SmpVolumeResult Volume, SmpPenaltyResult? Penalty);

public static class ContractSmpResponseText
{
    public static ContractSmpOption<ContractSmpStatus> Option(ContractSmpStatus status) =>
        new(status, status.Ru());

    public static ContractSmpOption<ContractSmpDocumentType> Option(ContractSmpDocumentType type) =>
        new(type, type.Ru());

    public static ContractSmpOption<ContractSmpPriceType> Option(ContractSmpPriceType priceType) =>
        new(priceType, priceType.Ru());
}
