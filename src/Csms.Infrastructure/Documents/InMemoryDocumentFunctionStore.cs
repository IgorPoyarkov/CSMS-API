using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Хранилище наборов в памяти по образцу InMemoryNpaRuleStore. Для первого
/// среза этого достаточно: версии и подтверждения живут до перезапуска API,
/// а весь набор можно сразу получить по sha256 документа.
/// </summary>
public sealed class InMemoryDocumentFunctionStore : IDocumentFunctionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DocumentFunctionSet>> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<DocumentFunctionSet>> GetVersionsAsync(
        string sha256, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DocumentFunctionSet>>(
                _versions.TryGetValue(sha256, out var sets) ? [.. sets] : []);
        }
    }

    public Task<DocumentFunctionSet?> GetActiveAsync(
        string sha256, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(sha256, out var sets) || sets.Count == 0)
                return Task.FromResult<DocumentFunctionSet?>(null);
            return Task.FromResult<DocumentFunctionSet?>(sets[^1]);
        }
    }

    public Task<DocumentFunctionSet> PublishAsync(
        DocumentFunctionSet set, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_versions.TryGetValue(set.Sha256, out var sets))
            {
                sets = [];
                _versions[set.Sha256] = sets;
            }

            if (sets.Any(existing => existing.Version == set.Version))
                return Task.FromResult(sets.First(existing => existing.Version == set.Version));

            sets.Add(set);
            return Task.FromResult(set);
        }
    }

    public Task<DocumentFunctionItem?> GetFunctionAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Find(id));
        }
    }

    public Task<DocumentFunctionItem?> VerifyAsync(
        Guid id, string status, string? note, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            DocumentFunctionItem? updated = null;

            // Один и тот же function_id может встречаться в нескольких версиях
            // одного документа. Подтверждение должно быть видно во всех:
            // иначе активная версия останется needs_review после проверки.
            foreach (var sets in _versions.Values)
            {
                for (var index = 0; index < sets.Count; index++)
                {
                    var set = sets[index];
                    var functions = set.Functions.ToArray();
                    var functionIndex = Array.FindIndex(functions, item => item.Id == id);
                    if (functionIndex < 0)
                        continue;

                    updated = functions[functionIndex] with
                    {
                        ReviewStatus = status,
                        ReviewComment = note,
                    };
                    functions[functionIndex] = updated;

                    sets[index] = set with { Functions = functions };
                }
            }

            return Task.FromResult(updated);
        }
    }

    private DocumentFunctionItem? Find(Guid id)
    {
        foreach (var sets in _versions.Values)
        {
            foreach (var set in sets)
            {
                var found = set.Functions.FirstOrDefault(item => item.Id == id);
                if (found is not null)
                    return found;
            }
        }
        return null;
    }
}
