using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище пакетов в памяти.
///
/// Годится для разработки и тестов: перезапуск сервиса очищает список.
/// Заменяется на БД без изменений в прикладном слое — он видит только
/// <see cref="IEisPackageRepository"/>.
/// </summary>
public sealed class InMemoryEisPackageRepository : IEisPackageRepository
{
    private readonly Dictionary<Guid, EisPackage> _items = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<EisPackage>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ordered = _items.Values
                .OrderByDescending(package => package.CreatedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<EisPackage>>(ordered);
        }
    }

    public Task<EisPackage?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _items.TryGetValue(id, out var package);

            return Task.FromResult(package);
        }
    }

    public Task AddAsync(EisPackage package, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.TryAdd(package.Id, package))
            {
                throw new InvalidOperationException($"Пакет {package.Id} уже есть в хранилище.");
            }

            return Task.CompletedTask;
        }
    }

    public Task SaveAsync(EisPackage package, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(package.Id))
            {
                throw new InvalidOperationException($"Пакет {package.Id} не найден в хранилище.");
            }

            // Объект мутабельный и лежит в словаре тем же экземпляром: сохранять
            // его отдельно не нужно. Метод оставлен осознанно — с настоящей БД
            // здесь появится Update, и вызывающий код не придётся менять
            return Task.CompletedTask;
        }
    }
}
