using Csms.Application;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище изменений и расторжений контрактов в PostgreSQL (схема <c>contract</c>).
///
/// Поведение совпадает с <see cref="InMemoryContractChangeRepository"/>: те же
/// фильтры, тот же порядок списка (свежие сверху). Отличие в главном — состояние
/// переживает перезапуск и защищено от одновременной правки через <c>xmin</c>.
/// </summary>
public sealed class EfCoreContractChangeRepository(IDbContextFactory<EisDbContext> contextFactory)
    : IContractChangeRepository
{
    public async Task<IReadOnlyList<ContractChange>> ListAsync(
        string? organizationId,
        string? contractId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ContractChanges.AsNoTracking().AsQueryable();

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

    public async Task<ContractChange?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractChanges
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

    public async Task AddAsync(ContractChange change, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        db.ContractChanges.Add(ToRow(change));

        var documentOrder = 0;

        foreach (var document in change.Documents)
        {
            db.ContractChangeDocuments.Add(ToDocumentRow(change.Id, documentOrder++, document));
        }

        var eventOrder = 0;

        foreach (var item in change.Events)
        {
            db.ContractChangeEvents.Add(ToEventRow(change.Id, eventOrder++, item));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ContractChange change, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractChanges
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == change.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Документ {change.Id} не найден в хранилище.");

        var storedDocuments = await db.ContractChangeDocuments
            .AsNoTracking()
            .Where(item => item.ChangeId == change.Id)
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        var storedEvents = await db.ContractChangeEvents
            .AsNoTracking()
            .CountAsync(item => item.ChangeId == change.Id, cancellationToken);

        // Ревизия, которую документ получил при чтении, — именно она уходит в
        // WHERE. Свежий xmin из только что прочитанной строки для блокировки не
        // годится: он всегда совпадёт, и конфликт останется незамеченным.
        var expectedRevision = change.Revision;

        MapInto(change, row);
        row.Revision = expectedRevision + 1;

        var entry = db.ContractChanges.Update(row);
        entry.Property(item => item.Revision).OriginalValue = expectedRevision;

        SyncDocuments(db, change, storedDocuments);
        SyncEvents(db, change, storedEvents);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new ContractChangeStoreConflictException(
                "Документ уже изменён в другом окне или другим процессом.",
                error);
        }

        change.AcceptRevision(expectedRevision + 1);
    }

    /// <summary>
    /// Документы дописываются и удаляются, но не переписываются «на месте»:
    /// удалённый документ должен исчезнуть из хранилища, а новый — получить
    /// свой порядковый номер. Изменение текста идёт через новую редакцию.
    /// </summary>
    private static void SyncDocuments(
        EisDbContext db,
        ContractChange change,
        IReadOnlyList<ContractChangeDocumentRow> stored)
    {
        var actual = change.Documents.Select(document => document.Id).ToHashSet();

        foreach (var row in stored.Where(row => !actual.Contains(row.Id)))
        {
            db.ContractChangeDocuments.Remove(row);
        }

        var known = stored.Select(row => row.Id).ToHashSet();
        var order = stored.Count;

        foreach (var document in change.Documents.Where(document => !known.Contains(document.Id)))
        {
            db.ContractChangeDocuments.Add(ToDocumentRow(change.Id, order++, document));
        }
    }

    /// <summary>Журнал только дописывается: уже сохранённые события не трогаем.</summary>
    private static void SyncEvents(EisDbContext db, ContractChange change, int storedCount)
    {
        var order = storedCount;

        foreach (var item in change.Events.Skip(storedCount))
        {
            db.ContractChangeEvents.Add(ToEventRow(change.Id, order++, item));
        }
    }

    private static async Task<Dictionary<Guid, List<ContractChangeDocumentRow>>> LoadDocumentsAsync(
        EisDbContext db,
        IEnumerable<Guid> changeIds,
        CancellationToken cancellationToken)
    {
        var ids = changeIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.ContractChangeDocuments
            .AsNoTracking()
            .Where(item => ids.Contains(item.ChangeId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.ChangeId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static async Task<Dictionary<Guid, List<ContractChangeEventRow>>> LoadEventsAsync(
        EisDbContext db,
        IEnumerable<Guid> changeIds,
        CancellationToken cancellationToken)
    {
        var ids = changeIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.ContractChangeEvents
            .AsNoTracking()
            .Where(item => ids.Contains(item.ChangeId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.ChangeId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static ContractChange ToDomain(
        ContractChangeRow row,
        IReadOnlyList<ContractChangeDocumentRow> documents,
        IReadOnlyList<ContractChangeEventRow> events)
    {
        var input = new ContractChangeInput(
            row.OrganizationId,
            row.ContractId,
            row.ContractNumber,
            row.ContractName,
            row.ContractPrice,
            row.ContractStatus,
            row.ContractDate,
            row.CustomerName,
            row.SupplierName,
            row.Ikz,
            row.EisRegistryNumber,
            row.ContractQuantity,
            row.ContractExecutionEndDate,
            Enum.Parse<ContractChangeKind>(row.Kind, ignoreCase: true),
            row.BasisCode,
            row.Reason,
            row.Description,
            row.ChangeDate,
            row.TerminationDate,
            row.NewPrice,
            row.NewQuantity,
            row.QuantityUnit,
            row.NewSubject,
            row.NewSupplier,
            row.NewExecutionEndDate,
            row.NoticeNumber,
            row.NoticeDate,
            row.CourtDecisionNumber,
            row.CourtDecisionDate,
            row.GovernmentActNumber,
            row.GovernmentActDate,
            row.FormulaText,
            row.UnitPrice,
            row.InitialMaxPrice,
            row.CreatedBy);

        var restoredDocuments = documents
            .Select(document => ContractChangeDocument.Restore(
                document.Id,
                Enum.Parse<ContractChangeDocumentType>(document.Type, ignoreCase: true),
                document.Title,
                document.Content,
                document.Version,
                document.Sha256,
                document.CreatedBy,
                document.CreatedAt))
            .ToList();

        var restoredEvents = events
            .Select(item => new ContractChangeEvent(
                item.At,
                Enum.Parse<ContractChangeStatus>(item.From, ignoreCase: true),
                Enum.Parse<ContractChangeStatus>(item.To, ignoreCase: true),
                item.Action,
                item.Comment,
                item.Actor))
            .ToList();

        return ContractChange.Restore(
            row.Id,
            input,
            Enum.Parse<ContractChangeStatus>(row.Status, ignoreCase: true),
            restoredDocuments,
            restoredEvents,
            row.CreatedAt,
            row.UpdatedAt,
            row.SubmittedAt,
            row.ApprovedAt,
            row.SignedAt,
            row.SignedBy,
            row.RegisteredAt,
            row.RegisteredBy,
            row.EisRegistrationNumber,
            row.EisRegistrationDate,
            row.RejectedReason,
            row.CancelledReason,
            row.Revision);
    }

    private static ContractChangeRow ToRow(ContractChange change) => new()
    {
        Id = change.Id,
        OrganizationId = change.Input.OrganizationId,
        ContractId = change.Input.ContractId,
        ContractNumber = change.Input.ContractNumber,
        ContractName = change.Input.ContractName,
        ContractPrice = change.Input.ContractPrice,
        ContractStatus = change.Input.ContractStatus,
        ContractDate = change.Input.ContractDate,
        CustomerName = change.Input.CustomerName,
        SupplierName = change.Input.SupplierName,
        Ikz = change.Input.Ikz,
        EisRegistryNumber = change.Input.EisRegistryNumber,
        ContractQuantity = change.Input.ContractQuantity,
        ContractExecutionEndDate = change.Input.ContractExecutionEndDate,
        Kind = change.Input.Kind.ToString(),
        BasisCode = change.Input.BasisCode,
        Reason = change.Input.Reason,
        Description = change.Input.Description,
        ChangeDate = change.Input.ChangeDate,
        TerminationDate = change.Input.TerminationDate,
        NewPrice = change.Input.NewPrice,
        NewQuantity = change.Input.NewQuantity,
        QuantityUnit = change.Input.QuantityUnit,
        NewSubject = change.Input.NewSubject,
        NewSupplier = change.Input.NewSupplier,
        NewExecutionEndDate = change.Input.NewExecutionEndDate,
        NoticeNumber = change.Input.NoticeNumber,
        NoticeDate = change.Input.NoticeDate,
        CourtDecisionNumber = change.Input.CourtDecisionNumber,
        CourtDecisionDate = change.Input.CourtDecisionDate,
        GovernmentActNumber = change.Input.GovernmentActNumber,
        GovernmentActDate = change.Input.GovernmentActDate,
        FormulaText = change.Input.FormulaText,
        UnitPrice = change.Input.UnitPrice,
        InitialMaxPrice = change.Input.InitialMaxPrice,
        CreatedBy = change.Input.CreatedBy,
        Revision = change.Revision,
        Status = change.Status.ToString(),
        CreatedAt = change.CreatedAt,
        UpdatedAt = change.UpdatedAt,
        SubmittedAt = change.SubmittedAt,
        ApprovedAt = change.ApprovedAt,
        SignedAt = change.SignedAt,
        SignedBy = change.SignedBy,
        RegisteredAt = change.RegisteredAt,
        RegisteredBy = change.RegisteredBy,
        EisRegistrationNumber = change.EisRegistrationNumber,
        EisRegistrationDate = change.EisRegistrationDate,
        RejectedReason = change.RejectedReason,
        CancelledReason = change.CancelledReason,
    };

    private static void MapInto(ContractChange change, ContractChangeRow row)
    {
        row.OrganizationId = change.Input.OrganizationId;
        row.ContractId = change.Input.ContractId;
        row.ContractNumber = change.Input.ContractNumber;
        row.ContractName = change.Input.ContractName;
        row.ContractPrice = change.Input.ContractPrice;
        row.ContractStatus = change.Input.ContractStatus;
        row.ContractDate = change.Input.ContractDate;
        row.CustomerName = change.Input.CustomerName;
        row.SupplierName = change.Input.SupplierName;
        row.Ikz = change.Input.Ikz;
        row.EisRegistryNumber = change.Input.EisRegistryNumber;
        row.ContractQuantity = change.Input.ContractQuantity;
        row.ContractExecutionEndDate = change.Input.ContractExecutionEndDate;
        row.Kind = change.Input.Kind.ToString();
        row.BasisCode = change.Input.BasisCode;
        row.Reason = change.Input.Reason;
        row.Description = change.Input.Description;
        row.ChangeDate = change.Input.ChangeDate;
        row.TerminationDate = change.Input.TerminationDate;
        row.NewPrice = change.Input.NewPrice;
        row.NewQuantity = change.Input.NewQuantity;
        row.QuantityUnit = change.Input.QuantityUnit;
        row.NewSubject = change.Input.NewSubject;
        row.NewSupplier = change.Input.NewSupplier;
        row.NewExecutionEndDate = change.Input.NewExecutionEndDate;
        row.NoticeNumber = change.Input.NoticeNumber;
        row.NoticeDate = change.Input.NoticeDate;
        row.CourtDecisionNumber = change.Input.CourtDecisionNumber;
        row.CourtDecisionDate = change.Input.CourtDecisionDate;
        row.GovernmentActNumber = change.Input.GovernmentActNumber;
        row.GovernmentActDate = change.Input.GovernmentActDate;
        row.FormulaText = change.Input.FormulaText;
        row.UnitPrice = change.Input.UnitPrice;
        row.InitialMaxPrice = change.Input.InitialMaxPrice;
        row.CreatedBy = change.Input.CreatedBy;
        row.Status = change.Status.ToString();
        row.UpdatedAt = change.UpdatedAt;
        row.SubmittedAt = change.SubmittedAt;
        row.ApprovedAt = change.ApprovedAt;
        row.SignedAt = change.SignedAt;
        row.SignedBy = change.SignedBy;
        row.RegisteredAt = change.RegisteredAt;
        row.RegisteredBy = change.RegisteredBy;
        row.EisRegistrationNumber = change.EisRegistrationNumber;
        row.EisRegistrationDate = change.EisRegistrationDate;
        row.RejectedReason = change.RejectedReason;
        row.CancelledReason = change.CancelledReason;
    }

    private static ContractChangeDocumentRow ToDocumentRow(
        Guid changeId,
        int order,
        ContractChangeDocument document) => new()
        {
            Id = document.Id,
            ChangeId = changeId,
            Type = document.Type.ToString(),
            Title = document.Title,
            Content = document.Content,
            Version = document.Version,
            Sha256 = document.Sha256,
            CreatedBy = document.CreatedBy,
            CreatedAt = document.CreatedAt,
            OrderIndex = order,
        };

    private static ContractChangeEventRow ToEventRow(
        Guid changeId,
        int order,
        ContractChangeEvent item) => new()
        {
            Id = Guid.NewGuid(),
            ChangeId = changeId,
            At = item.At,
            From = item.From.ToString(),
            To = item.To.ToString(),
            Action = item.Action,
            Comment = item.Comment,
            Actor = item.Actor,
            OrderIndex = order,
        };
}
