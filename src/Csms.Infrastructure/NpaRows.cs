namespace Csms.Infrastructure;

/// <summary>
/// Версия правил по официальному акту в схеме ref. Как и у справочников НСИ,
/// старые версии остаются в истории: по ним можно понять, какое правило
/// действовало на дату документа закупки.
/// </summary>
public sealed class NpaDocumentRow
{
    public Guid Id { get; set; }

    /// <summary>Код акта внутри системы: rp-471, pp-1875, pp-2604.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Версия: pravo-&lt;eoNumber&gt; для выгрузки с портала, file-&lt;sha16&gt; для файла.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Идентификатор официального опубликования на publication.pravo.gov.ru.</summary>
    public string? EoNumber { get; set; }

    /// <summary>Номер акта без года: 471-р, 1875, 2604.</summary>
    public string? Number { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateOnly? DocumentDate { get; set; }

    public DateOnly? PublishDate { get; set; }

    public int PagesCount { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>SHA-256 исходного PDF: по нему агент понимает, что текст изменился.</summary>
    public string? PdfSha256 { get; set; }

    /// <summary>Чем распознан текст: tesseract 5.4 (rus) и т.п. Нужно для аудита.</summary>
    public string? OcrEngine { get; set; }

    /// <summary>Когда эта версия правил была опубликована («обновлено»).</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>
    /// Когда официальный текст последний раз проверялся («проверено»).
    /// Отличается от FetchedAt: при неизменном тексте новая версия не создаётся.
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>up_to_date / published / prepared / error.</summary>
    public string? LastCheckStatus { get; set; }

    /// <summary>Короткий итог последней проверки для реестра актов.</summary>
    public string? LastCheckMessage { get; set; }

    public int ItemCount { get; set; }

    /// <summary>
    /// Строки, которые агент не смог превратить в код: jsonb-массив
    /// { page, row, text, reason }. Честный след вместо молчаливой потери.
    /// </summary>
    public string? UnresolvedJson { get; set; }

    public bool IsActive { get; set; }

    public ICollection<NpaRuleRow> Rules { get; set; } = new List<NpaRuleRow>();
}

/// <summary>
/// Правило «код ОКПД2 → тип ограничения» с цитатой из акта. Унаследованные
/// правила материализуются: фронту не нужно строить дерево ОКПД2 самому.
/// </summary>
public sealed class NpaRuleRow
{
    public Guid Id { get; set; }

    public Guid DocumentId { get; set; }

    public string Okpd2Code { get; set; } = string.Empty;

    /// <summary>include / exclude / conditional (NpaRuleTypes).</summary>
    public string RuleType { get; set; } = string.Empty;

    /// <summary>Выжимка условия: «кроме кодов 01.4, 01.7», «только при цене до ...».</summary>
    public string? ConditionText { get; set; }

    /// <summary>Дословная цитата из акта, по которой принято решение.</summary>
    public string? Quote { get; set; }

    public bool IsInherited { get; set; }

    /// <summary>Код-родитель, от которого правило унаследовано.</summary>
    public string? InheritedFrom { get; set; }

    public double Confidence { get; set; }

    /// <summary>auto / needs_review / approved / rejected (NpaRuleStatuses).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Комментарий проверяющего или причина отправки на проверку.</summary>
    public string? Note { get; set; }

    public int? SourcePage { get; set; }

    public int? SourceRow { get; set; }

    public NpaDocumentRow? Document { get; set; }
}
