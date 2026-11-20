using Csms.Application;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище сведений о привлечении СМП в PostgreSQL (схема <c>contract</c>).
///
/// Поведение совпадает с <see cref="InMemoryContractSmpRecordRepository"/>: те же
/// фильтры и порядок списка (свежие сверху). Состояние переживает перезапуск, а
/// ревизия и xmin защищают от двух одновременных правок одной записи.
/// </summary>
public sealed class EfCoreContractSmpRecordRepository(IDbContextFactory<EisDbContext> contextFactory)
    : IContractSmpRecordRepository
{
    public async Task<IReadOnlyList<ContractSmpRecord>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ContractSmpRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            var value = organizationId.Trim();
            query = query.Where(row => row.OrganizationId == value);
        }

        if (!string.IsNullOrWhiteSpace(contractId))
        {
            var value = contractId.Trim();
            query = query.Where(row => row.ContractId == value);
        }

        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var ids = rows.Select(row => row.Id).ToArray();

        var documents = await LoadDocumentsAsync(db, ids, cancellationToken);
        var events = await LoadEventsAsync(db, ids, cancellationToken);

        return rows
            .Select(row => ToDomain(
                row,
                documents.GetValueOrDefault(row.Id, []),
                events.GetValueOrDefault(row.Id, [])))
            .ToList();
    }

    public async Task<ContractSmpRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractSmpRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var documents = await LoadDocumentsAsync(db, [id], cancellationToken);
        var events = await LoadEventsAsync(db, [id], cancellationToken);

        return ToDomain(
            row,
            documents.GetValueOrDefault(id, []),
            events.GetValueOrDefault(id, []));
    }

    public async Task AddAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        db.ContractSmpRecords.Add(ToRow(record));

        var documentOrder = 0;

        foreach (var document in record.Documents)
        {
            db.ContractSmpDocuments.Add(ToDocumentRow(record.Id, documentOrder++, document));
        }

        var eventOrder = 0;

        foreach (var item in record.Events)
        {
            db.ContractSmpEvents.Add(ToEventRow(record.Id, eventOrder++, item));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractSmpRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == record.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Запись {record.Id} не найдена в хранилище.");

        var storedDocuments = await db.ContractSmpDocuments
            .AsNoTracking()
            .Where(item => item.RecordId == record.Id)
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        var storedEvents = await db.ContractSmpEvents
            .AsNoTracking()
            .CountAsync(item => item.RecordId == record.Id, cancellationToken);

        var expectedRevision = record.Revision;

        MapInto(record, row);
        row.Revision = expectedRevision + 1;

        var entry = db.ContractSmpRecords.Update(row);
        entry.Property(item => item.Revision).OriginalValue = expectedRevision;

        SyncDocuments(db, record, storedDocuments);
        SyncEvents(db, record, storedEvents);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new ContractSmpStoreConflictException(
                "Сведения уже изменены в другом окне или другим процессом.",
                error);
        }

        record.AcceptRevision(expectedRevision + 1);
    }

    public async Task DeleteAsync(ContractSmpRecord record, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractSmpRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == record.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Запись {record.Id} не найдена в хранилище.");

        var entry = db.Entry(row);
        entry.Property(item => item.Revision).OriginalValue = record.Revision;

        db.ContractSmpRecords.Remove(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new ContractSmpStoreConflictException(
                "Сведения уже изменены в другом окне или другим процессом.",
                error);
        }
    }

    private static void SyncDocuments(
        EisDbContext db,
        ContractSmpRecord record,
        IReadOnlyList<ContractSmpDocumentRow> stored)
    {
        var actual = record.Documents.Select(document => document.Id).ToHashSet();

        foreach (var row in stored.Where(row => !actual.Contains(row.Id)))
        {
            db.ContractSmpDocuments.Remove(row);
        }

        var known = stored.Select(row => row.Id).ToHashSet();
        var order = stored.Count;

        foreach (var document in record.Documents.Where(document => !known.Contains(document.Id)))
        {
            db.ContractSmpDocuments.Add(ToDocumentRow(record.Id, order++, document));
        }
    }

    /// <summary>Журнал только дописывается: уже сохранённые события не трогаем.</summary>
    private static void SyncEvents(EisDbContext db, ContractSmpRecord record, int storedCount)
    {
        var order = storedCount;

        foreach (var item in record.Events.Skip(storedCount))
        {
            db.ContractSmpEvents.Add(ToEventRow(record.Id, order++, item));
        }
    }

    private static async Task<Dictionary<Guid, List<ContractSmpDocumentRow>>> LoadDocumentsAsync(
        EisDbContext db,
        IEnumerable<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        var ids = recordIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.ContractSmpDocuments
            .AsNoTracking()
            .Where(item => ids.Contains(item.RecordId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.RecordId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static async Task<Dictionary<Guid, List<ContractSmpEventRow>>> LoadEventsAsync(
        EisDbContext db,
        IEnumerable<Guid> recordIds,
        CancellationToken cancellationToken)
    {
        var ids = recordIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.ContractSmpEvents
            .AsNoTracking()
            .Where(item => ids.Contains(item.RecordId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.RecordId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static ContractSmpRecord ToDomain(
        ContractSmpRecordRow row,
        IReadOnlyList<ContractSmpDocumentRow> documents,
        IReadOnlyList<ContractSmpEventRow> events)
    {
        var input = new ContractSmpRecordInput(
            row.OrganizationId,
            row.ContractId,
            row.ContractNumber,
            row.ContractName,
            row.ContractPrice,
            row.RequiredPercent,
            row.EisRegistryNumber,
            Enum.Parse<ContractSmpDocumentType>(row.DocumentType, ignoreCase: true),
            row.DocumentNumber,
            row.DocumentDate,
            row.Amount,
            row.Okpd2Code,
            Enum.Parse<ContractSmpPriceType>(row.PriceType, ignoreCase: true),
            row.UnitPrice,
            row.Subject,
            row.Period,
            row.SupplierName,
            row.SupplierInn,
            row.SupplierKpp,
            row.IsSmpSubject,
            row.SubContractRecordId,
            row.IsReplacementSubContract,
            row.PaidEarlierThanContractSchedule,
            row.PaidToSupplierDate,
            row.AcceptanceSignedDate,
            row.ReceivedAt,
            row.SourceFileName,
            row.SupplierConfirmedAt,
            row.SupplierConfirmedBy,
            row.SupplierConfirmedDocument,
            row.CustomerConfirmedAt,
            row.CustomerConfirmedBy,
            row.CustomerConfirmedDocument,
            row.EisPublishDate,
            row.EisRecordNumber,
            row.Comment,
            row.CreatedBy);

        var restoredDocuments = documents
            .Select(document => ContractSmpDocument.Restore(
                document.Id,
                Enum.Parse<ContractSmpDocumentType>(document.Type, ignoreCase: true),
                document.Title,
                document.Content,
                document.Version,
                document.Sha256,
                document.CreatedBy,
                document.CreatedAt))
            .ToList();

        var restoredEvents = events
            .Select(item => new ContractSmpEvent(
                item.At,
                Enum.Parse<ContractSmpStatus>(item.From, ignoreCase: true),
                Enum.Parse<ContractSmpStatus>(item.To, ignoreCase: true),
                item.Action,
                item.Comment,
                item.Actor))
            .ToList();

        return ContractSmpRecord.Restore(
            row.Id,
            input,
            Enum.Parse<ContractSmpStatus>(row.Status, ignoreCase: true),
            restoredDocuments,
            restoredEvents,
            row.CreatedAt,
            row.UpdatedAt,
            row.SubmittedAt,
            row.AcceptedAt,
            row.AcceptedBy,
            row.RejectedAt,
            row.RejectedReason,
            row.PushedAt,
            row.PushedBy,
            row.EisPublishDate,
            row.EisRecordNumber,
            row.Revision);
    }

    private static ContractSmpRecordRow ToRow(ContractSmpRecord record)
    {
        var row = new ContractSmpRecordRow();

        MapInto(record, row);
        row.Id = record.Id;
        row.Revision = record.Revision;

        return row;
    }

    private static void MapInto(ContractSmpRecord record, ContractSmpRecordRow row)
    {
        row.OrganizationId = record.Input.OrganizationId;
        row.ContractId = record.Input.ContractId;
        row.ContractNumber = record.Input.ContractNumber;
        row.ContractName = record.Input.ContractName;
        row.ContractPrice = record.Input.ContractPrice;
        row.RequiredPercent = record.Input.RequiredPercent;
        row.EisRegistryNumber = record.Input.EisRegistryNumber;
        row.DocumentType = record.Input.DocumentType.ToString();
        row.DocumentNumber = record.Input.DocumentNumber;
        row.DocumentDate = record.Input.DocumentDate;
        row.Amount = record.Input.Amount;
        row.Okpd2Code = record.Input.Okpd2Code;
        row.PriceType = record.Input.PriceType.ToString();
        row.UnitPrice = record.Input.UnitPrice;
        row.Subject = record.Input.Subject;
        row.Period = record.Input.Period;
        row.SupplierName = record.Input.SupplierName;
        row.SupplierInn = record.Input.SupplierInn;
        row.SupplierKpp = record.Input.SupplierKpp;
        row.IsSmpSubject = record.Input.IsSmpSubject;
        row.SubContractRecordId = record.Input.SubContractRecordId;
        row.IsReplacementSubContract = record.Input.IsReplacementSubContract;
        row.PaidEarlierThanContractSchedule = record.Input.PaidEarlierThanContractSchedule;
        row.PaidToSupplierDate = record.Input.PaidToSupplierDate;
        row.AcceptanceSignedDate = record.Input.AcceptanceSignedDate;
        row.ReceivedAt = record.Input.ReceivedAt;
        row.SourceFileName = record.Input.SourceFileName;
        row.SupplierConfirmedAt = record.Input.SupplierConfirmedAt;
        row.SupplierConfirmedBy = record.Input.SupplierConfirmedBy;
        row.SupplierConfirmedDocument = record.Input.SupplierConfirmedDocument;
        row.CustomerConfirmedAt = record.Input.CustomerConfirmedAt;
        row.CustomerConfirmedBy = record.Input.CustomerConfirmedBy;
        row.CustomerConfirmedDocument = record.Input.CustomerConfirmedDocument;
        row.CreatedBy = record.Input.CreatedBy;
        row.Status = record.Status.ToString();
        row.CreatedAt = record.CreatedAt;
        row.UpdatedAt = record.UpdatedAt;
        row.SubmittedAt = record.SubmittedAt;
        row.AcceptedAt = record.AcceptedAt;
        row.AcceptedBy = record.AcceptedBy;
        row.RejectedAt = record.RejectedAt;
        row.RejectedReason = record.RejectedReason;
        row.PushedAt = record.PushedAt;
        row.PushedBy = record.PushedBy;
        row.EisPublishDate = record.EisPublishDate;
        row.EisRecordNumber = record.EisRecordNumber;
    }

    private static ContractSmpDocumentRow ToDocumentRow(
        Guid recordId,
        int order,
        ContractSmpDocument document) => new()
    {
        Id = document.Id,
        RecordId = recordId,
        Type = document.Type.ToString(),
        Title = document.Title,
        Content = document.Content,
        Version = document.Version,
        Sha256 = document.Sha256,
        CreatedBy = document.CreatedBy,
        CreatedAt = document.CreatedAt,
        OrderIndex = order,
    };

    private static ContractSmpEventRow ToEventRow(
        Guid recordId,
        int order,
        ContractSmpEvent item) => new()
    {
        Id = Guid.NewGuid(),
        RecordId = recordId,
        At = item.At,
        From = item.From.ToString(),
        To = item.To.ToString(),
        Action = item.Action,
        Comment = item.Comment,
        Actor = item.Actor,
        OrderIndex = order,
    };
}
