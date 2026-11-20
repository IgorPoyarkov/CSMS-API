namespace Csms.Infrastructure;

/// <summary>
/// Строка документа о приёмке (схема <c>contract</c>).
///
/// Снимок контракта и условий приёмки хранится рядом с документом, а не
/// подтягивается из json-server: документ должен читаться и после того, как
/// карточку контракта изменят.
/// </summary>
public sealed class ContractAcceptanceRow
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
    public string? SupplierInn { get; set; }
    public string? Ikz { get; set; }
    public string? EisRegistryNumber { get; set; }
    public int? StageNumber { get; set; }
    public string? StageName { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string? DocumentNumber { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public string? CorrectedDocumentNumber { get; set; }
    public DateOnly? CorrectedDocumentDate { get; set; }
    public string? CorrectionReason { get; set; }

    public int AcceptanceTermDays { get; set; }
    public bool ExpertiseRequired { get; set; }
    public bool ExpertiseByThirdParty { get; set; }
    public string? ExpertiseOrganization { get; set; }
    public string? ExpertiseConclusionNumber { get; set; }
    public DateOnly? ExpertiseConclusionDate { get; set; }

    public bool CommissionRequired { get; set; }
    public string? CommissionDecisionNumber { get; set; }
    public DateOnly? CommissionDecisionDate { get; set; }

    public string? DeliveryPlace { get; set; }
    public string? Notes { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedBy { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public string? RegisteredBy { get; set; }
    public string? EisRegistrationNumber { get; set; }
    public DateOnly? EisRegistrationDate { get; set; }
    public string? RefusalReason { get; set; }
    public string? ReturnComment { get; set; }
    public string? CancelledReason { get; set; }

    /// <summary>
    /// Ревизия записи: увеличивается хранилищем при каждой успешной записи.
    /// Токен оптимистичной блокировки — известен документу с момента чтения.
    /// </summary>
    public int Revision { get; set; }

    /// <summary>Дополнительная защита на уровне PostgreSQL.</summary>
    public uint Xmin { get; set; }

    public List<AcceptancePositionRow> Positions { get; set; } = [];
    public List<AcceptanceDefectRow> Defects { get; set; } = [];
    public List<AcceptanceEventRow> Events { get; set; } = [];
}

public sealed class AcceptancePositionRow
{
    public Guid Id { get; set; }
    public Guid AcceptanceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Okpd2 { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public string? CountryOrigin { get; set; }
    public string? RegistryNumber { get; set; }
    public int OrderIndex { get; set; }
}

public sealed class AcceptanceDefectRow
{
    public Guid Id { get; set; }
    public Guid AcceptanceId { get; set; }
    public Guid? PositionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Requirement { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}

public sealed class AcceptanceEventRow
{
    public Guid Id { get; set; }
    public Guid AcceptanceId { get; set; }
    public DateTimeOffset At { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}
