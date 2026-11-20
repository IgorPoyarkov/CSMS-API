using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище сведений о привлечении СМП в памяти (EIS_STORE=memory).
/// Сценарии и правила те же, что и с PostgreSQL, но записи не переживают
/// перезапуск процесса.
/// </summary>
public sealed class InMemoryContractSmpRecordRepository : IContractSmpRecordRepository
{
    private readonly Dictionary<Guid, ContractSmpRecord> _items = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<ContractSmpRecord>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var items = _items.Values
                .Where(record => Matches(record.Input.OrganizationId, organizationId))
                .Where(record => Matches(record.Input.ContractId, contractId))
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContractSmpRecord>>(items);
        }
    }

    public Task<ContractSmpRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _items.TryGetValue(id, out var record);

            return Task.FromResult(record);
        }
    }

    public Task AddAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryAdd(record.Id, record))
            {
                throw new InvalidOperationException($"Запись {record.Id} уже есть в хранилище.");
            }

            return Task.CompletedTask;
        }
    }

    public Task SaveAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(record.Id))
            {
                throw new InvalidOperationException($"Запись {record.Id} не найдена в хранилище.");
            }

            // Объект мутабельный и лежит в словаре тем же экземпляром.
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.Remove(record.Id))
            {
                throw new InvalidOperationException($"Запись {record.Id} не найдена в хранилище.");
            }

            return Task.CompletedTask;
        }
    }

    private static bool Matches(string value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);
}
