using Csms.Domain;

namespace Csms.Application;

/// <summary>Хранилище сведений о привлечении СМП/СОНКО по контрактам.</summary>
public interface IContractSmpRecordRepository
{
    Task<IReadOnlyList<ContractSmpRecord>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken);

    Task<ContractSmpRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(ContractSmpRecord record, CancellationToken cancellationToken);

    Task SaveAsync(ContractSmpRecord record, CancellationToken cancellationToken);

    Task DeleteAsync(ContractSmpRecord record, CancellationToken cancellationToken);
}

/// <summary>Конфликт одновременной правки: второй редактор должен обновить страницу.</summary>
public sealed class ContractSmpStoreConflictException(string message, Exception? inner = null)
    : Exception(message, inner);
