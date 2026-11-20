namespace Csms.Infrastructure;

/// <summary>
/// Строка сведений о привлечении СМП/СОНКО (схема <c>contract</c>).
///
/// Снимок контракта хранится рядом с записью: карточка в реестре может
/// измениться, а сведения должны читаться в том виде, в каком их подавали.
/// </summary>
public sealed class ContractSmpRecordRow
{
    public Guid Id { get; set; }

    public string OrganizationId { get; set; } = string.Empty;
    public string ContractId { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ContractName { get; set; } = string.Empty;
    public decimal ContractPrice { get; set; }
    public decimal? RequiredPercent { get; set; }
    public string? EisRegistryNumber { get; set; }

    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public DateOnly DocumentDate { get; set; }
    public decimal? Amount { get; set; }
    public string? Okpd2Code { get; set; }
    public string PriceType { get; set; } = string.Empty;
    public decimal? UnitPrice { get; set; }
    public string? Subject { get; set; }
    public string? Period { get; set; }

    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierInn { get; set; }
    public string? SupplierKpp { get; set; }
    public bool IsSmpSubject { get; set; }
    public string? SubContractRecordId { get; set; }
    public bool IsReplacementSubContract { get; set; }
    public bool PaidEarlierThanContractSchedule { get; set; }
    public DateOnly? PaidToSupplierDate { get; set; }
    public DateOnly? AcceptanceSignedDate { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? SourceFileName { get; set; }

    public DateTimeOffset? SupplierConfirmedAt { get; set; }
    public string? SupplierConfirmedBy { get; set; }
    public string? SupplierConfirmedDocument { get; set; }
    public DateTimeOffset? CustomerConfirmedAt { get; set; }
    public string? CustomerConfirmedBy { get; set; }
    public string? CustomerConfirmedDocument { get; set; }
    public string? Comment { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectedReason { get; set; }
    public DateTimeOffset? PushedAt { get; set; }
    public string? PushedBy { get; set; }
    public DateOnly? EisPublishDate { get; set; }
    public string? EisRecordNumber { get; set; }

    /// <summary>Токен оптимистичной блокировки, известный домену с момента чтения.</summary>
    public int Revision { get; set; }

    /// <summary>Дополнительная блокировка PostgreSQL.</summary>
    public uint Xmin { get; set; }

    public List<ContractSmpDocumentRow> Documents { get; set; } = [];
    public List<ContractSmpEventRow> Events { get; set; } = [];
}

public sealed class ContractSmpDocumentRow
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int OrderIndex { get; set; }
}

public sealed class ContractSmpEventRow
{
    public Guid Id { get; set; }
    public Guid RecordId { get; set; }
    public DateTimeOffset At { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}
