namespace Csms.Application;

/// <summary>
/// Преобразует пункт пула 158н в структурированную обязанность Сущности А.
/// Реквизиты берутся из эталонного пула, чтобы гипотезы нарушений и поиск
/// норм строились на официальной формулировке функции контрактной службы.
/// </summary>
public static class LiabilityDutyFactory
{
    public const string LiabilityActCode = "koap";

    public static DutyInput FromOrder158n(Order158nPoolItemInfo item) => new(
        item.OfficialFormulation,
        item.DefaultTaskDescription,
        "должностное лицо контрактной службы заказчика",
        item.DeadlineBasis,
        item.CompletionCriteria,
        new DutyIdentifiers(
            item.Id,
            "order-158n",
            null,
            item.Clause,
            null,
            item.Clause),
        LiabilityActCode);
}

/// <summary>Сводка последнего сопоставления для списка на фронтенде.</summary>
public sealed record LiabilityMappingOverviewItem(
    string DutyId,
    string Clause,
    string SectionTitle,
    string Statement,
    bool HasResult,
    Guid? RunId,
    int CandidateCount,
    int MatchedCount,
    int ReviewCount,
    string? TopNormReference,
    string? TopMatchDegree,
    double? TopProbabilityScore,
    DateTimeOffset? CalculatedAt);
