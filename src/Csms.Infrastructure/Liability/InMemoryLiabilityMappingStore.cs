using Csms.Application;

namespace Csms.Infrastructure.Liability;

/// <summary>
/// Хранилище для режима memory и тестов: та же семантика, что у PostgreSQL,
/// включая идемпотентный run_id и omission-фолбэк (норма не пропадает молча).
/// </summary>
public sealed class InMemoryLiabilityMappingStore : ILiabilityMappingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<LiabilityCandidate>> _candidates = [];
    private readonly Dictionary<string, LiabilityMappingResult> _results = [];
    private readonly List<LlmAuditEntry> _audits = [];

    public Task<bool> HasCompletedRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_results.Values.Any(result => result.RunId == runId));
        }
    }

    public Task SaveCandidatesAsync(
        Guid runId,
        string dutyId,
        IReadOnlyList<LiabilityCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _candidates[Key(runId, dutyId)] = candidates.ToArray();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiabilityCandidate>> GetCandidatesAsync(
        Guid runId,
        string dutyId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _candidates.TryGetValue(Key(runId, dutyId), out var candidates)
                    ? candidates
                    : (IReadOnlyList<LiabilityCandidate>)[]);
        }
    }

    public Task SaveMappingsAsync(
        Guid runId,
        LiabilityMappingResult result,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var existing in _results
                         .Where(pair => pair.Value.DutyId == result.DutyId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _results.Remove(existing);
            }

            _results[Key(runId, result.DutyId)] = result;
        }

        return Task.CompletedTask;
    }

    public Task<LiabilityMappingResult?> GetResultAsync(
        Guid runId,
        string dutyId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _results.TryGetValue(Key(runId, dutyId), out var result) ? result : null);
        }
    }

    public Task<LiabilityMappingResult?> GetLatestByDutyAsync(
        string dutyId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_results.Values
                .Where(result => result.DutyId == dutyId)
                .OrderByDescending(result => result.CalculatedAt)
                .FirstOrDefault());
        }
    }

    public Task WriteAuditAsync(LlmAuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _audits.Add(entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>Только для тестов: сколько попыток арбитра записано в аудит.</summary>
    public IReadOnlyList<LlmAuditEntry> Audits
    {
        get
        {
            lock (_gate)
            {
                return _audits.ToArray();
            }
        }
    }

    private static string Key(Guid runId, string dutyId) => $"{runId}:{dutyId}";
}
