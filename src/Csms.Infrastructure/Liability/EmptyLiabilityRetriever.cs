using Csms.Application;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Заглушка для режима EIS_STORE=memory: без pgvector/FTS пул кандидатов пуст.
/// Контракт и эндпоинт при этом остаются рабочими и честно возвращают 0 норм.
/// </summary>
public sealed class EmptyLiabilityRetriever : ILiabilityRetriever
{
    public Task<IReadOnlyList<LiabilityCandidate>> RetrieveAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LiabilityCandidate>>([]);
}
