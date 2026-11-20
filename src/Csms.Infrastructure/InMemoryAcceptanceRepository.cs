using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище документов о приёмке в памяти.
///
/// Используется при <c>EIS_STORE=memory</c>: сценарий и правила те же, что и с
/// PostgreSQL, но записи не переживают перезапуск. Для разработки и тестов
/// этого достаточно, для пилота — нет.
/// </summary>
public sealed class InMemoryAcceptanceRepository : IAcceptanceRepository
{
    private readonly Dictionary<Guid, ContractAcceptance> _items = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<ContractAcceptance>> ListAsync(
        string? organizationId,
        string? contractId,
        AcceptanceStatus? status,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var items = _items.Values
                .Where(acceptance => Matches(acceptance.Input.OrganizationId, organizationId))
                .Where(acceptance => Matches(acceptance.Input.ContractId, contractId))
                .Where(acceptance => status is null || acceptance.Status == status)
                .OrderByDescending(acceptance => acceptance.CreatedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ContractAcceptance>>(items);
        }
    }

    public Task<ContractAcceptance?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _items.TryGetValue(id, out var acceptance);

            return Task.FromResult(acceptance);
        }
    }

    public Task AddAsync(ContractAcceptance acceptance, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryAdd(acceptance.Id, acceptance))
            {
                throw new InvalidOperationException($"Документ о приёмке {acceptance.Id} уже есть в хранилище.");
            }

            return Task.CompletedTask;
        }
    }

    public Task SaveAsync(ContractAcceptance acceptance, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(acceptance.Id))
            {
                throw new InvalidOperationException($"Документ о приёмке {acceptance.Id} не найден в хранилище.");
            }

            // Объект мутабельный и лежит в словаре тем же экземпляром.
            return Task.CompletedTask;
        }
    }

    private static bool Matches(string value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);
}
