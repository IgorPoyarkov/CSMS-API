namespace Csms.Infrastructure;

/// <summary>Версия справочника в схеме ref. Старые версии остаются для истории.</summary>
public sealed class ReferenceDatasetRow
{
    public Guid Id { get; set; }

    /// <summary>Код из «Перечня справочников ЕИС»: nsiOKPD2, nsiKTRU, nsiKLADR...</summary>
    public string Code { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? SourceDocument { get; set; }

    public string? SourceUrl { get; set; }

    public string? SourceSha256 { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset ImportedAt { get; set; }

    public int ItemCount { get; set; }

    public bool IsActive { get; set; }

    public ICollection<ReferenceItemRow> Items { get; set; } = new List<ReferenceItemRow>();
}

/// <summary>
/// Строка справочника. Code — код ОКПД2/КТРУ/КЛАДР; ParentCode — родитель в
/// иерархии либо код ОКПД2 для позиции КТРУ. PayloadJson — полный объект из
/// официальной выгрузки, чтобы не терять поля, которые пока не нужны фронту.
/// </summary>
public sealed class ReferenceItemRow
{
    public Guid Id { get; set; }

    public Guid DatasetId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string? ParentCode { get; set; }

    public string? Name { get; set; }

    public bool Actual { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public ReferenceDatasetRow? Dataset { get; set; }
}
