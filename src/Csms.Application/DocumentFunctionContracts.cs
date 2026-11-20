namespace Csms.Application;

/// <summary>
/// Таксономия функций контрактной службы. Коды совпадают с
/// ContractServiceFunctionSchema на фронте — новый справочник не выдумываем.
/// </summary>
public static class DocumentFunctionTaxonomy
{
    public const string Planning = "planning";
    public const string PurchaseDescription = "purchase_description";
    public const string SupplierDetermination = "supplier_determination";
    public const string ContractConclusion = "contract_conclusion";
    public const string ContractExecution = "contract_execution";
    public const string Other = "other";

    public const string Version = "csms-contract-service-v1";

    public static readonly string[] Codes =
    [
        Planning,
        PurchaseDescription,
        SupplierDetermination,
        ContractConclusion,
        ContractExecution,
        Other,
    ];
}

/// <summary>Характер предписания: обязанность, право или информационный абзац.</summary>
public static class DocumentFunctionObligations
{
    public const string Mandatory = "mandatory";
    public const string Discretionary = "discretionary";
    public const string Informational = "informational";
}

/// <summary>Статус извлечённой задачи: ждёт человека или подтверждена/отклонена.</summary>
public static class DocumentFunctionReviewStatuses
{
    public const string NeedsReview = "needs_review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

/// <summary>Метаданные разбираемого документа.</summary>
public sealed record DocumentFunctionDocumentMeta(
    string Sha256,
    string FileName,
    string Title,
    string? Number,
    DateOnly? DocumentDate,
    string TaxonomyVersion,
    string PromptVersion);

/// <summary>Родительский заголовок с его текстом — «хлебные крошки» сегмента.</summary>
public sealed record DocumentSegmentAncestor(string Path, string Text);

/// <summary>
/// Логический блок документа: пункт, подпункт, абзац со всеми предками.
/// </summary>
public sealed record DocumentSegment(
    string SegmentId,
    string SourcePath,
    IReadOnlyList<DocumentSegmentAncestor> Ancestors,
    int? Page,
    int CharStart,
    int CharEnd,
    string Text);

/// <summary>Атомарная подзадача с обратной ссылкой на цитату.</summary>
public sealed record ExtractedDocumentSubtask(
    string SubtaskId,
    string ActionVerb,
    string TargetObject,
    string Description,
    string? TriggerCondition,
    string? DeadlineRule,
    string SourceQuote,
    bool NeedsReview,
    double Confidence);

/// <summary>Функция, собранная из одного или нескольких сегментов.</summary>
public sealed record ExtractedDocumentFunction(
    string FunctionId,
    string ParentFunctionTitle,
    string Category,
    double CategoryConfidence,
    string? SuggestedCategory,
    string? ActorRole,
    string Obligation,
    bool IsDecomposed,
    IReadOnlyList<ExtractedDocumentSubtask> Subtasks);

/// <summary>Отчёт по одному сегменту — нужен для coverage ledger.</summary>
public sealed record DocumentSegmentReport(
    int SegmentsTotal,
    int SegmentsWithFunctions,
    bool Informational,
    int Candidates,
    int QuotesRejected,
    int NeedsReview);

/// <summary>Ответ Map по одному сегменту (или basename батча).</summary>
public sealed record DocumentSegmentExtraction(
    string SegmentId,
    string SourcePath,
    string Status,
    IReadOnlyList<ExtractedDocumentFunction> ExtractedFunctions,
    DocumentSegmentReport Report);

/// <summary>Итоговая функция после Reduce: все ссылки на пункты сохранены.</summary>
public sealed record DocumentFunctionItem(
    Guid Id,
    string FunctionKey,
    string ParentFunctionTitle,
    string Category,
    string? SuggestedCategory,
    string? ActorRole,
    string Obligation,
    string MergeReason,
    IReadOnlyList<string> SourcePaths,
    IReadOnlyList<DocumentFunctionSubtaskItem> Subtasks,
    string ReviewStatus,
    string? ReviewComment);

/// <summary>Подзадача в итоговом наборе.</summary>
public sealed record DocumentFunctionSubtaskItem(
    Guid Id,
    string SubtaskKey,
    string ActionVerb,
    string TargetObject,
    string Description,
    string? TriggerCondition,
    string? DeadlineRule,
    string SourceQuote,
    IReadOnlyList<string> SourcePaths,
    bool NeedsReview,
    double Confidence);

/// <summary>Сводный отчёт прогона.</summary>
public sealed record DocumentExtractionReport(
    int SegmentsTotal,
    int Processed,
    IReadOnlyList<string> FailedSegments,
    IReadOnlyList<string> UncoveredSegments,
    int CandidatesBefore,
    int FunctionsAfter,
    int DuplicatesMerged,
    int NeedsReview,
    int QuotesRejected);

/// <summary>Версия набора функций по одному документу.</summary>
public sealed record DocumentFunctionSet(
    string Sha256,
    string Version,
    DocumentFunctionDocumentMeta Document,
    string Model,
    IReadOnlyList<DocumentFunctionItem> Functions,
    DocumentExtractionReport Report,
    DateTimeOffset CreatedAt);

/// <summary>Результат dryRun: набор ещё не сохранён.</summary>
public sealed record DocumentExtractionResponse(
    string Sha256,
    string Version,
    string Status,
    DocumentFunctionDocumentMeta Document,
    IReadOnlyList<DocumentFunctionItem> Functions,
    DocumentExtractionReport Report);
