namespace Csms.Application;

/// <summary>Один этап типового маршрута исполнения задачи по 158н.</summary>
public sealed record Order158nStepInfo(
    string StageLabel,
    string Title,
    string Detail,
    string ExpectedArtifact,
    double RelativeProgress);

/// <summary>
/// Пункт пула функций и типовых задач контрактной службы по Приказу № 158н.
/// Пул целиком хранится в payload справочника <c>order158nPool</c>:
/// так версия пула живёт в БД и не дублируется в клиентском бандле.
/// </summary>
public sealed record Order158nPoolItemInfo(
    string Id,
    string Clause,
    string SectionTitle,
    string FunctionCode,
    string OfficialFormulation,
    string DefaultTaskTitle,
    string DefaultTaskDescription,
    int RecommendedDays,
    string DeadlineBasis,
    string LegalReference,
    string? AppRoute,
    string? AppRouteLabel,
    IReadOnlyList<string> RequiredInputs,
    string CompletionCriteria,
    IReadOnlyList<Order158nStepInfo> Steps,
    int SortIndex);

/// <summary>Активная версия пула 158н с реквизитами официального источника.</summary>
public sealed record Order158nPoolSnapshot(
    string Version,
    string SourceDocument,
    string? SourceUrl,
    string? SourceSha256,
    DateTimeOffset ImportedAt,
    IReadOnlyList<Order158nPoolItemInfo> Items);

/// <summary>
/// Источник пула для API и агентов: читает активную версию из хранилища
/// справочников. Первичное заполнение делает сам каталог, если версии ещё нет.
/// </summary>
public interface IOrder158nPoolCatalog
{
    Task<Order158nPoolSnapshot> GetAsync(CancellationToken cancellationToken = default);
}
