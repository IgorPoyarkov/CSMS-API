using Csms.Domain;

namespace Csms.Api;

/// <summary>
/// Снимок контракта и предлагаемые условия изменения/расторжения.
///
/// Снимок приходит с фронта из реестра контрактов: модуль не читает
/// json-server и не подменяет цену или статус контракта «своими». Поля
/// необязательны на уровне DTO — обязательность проверяют правила ст. 95,
/// чтобы ошибка была одинаковой и в форме, и при повторном сохранении.
/// </summary>
public sealed record ContractChangeInputRequest(
    string? OrganizationId,
    string? ContractId,
    string? ContractNumber,
    string? ContractName,
    decimal ContractPrice,
    string? ContractStatus,
    DateOnly? ContractDate,
    string? CustomerName,
    string? SupplierName,
    string? Ikz,
    string? EisRegistryNumber,
    decimal? ContractQuantity,
    DateOnly? ContractExecutionEndDate,
    ContractChangeKind Kind,
    string? BasisCode,
    string? Reason,
    string? Description,
    DateOnly? ChangeDate,
    DateOnly? TerminationDate,
    decimal? NewPrice,
    decimal? NewQuantity,
    string? QuantityUnit,
    string? NewSubject,
    string? NewSupplier,
    DateOnly? NewExecutionEndDate,
    string? NoticeNumber,
    DateOnly? NoticeDate,
    string? CourtDecisionNumber,
    DateOnly? CourtDecisionDate,
    string? GovernmentActNumber,
    DateOnly? GovernmentActDate,
    string? FormulaText,
    decimal? UnitPrice,
    decimal? InitialMaxPrice,
    string? CreatedBy)
{
    public ContractChangeInput ToDomain() => new(
        OrganizationId?.Trim() ?? string.Empty,
        ContractId?.Trim() ?? string.Empty,
        ContractNumber?.Trim() ?? string.Empty,
        ContractName?.Trim() ?? string.Empty,
        ContractPrice,
        ContractStatus?.Trim().ToLowerInvariant() ?? string.Empty,
        ContractDate,
        CustomerName?.Trim(),
        SupplierName?.Trim(),
        Ikz?.Trim(),
        EisRegistryNumber?.Trim(),
        ContractQuantity,
        ContractExecutionEndDate,
        Kind,
        BasisCode?.Trim() ?? string.Empty,
        Reason?.Trim() ?? string.Empty,
        Description?.Trim(),
        ChangeDate ?? default,
        TerminationDate,
        NewPrice,
        NewQuantity,
        QuantityUnit?.Trim(),
        NewSubject?.Trim(),
        NewSupplier?.Trim(),
        NewExecutionEndDate,
        NoticeNumber?.Trim(),
        NoticeDate,
        CourtDecisionNumber?.Trim(),
        CourtDecisionDate,
        GovernmentActNumber?.Trim(),
        GovernmentActDate,
        FormulaText?.Trim(),
        UnitPrice,
        InitialMaxPrice,
        CreatedBy?.Trim() ?? string.Empty);
}

/// <summary>Создать документ об изменении или расторжении.</summary>
public sealed record CreateContractChangeRequest(ContractChangeInputRequest? Input)
{
    public ContractChangeInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия изменения или расторжения.")).ToDomain();
}

/// <summary>Изменить черновик или отклонённый документ.</summary>
public sealed record UpdateContractChangeRequest(ContractChangeInputRequest? Input, string? Actor)
{
    public ContractChangeInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия изменения или расторжения.")).ToDomain();
}

/// <summary>Добавить соглашение, уведомление, решение или приложение.</summary>
public sealed record AddContractChangeDocumentRequest(
    ContractChangeDocumentType Type,
    string? Title,
    string? Content,
    string? Actor);

/// <summary>Действие по статусу: отправить, согласовать, отклонить, подписать, отменить.</summary>
public sealed record ContractChangeActionRequest(string? Actor, string? Comment);

/// <summary>Зафиксировать регистрацию документа в ЕИС.</summary>
public sealed record RegisterContractChangeRequest(
    string? Actor,
    string? RegistrationNumber,
    DateOnly RegistrationDate);

/// <summary>Проверить условия без сохранения — для формы и предпросмотра.</summary>
public sealed record ValidateContractChangeRequest(ContractChangeInputRequest? Input)
{
    public ContractChangeInput ToDomain() =>
        (Input ?? throw new DomainException("Не переданы условия изменения или расторжения.")).ToDomain();
}

/// <summary>Документ в том виде, в каком его видит фронт.</summary>
public sealed record ContractChangeDocumentResponse(
    Guid Id,
    ContractChangeDocumentType Type,
    string Title,
    string Content,
    int Version,
    string Sha256,
    bool MatchesHash,
    string CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static ContractChangeDocumentResponse From(ContractChangeDocument document) =>
        new(
            document.Id,
            document.Type,
            document.Title,
            document.Content,
            document.Version,
            document.Sha256,
            document.MatchesHash(),
            document.CreatedBy,
            document.CreatedAt);
}

/// <summary>Событие аудита по документу.</summary>
public sealed record ContractChangeEventResponse(
    DateTimeOffset At,
    ContractChangeStatus From,
    ContractChangeStatus To,
    string Action,
    string Comment,
    string Actor)
{
    public static ContractChangeEventResponse FromEvent(ContractChangeEvent item) =>
        new(item.At, item.From, item.To, item.Action, item.Comment, item.Actor);
}

/// <summary>
/// Документ изменения/расторжения вместе с текущими замечаниями.
///
/// <c>Issues</c> отдаются всегда, а не только при ошибке: интерфейс показывает
/// предупреждения до отправки на согласование, а не после отказа сервера.
/// </summary>
public sealed record ContractChangeResponse(
    Guid Id,
    ContractChangeInput Input,
    ContractChangeStatus Status,
    int Revision,
    IReadOnlyList<ContractChangeDocumentResponse> Documents,
    IReadOnlyList<ContractChangeEventResponse> Events,
    IReadOnlyList<ContractChangeIssue> Issues,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SignedAt,
    string? SignedBy,
    DateTimeOffset? RegisteredAt,
    string? RegisteredBy,
    string? EisRegistrationNumber,
    DateOnly? EisRegistrationDate,
    string? RejectedReason,
    string? CancelledReason)
{
    public static ContractChangeResponse From(ContractChange change) =>
        new(
            change.Id,
            change.Input,
            change.Status,
            change.Revision,
            change.Documents.Select(ContractChangeDocumentResponse.From).ToList(),
            change.Events.Select(ContractChangeEventResponse.FromEvent).ToList(),
            change.Validate(),
            change.CreatedAt,
            change.UpdatedAt,
            change.SubmittedAt,
            change.ApprovedAt,
            change.SignedAt,
            change.SignedBy,
            change.RegisteredAt,
            change.RegisteredBy,
            change.EisRegistrationNumber,
            change.EisRegistrationDate,
            change.RejectedReason,
            change.CancelledReason);
}

/// <summary>Основание изменения/расторжения для формы.</summary>
public sealed record ContractChangeBasisResponse(
    string Code,
    ContractChangeKind Kind,
    string Article,
    string Title,
    string Description,
    bool AllowsPriceChange,
    bool AllowsQuantityChange,
    decimal MaxQuantityPercent,
    bool AllowsQualityImprovement,
    bool RequiresGovernmentAct,
    bool RequiresCourtDecision,
    bool RequiresUnilateralNotice,
    bool RequiresNewSupplier,
    bool RequiresFormula,
    bool RequiresUnitPrice,
    bool RequiresEisRegistration,
    bool OnlyPriceReduction,
    decimal MaxPriceIncreasePercent)
{
    public static ContractChangeBasisResponse From(ContractChangeBasis basis) =>
        new(
            basis.Code,
            basis.Kind,
            basis.Article,
            basis.Title,
            basis.Description,
            basis.AllowsPriceChange,
            basis.AllowsQuantityChange,
            basis.MaxQuantityPercent,
            basis.AllowsQualityImprovement,
            basis.RequiresGovernmentAct,
            basis.RequiresCourtDecision,
            basis.RequiresUnilateralNotice,
            basis.RequiresNewSupplier,
            basis.RequiresFormula,
            basis.RequiresUnitPrice,
            basis.RequiresEisRegistration,
            basis.OnlyPriceReduction,
            basis.MaxPriceIncreasePercent);
}

/// <summary>Справочные значения для формы: статусы, виды, типы документов.</summary>
public sealed record ContractChangeMetaResponse(
    IReadOnlyList<ContractChangeStatusOption> Statuses,
    IReadOnlyList<ContractChangeKindOption> Kinds,
    IReadOnlyList<ContractChangeDocumentTypeOption> DocumentTypes,
    int UnilateralNoticeDays);

public sealed record ContractChangeStatusOption(ContractChangeStatus Value, string Label);

public sealed record ContractChangeKindOption(ContractChangeKind Value, string Label);

public sealed record ContractChangeDocumentTypeOption(ContractChangeDocumentType Value, string Label);
