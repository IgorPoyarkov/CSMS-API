using Csms.Domain;

namespace Csms.Application;

/// <summary>Хранилище пакетов. Сейчас в памяти, потом — БД.</summary>
public interface IEisPackageRepository
{
    Task<IReadOnlyList<EisPackage>> ListAsync(CancellationToken cancellationToken);

    Task<EisPackage?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(EisPackage package, CancellationToken cancellationToken);

    Task SaveAsync(EisPackage package, CancellationToken cancellationToken);
}
