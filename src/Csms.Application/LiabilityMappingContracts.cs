namespace Csms.Application;

/// <summary>Идентификаторы обязанности из Сущности А.</summary>
public sealed record DutyIdentifiers(
    string DutyId,
    string? SourceDocumentId,
    string? Article,
    string? Clause,
    string? Item,
    string? ExternalId);

/// <summary>Структурированная обязанность, подаваемая на сопоставление.</summary>
public sealed record DutyInput(
    string Statement,
    string Object,
    string Subject,
    string? Deadline,
    string? Condition,
    DutyIdentifiers Identifiers,
    string LiabilityActCode);

/// <summary>Вид гипотезы нарушения после семантической инверсии обязанности.</summary>
public enum ViolationKind
{
    Omission,
    DeadlineViolation,
    ProcedureOrFormViolation,
    SubjectOrAuthorityViolation
}

/// <summary>Гипотеза возможного нарушения, построенная из позитивной обязанности.</summary>
public sealed record ViolationHypothesis(
    string Id,
    ViolationKind Kind,
    string Text,
    string DutyId);

/// <summary>Тип нормы ответственности: специальная, общая, бланкетная и т.д.</summary>
public enum LiabilityNormType
{
    Special,
    General,
    Blanket,
    Procedural,
    Other
}

/// <summary>Источник попадания нормы в пул кандидатов.</summary>
public enum CandidateSource
{
    Vector,
    FullText,
    Umbrella,
    Merged
}

/// <summary>
/// Снимок структурной единицы Сущности Б, который видит LLM-арбитр.
/// Вне этого снимка арбитр не знает ни одной нормы.
/// </summary>
public sealed record LiabilityCandidate(
    string LiabilityNodeId,
    string NormReference,
    string Text,
    string ActCode,
    string ActVersion,
    LiabilityNormType NormType,
    string Status,
    DateOnly? EditionDate,
    IReadOnlyDictionary<string, string> Metadata,
    CandidateSource Source,
    double SearchScore,
    string? ChunkId = null);

/// <summary>Степень совпадения: факт соответствия состава, а не условия ответственности.</summary>
public enum MatchDegree
{
    Full,
    Partial,
    Blanket,
    Irrelevant
}

/// <summary>Итоговая связь обязанности с одной нормой ответственности.</summary>
public sealed record MatchedLiability(
    string LiabilityNodeId,
    string NormReference,
    MatchDegree MatchDegree,
    double ProbabilityScore,
    IReadOnlyList<string> QualifyingConditions,
    string SanctionSummary,
    string LegalReasoning,
    bool RequiresHumanReview);

/// <summary>Результат сопоставления одной обязанности со многими нормами.</summary>
public sealed record LiabilityMappingResult(
    string DutyId,
    Guid RunId,
    IReadOnlyList<ViolationHypothesis> Hypotheses,
    IReadOnlyList<MatchedLiability> MatchedNorms,
    int CandidateCount,
    string ModelVersion,
    string PromptVersion,
    DateTimeOffset CalculatedAt,
    bool RequiresHumanReview);

/// <summary>Одна оценка нормы в строгом JSON-ответе арбитра.</summary>
public sealed record LlmArbiterItem(
    string LiabilityNodeId,
    MatchDegree MatchDegree,
    double ProbabilityScore,
    IReadOnlyList<string> QualifyingConditions,
    string SanctionSummary,
    string LegalReasoning,
    bool RequiresHumanReview);

/// <summary>Строгий JSON-ответ арбитра: только оценки кандидатов.</summary>
public sealed record LlmArbiterResponse(IReadOnlyList<LlmArbiterItem> Results);

/// <summary>Результат валидации ответа LLM и человекочитаемые нарушения.</summary>
public sealed record ValidationOutcome(
    IReadOnlyList<MatchedLiability> Accepted,
    IReadOnlyList<string> Violations,
    bool NeedsManualReviewFallback,
    bool HasHardViolations);

/// <summary>Запись аудита вызова LLM (или пропущенного вызова).</summary>
public sealed record LlmAuditEntry(
    Guid Id,
    Guid RunId,
    string DutyId,
    string Stage,
    string Model,
    double Temperature,
    string PromptVersion,
    int Attempt,
    string RequestJson,
    string? ResponseJson,
    string? ErrorText,
    int DurationMs,
    string ValidationResult,
    DateTimeOffset CreatedAt);

/// <summary>Получение эмбеддингов локальной моделью — без внешних ключей и сети.</summary>
public interface ILiabilityEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Минимальный контракт вызова LLM с фиксированной температурой 0.</summary>
public interface ILlmCompletionClient
{
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}

/// <summary>Этап 1: детерминированная инверсия обязанности в гипотезы нарушений.</summary>
public interface IViolationHypothesisGenerator
{
    IReadOnlyList<ViolationHypothesis> Generate(DutyInput duty);
}

/// <summary>Этап 2: гибридный поиск с принудительным включением общих норм.</summary>
public interface ILiabilityRetriever
{
    Task<IReadOnlyList<LiabilityCandidate>> RetrieveAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        CancellationToken cancellationToken = default);
}

/// <summary>Этап 3: правовой арбитр, батчинг и retries внутри реализации.</summary>
public interface ILlmArbiter
{
    Task<LlmArbiterResponse> ArbitrateAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        IReadOnlyList<LiabilityCandidate> candidates,
        Guid runId,
        CancellationToken cancellationToken = default);
}

/// <summary>Валидация строгого JSON и правовых ограничений вывода.</summary>
public interface IMappingValidator
{
    ValidationOutcome Validate(
        LlmArbiterResponse response,
        IReadOnlyList<LiabilityCandidate> candidates);
}

/// <summary>Хранилище кандидатов, связей и аудита; обеспечивает идемпотентность.</summary>
public interface ILiabilityMappingStore
{
    Task<bool> HasCompletedRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task SaveCandidatesAsync(
        Guid runId,
        string dutyId,
        IReadOnlyList<LiabilityCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LiabilityCandidate>> GetCandidatesAsync(
        Guid runId,
        string dutyId,
        CancellationToken cancellationToken = default);

    Task SaveMappingsAsync(Guid runId, LiabilityMappingResult result, CancellationToken cancellationToken = default);

    Task<LiabilityMappingResult?> GetResultAsync(Guid runId, string dutyId, CancellationToken cancellationToken = default);

    Task<LiabilityMappingResult?> GetLatestByDutyAsync(string dutyId, CancellationToken cancellationToken = default);

    Task WriteAuditAsync(LlmAuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>Оркестратор конвейера «одна обязанность → многие нормы ответственности».</summary>
public interface IDutyLiabilityMappingService
{
    Task<LiabilityMappingResult> MapAsync(DutyInput duty, CancellationToken cancellationToken = default);

    Task<LiabilityMappingResult?> GetAsync(string dutyId, CancellationToken cancellationToken = default);
}
