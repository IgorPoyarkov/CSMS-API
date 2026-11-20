using Csms.Domain;

namespace Csms.Api;

/// <summary>
/// Снимок контракта и условий приёмки, присланный с фронта.
///
/// Поля необязательны на уровне DTO — обязательность проверяют правила
/// ст. 94, чтобы ошибка была одинаковой и в форме, и при повторном сохранении.
/// </summary>
public sealed record AcceptanceInputRequest(
    string? OrganizationId,
    string? ContractId,
    string? ContractNumber,
    string? ContractName,
    decimal ContractPrice,
    string? ContractStatus,
    DateOnly? ContractDate,
    string? CustomerName,
    string? SupplierName,
    string? SupplierInn,
    string? Ikz,
    string? EisRegistryNumber,
    int? StageNumber,
    string? StageName,
    AcceptanceKind Kind,
    string? DocumentNumber,
    DateOnly? DocumentDate,
    string? CorrectedDocumentNumber,
    DateOnly? CorrectedDocumentDate,
    string? CorrectionReason,
    int? AcceptanceTermDays,
    bool ExpertiseRequired,
    bool ExpertiseByThirdParty,
    string? ExpertiseOrganization,
    string? ExpertiseConclusionNumber,
    DateOnly? ExpertiseConclusionDate,
    bool CommissionRequired,
    string? CommissionDecisionNumber,
    DateOnly? CommissionDecisionDate,
    string? DeliveryPlace,
    string? Notes,
    string? CreatedBy)
{
    public AcceptanceInput ToDomain() => new(
        OrganizationId?.Trim() ?? string.Empty,
        ContractId?.Trim() ?? string.Empty,
        ContractNumber?.Trim() ?? string.Empty,
        ContractName?.Trim() ?? string.Empty,
        ContractPrice,
        ContractStatus?.Trim().ToLowerInvariant() ?? string.Empty,
        ContractDate,
        CustomerName?.Trim(),
        SupplierName?.Trim(),
        SupplierInn?.Trim(),
        Ikz?.Trim(),
        EisRegistryNumber?.Trim(),
        StageNumber,
        StageName?.Trim(),
        Kind,
        DocumentNumber?.Trim(),
        DocumentDate,
        CorrectedDocumentNumber?.Trim(),
        CorrectedDocumentDate,
        CorrectionReason?.Trim(),
        // Если срок не передан, берём предельный по ч. 13 ст. 94: правило
        // проверит его как обычное значение контракта.
        AcceptanceTermDays ?? AcceptanceRules.MaxReviewTermDays,
        ExpertiseRequired,
        ExpertiseByThirdParty,
        ExpertiseOrganization?.Trim(),
        ExpertiseConclusionNumber?.Trim(),
        ExpertiseConclusionDate,
        CommissionRequired,
        CommissionDecisionNumber?.Trim(),
        CommissionDecisionDate,
        DeliveryPlace?.Trim(),
        Notes?.Trim(),
        CreatedBy?.Trim() ?? string.Empty);
}

/// <summary>Создать документ о приёмке или корректировочный документ.</summary>
public sealed record CreateAcceptanceRequest(AcceptanceInputRequest? Input)
{
    public AcceptanceInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия приёмки.")).ToDomain();
}

/// <summary>Изменить черновик или возвращённый для уточнения документ.</summary>
public sealed record UpdateAcceptanceRequest(AcceptanceInputRequest? Input, string? Actor)
{
    public AcceptanceInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия приёмки.")).ToDomain();
}

/// <summary>Проверить условия без сохранения — для формы и предпросмотра.</summary>
public sealed record ValidateAcceptanceRequest(AcceptanceInputRequest? Input)
{
    public AcceptanceInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия приёмки.")).ToDomain();
}

/// <summary>Позиция принятого.</summary>
public sealed record AddAcceptancePositionRequest(
    string? Name,
    string? Okpd2,
    string? Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal VatRate,
    string? CountryOrigin,
    string? RegistryNumber,
    string? Actor);

/// <summary>Недостаток, выявленный при приёмке.</summary>
public sealed record AddAcceptanceDefectRequest(
    Guid? PositionId,
    string? Description,
    decimal Quantity,
    AcceptanceDefectRequirement Requirement,
    string? Actor);

/// <summary>Действие по статусу: направить, отказать, вернуть, отменить.</summary>
public sealed record AcceptanceActionRequest(string? Actor, string? Comment);

/// <summary>Итог приёмки, который подписывает заказчик.</summary>
public sealed record AcceptAcceptanceRequest(
    string? Actor,
    AcceptanceResult Result,
    string? Comment);

/// <summary>Зафиксировать регистрацию документа в ЕИС.</summary>
public sealed record RegisterAcceptanceRequest(
    string? Actor,
    string? RegistrationNumber,
    DateOnly RegistrationDate);

/// <summary>Позиция в том виде, в каком её видит фронт.</summary>
public sealed record AcceptancePositionResponse(
    Guid Id,
    string Name,
    string? Okpd2,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal VatRate,
    decimal TotalWithVat,
    decimal VatAmount,
    decimal TotalWithoutVat,
    string? CountryOrigin,
    string? RegistryNumber)
{
    public static AcceptancePositionResponse From(AcceptancePosition position) =>
        new(
            position.Id,
            position.Name,
            position.Okpd2,
            position.Unit,
            position.Quantity,
            position.UnitPrice,
            position.VatRate,
            position.TotalWithVat,
            position.VatAmount,
            position.TotalWithoutVat,
            position.CountryOrigin,
            position.RegistryNumber);
}

public sealed record AcceptanceDefectResponse(
    Guid Id,
    Guid? PositionId,
    string Description,
    decimal Quantity,
    AcceptanceDefectRequirement Requirement,
    string RequirementLabel)
{
    public static AcceptanceDefectResponse From(AcceptanceDefect defect) =>
        new(
            defect.Id,
            defect.PositionId,
            defect.Description,
            defect.Quantity,
            defect.Requirement,
            defect.Requirement.Ru());
}

public sealed record AcceptanceEventResponse(
    DateTimeOffset At,
    AcceptanceStatus From,
    AcceptanceStatus To,
    string Action,
    string Comment,
    string Actor)
{
    public static AcceptanceEventResponse FromEvent(AcceptanceEvent item) =>
        new(item.At, item.From, item.To, item.Action, item.Comment, item.Actor);
}

/// <summary>
/// Документ о приёмке вместе с текущими замечаниями и итогами.
///
/// <c>Issues</c> отдаются всегда, а не только при ошибке: интерфейс показывает
/// предупреждения до отправки, а не после отказа сервера.
/// </summary>
public sealed record AcceptanceResponse(
    Guid Id,
    AcceptanceInput Input,
    AcceptanceStatus Status,
    AcceptanceResult? Result,
    int Revision,
    IReadOnlyList<AcceptancePositionResponse> Positions,
    IReadOnlyList<AcceptanceDefectResponse> Defects,
    IReadOnlyList<AcceptanceEventResponse> Events,
    IReadOnlyList<AcceptanceIssue> Issues,
    decimal TotalWithVat,
    decimal VatAmount,
    decimal TotalWithoutVat,
    DateOnly? ReviewDeadline,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? SignedAt,
    string? SignedBy,
    DateTimeOffset? RegisteredAt,
    string? RegisteredBy,
    string? EisRegistrationNumber,
    DateOnly? EisRegistrationDate,
    string? RefusalReason,
    string? ReturnComment,
    string? CancelledReason)
{
    public static AcceptanceResponse From(ContractAcceptance acceptance) =>
        new(
            acceptance.Id,
            acceptance.Input,
            acceptance.Status,
            acceptance.Result,
            acceptance.Revision,
            acceptance.Positions.Select(AcceptancePositionResponse.From).ToList(),
            acceptance.Defects.Select(AcceptanceDefectResponse.From).ToList(),
            acceptance.Events.Select(AcceptanceEventResponse.FromEvent).ToList(),
            acceptance.Validate(),
            acceptance.TotalWithVat,
            acceptance.VatAmount,
            acceptance.TotalWithoutVat,
            acceptance.ReviewDeadline,
            acceptance.CreatedAt,
            acceptance.UpdatedAt,
            acceptance.SubmittedAt,
            acceptance.ReviewedAt,
            acceptance.SignedAt,
            acceptance.SignedBy,
            acceptance.RegisteredAt,
            acceptance.RegisteredBy,
            acceptance.EisRegistrationNumber,
            acceptance.EisRegistrationDate,
            acceptance.RefusalReason,
            acceptance.ReturnComment,
            acceptance.CancelledReason);
}

/// <summary>Справочные значения для формы приёмки.</summary>
public sealed record AcceptanceMetaResponse(
    IReadOnlyList<AcceptanceStatusOption> Statuses,
    IReadOnlyList<AcceptanceKindOption> Kinds,
    IReadOnlyList<AcceptanceResultOption> Results,
    IReadOnlyList<AcceptanceDefectRequirementOption> DefectRequirements,
    IReadOnlyList<decimal> VatRates,
    int MaxReviewTermDays);

public sealed record AcceptanceStatusOption(AcceptanceStatus Value, string Label);

public sealed record AcceptanceKindOption(AcceptanceKind Value, string Label);

public sealed record AcceptanceResultOption(AcceptanceResult Value, int EisCode, string Label);

public sealed record AcceptanceDefectRequirementOption(AcceptanceDefectRequirement Value, string Label);
