using Csms.Application;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище пакетов в PostgreSQL (схема <c>eis</c>).
///
/// Поведение совпадает с <see cref="InMemoryEisPackageRepository"/>: те же
/// методы, тот же порядок списка (свежие сверху). Отличие в главном — состояние
/// переживает перезапуск сервиса и защищено от одновременной правки.
/// </summary>
public sealed class EfCoreEisPackageRepository(IDbContextFactory<EisDbContext> contextFactory)
    : IEisPackageRepository
{
    public async Task<IReadOnlyList<EisPackage>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Packages
            .AsNoTracking()
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var events = await LoadEventsAsync(db, rows.Select(row => row.Id), cancellationToken);

        return rows
            .Select(row => ToDomain(row, events.GetValueOrDefault(row.Id, [])))
            .ToList();
    }

    public async Task<EisPackage?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.Packages
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var events = await LoadEventsAsync(db, [id], cancellationToken);

        return ToDomain(row, events.GetValueOrDefault(id, []));
    }

    public async Task AddAsync(EisPackage package, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        db.Packages.Add(ToRow(package));

        var order = 0;

        foreach (var item in package.History)
        {
            db.Events.Add(ToEventRow(package.Id, order++, item));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(EisPackage package, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.Packages
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == package.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Пакет {package.Id} не найден в хранилище.");

        var storedEvents = await db.Events
            .AsNoTracking()
            .CountAsync(item => item.PackageId == package.Id, cancellationToken);

        MapInto(package, row);

        // Xmin остался тем, что прочитали: по нему пойдёт проверка в WHERE.
        // Если строку успел изменить другой поток, совпадать не будет
        db.Packages.Update(row);

        // Журнал только дописывается: уже сохранённые события не трогаем,
        // порядок задаёт OrderIndex, а не метка времени
        var order = storedEvents;

        foreach (var item in package.History.Skip(storedEvents))
        {
            db.Events.Add(ToEventRow(package.Id, order++, item));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new EisPackageStoreConflictException(
                "Пакет уже изменён в другом окне или другим процессом.",
                error);
        }
    }

    private static async Task<Dictionary<Guid, List<EisPackageEventRow>>> LoadEventsAsync(
        EisDbContext db,
        IEnumerable<Guid> packageIds,
        CancellationToken cancellationToken)
    {
        var ids = packageIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.Events
            .AsNoTracking()
            .Where(item => ids.Contains(item.PackageId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.PackageId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static EisPackage ToDomain(
        EisPackageRow row,
        IReadOnlyList<EisPackageEventRow> events) =>
        EisPackage.Restore(
            row.Id,
            Enum.Parse<EisDocumentType>(row.DocumentType, ignoreCase: true),
            row.ContractEisNumber,
            row.Ikz,
            row.PayloadXml,
            row.PayloadSchemaVersion,
            row.SignatureBase64,
            row.SignerThumbprint,
            row.SignedAt,
            row.EisGuid,
            Enum.Parse<EisPackageStatus>(row.Status, ignoreCase: true),
            row.LastError,
            row.FlkErrors,
            events
                .Select(item => new EisPackageEvent(
                    item.At,
                    Enum.Parse<EisPackageStatus>(item.From, ignoreCase: true),
                    Enum.Parse<EisPackageStatus>(item.To, ignoreCase: true),
                    item.Comment))
                .ToList(),
            row.CreatedAt,
            row.UpdatedAt,
            row.Source,
            row.Customer);

    private static EisPackageRow ToRow(EisPackage package) => new()
    {
        Id = package.Id,
        DocumentType = package.DocumentType.ToString(),
        ContractEisNumber = package.ContractEisNumber,
        Ikz = package.Ikz,
        PayloadXml = package.PayloadXml,
        PayloadSchemaVersion = package.PayloadSchemaVersion,
        SignatureBase64 = package.SignatureBase64,
        SignerThumbprint = package.SignerThumbprint,
        SignedAt = package.SignedAt,
        EisGuid = package.EisGuid,
        Status = package.Status.ToString(),
        LastError = package.LastError,
        CreatedAt = package.CreatedAt,
        UpdatedAt = package.UpdatedAt,
        FlkErrors = package.FlkErrors.ToList(),
        Source = package.Source,
        Customer = package.Customer,
    };

    private static void MapInto(EisPackage package, EisPackageRow row)
    {
        row.PayloadXml = package.PayloadXml;
        row.PayloadSchemaVersion = package.PayloadSchemaVersion;
        row.SignatureBase64 = package.SignatureBase64;
        row.SignerThumbprint = package.SignerThumbprint;
        row.SignedAt = package.SignedAt;
        row.EisGuid = package.EisGuid;
        row.Status = package.Status.ToString();
        row.LastError = package.LastError;
        row.UpdatedAt = package.UpdatedAt;
        row.FlkErrors = package.FlkErrors.ToList();

        // Source и Customer намеренно не перезаписываются: они фиксируются при
        // создании, а строка прочитана из БД вместе с ними. Повторная подпись
        // или возврат в работу не должны менять снимок заказчика.
    }

    private static EisPackageEventRow ToEventRow(Guid packageId, int order, EisPackageEvent item) => new()
    {
        Id = Guid.NewGuid(),
        PackageId = packageId,
        At = item.At,
        OrderIndex = order,
        From = item.From.ToString(),
        To = item.To.ToString(),
        Comment = item.Comment,
    };
}
