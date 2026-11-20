using Csms.Domain;

namespace Csms.Application;

/// <summary>Хранилище документов об изменении и расторжении контрактов.</summary>
public interface IContractChangeRepository
{
    Task<IReadOnlyList<ContractChange>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken);

    Task<ContractChange?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(ContractChange change, CancellationToken cancellationToken);

    Task SaveAsync(ContractChange change, CancellationToken cancellationToken);
}

/// <summary>Конфликт одновременной правки: второй редактор должен обновить страницу.</summary>
public sealed class ContractChangeStoreConflictException(string message, Exception? inner = null)
    : Exception(message, inner);
