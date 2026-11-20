namespace Csms.Infrastructure;

/// <summary>
/// Строка документа об изменении/расторжении (схема <c>contract</c>).
///
/// Поля снимка контракта хранятся рядом с изменением, а не подтягиваются из
/// json-server: документ должен читаться и после того, как контракт изменят.
/// </summary>
public sealed class ContractChangeRow
{
    public Guid Id { get; set; }

    public string OrganizationId { get; set; } = string.Empty;
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ContractName { get; set; } = string.Empty;
    public decimal ContractPrice { get; set; }
    public string ContractStatus { get; set; } = string.Empty;
    public DateOnly? ContractDate { get; set; }
    public string? CustomerName { get; set; }
    public string? SupplierName { get; set; }
    public string? Ikz { get; set; }
    public string? EisRegistryNumber { get; set; }
    public decimal? ContractQuantity { get; set; }
    public DateOnly? ContractExecutionEndDate { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string BasisCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly ChangeDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public decimal? NewPrice { get; set; }
    public decimal? NewQuantity { get; set; }
    public string? QuantityUnit { get; set; }
    public string? NewSubject { get; set; }
    public string? NewSupplier { get; set; }
    public DateOnly? NewExecutionEndDate { get; set; }
    public string? NoticeNumber { get; set; }
    public DateOnly? NoticeDate { get; set; }
    public string? CourtDecisionNumber { get; set; }
    public DateOnly? CourtDecisionDate { get; set; }
    public string? GovernmentActNumber { get; set; }
    public DateOnly? GovernmentActDate { get; set; }
    public string? FormulaText { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? InitialMaxPrice { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedBy { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public string? EisRegistrationNumber { get; set; }
    public DateOnly? EisRegistrationDate { get; set; }
    public string? RejectedReason { get; set; }
    public string? CancelledReason { get; set; }

    /// <summary>
    /// Ревизия записи: увеличивается хранилищем при каждой успешной записи.
    /// Это токен оптимистичной блокировки — в отличие от <see cref="Xmin"/>,
    /// его значение известно документу с момента чтения.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Оптимистичная блокировка PostgreSQL, как у пакетов ЕИС.</summary>
    public uint Xmin { get; set; }

    public List<ContractChangeDocumentRow> Documents { get; set; } = [];
    public List<ContractChangeEventRow> Events { get; set; } = [];
}

public sealed class ContractChangeDocumentRow
{
    public Guid Id { get; set; }
    public Guid ChangeId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int OrderIndex { get; set; }
}

public sealed class ContractChangeEventRow
{
    public Guid Id { get; set; }
    public Guid ChangeId { get; set; }
    public DateTimeOffset At { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}
