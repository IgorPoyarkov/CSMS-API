using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище изменений и расторжений в памяти.
///
/// Используется при <c>EIS_STORE=memory</c>: сценарий и правила те же, что и с
/// PostgreSQL, но записи не переживают перезапуск. Для разработки и тестов
/// этого достаточно, для пилота — нет.
/// </summary>
public sealed class InMemoryContractChangeRepository : IContractChangeRepository
{
    private readonly Dictionary<Guid, ContractChange> _items = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<ContractChange>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var items = _items.Values
                .Where(change => Matches(change.Input.OrganizationId, organizationId))
                .Where(change => Matches(change.Input.ContractId, contractId))
                .OrderByDescending(change => change.CreatedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContractChange>>(items);
        }
    }

    public Task<ContractChange?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _items.TryGetValue(id, out var change);

            return Task.FromResult(change);
        }
    }

    public Task AddAsync(ContractChange change, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryAdd(change.Id, change))
            {
                throw new InvalidOperationException($"Документ {change.Id} уже есть в хранилище.");
            }

            return Task.CompletedTask;
        }
    }

    public Task SaveAsync(ContractChange change, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(change.Id))
            {
                throw new InvalidOperationException($"Документ {change.Id} не найден в хранилище.");
            }

            // Объект мутабельный и лежит в словаре тем же экземпляром.
            return Task.CompletedTask;
        }
    }

    private static bool Matches(string value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);
}
