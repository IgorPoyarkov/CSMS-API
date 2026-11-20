using Csms.Domain;

namespace Csms.Application;

/// <summary>Хранилище документов о приёмке.</summary>
public interface IAcceptanceRepository
{
    Task<IReadOnlyList<ContractAcceptance>> ListAsync(
        string? organizationId,
        string? contractId,
        AcceptanceStatus? status,
        CancellationToken cancellationToken);

    Task<ContractAcceptance?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(ContractAcceptance acceptance, CancellationToken cancellationToken);

    Task SaveAsync(ContractAcceptance acceptance, CancellationToken cancellationToken);
}

/// <summary>Конфликт одновременной правки: второй редактор должен обновить страницу.</summary>
public sealed class AcceptanceStoreConflictException(string message, Exception? inner = null)
    : Exception(message, inner);
