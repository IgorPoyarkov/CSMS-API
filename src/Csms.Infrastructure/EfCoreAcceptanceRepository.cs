using Csms.Application;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище документов о приёмке в PostgreSQL.
///
/// Поведение совпадает с <see cref="InMemoryAcceptanceRepository"/>: те же
/// фильтры, тот же порядок списка (свежие сверху). Отличие в главном —
/// состояние переживает перезапуск и защищено от одновременной правки
/// ревизией записи.
/// </summary>
public sealed class EfCoreAcceptanceRepository(IDbContextFactory<EisDbContext> contextFactory)
    : IAcceptanceRepository
{
    public async Task<IReadOnlyList<ContractAcceptance>> ListAsync(
        string? organizationId,
        string? contractId,
        AcceptanceStatus? status,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ContractAcceptances.AsNoTracking().AsQueryable();

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

        if (status is not null)
        {
            var value = status.Value.ToString();
            query = query.Where(row => row.Status == value);
        }

        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(cancellationToken);

        var ids = rows.Select(row => row.Id).ToArray();
        var positions = await LoadPositionsAsync(db, ids, cancellationToken);
        var defects = await LoadDefectsAsync(db, ids, cancellationToken);
        var events = await LoadEventsAsync(db, ids, cancellationToken);

        return rows
            .Select(row => ToDomain(
                row,
                positions.GetValueOrDefault(row.Id) ?? [],
                defects.GetValueOrDefault(row.Id) ?? [],
                events.GetValueOrDefault(row.Id) ?? []))
            .ToArray();
    }

    public async Task<ContractAcceptance?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractAcceptances
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var positions = await LoadPositionsAsync(db, [id], cancellationToken);
        var defects = await LoadDefectsAsync(db, [id], cancellationToken);
        var events = await LoadEventsAsync(db, [id], cancellationToken);

        return ToDomain(
            row,
            positions.GetValueOrDefault(id) ?? [],
            defects.GetValueOrDefault(id) ?? [],
            events.GetValueOrDefault(id) ?? []);
    }

    public async Task AddAsync(ContractAcceptance acceptance, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        db.ContractAcceptances.Add(ToRow(acceptance));

        var order = 0;

        foreach (var position in acceptance.Positions)
        {
            db.AcceptancePositions.Add(ToPositionRow(acceptance.Id, order++, position));
        }

        order = 0;

        foreach (var defect in acceptance.Defects)
        {
            db.AcceptanceDefects.Add(ToDefectRow(acceptance.Id, order++, defect));
        }

        order = 0;

        foreach (var item in acceptance.Events)
        {
            db.AcceptanceEvents.Add(ToEventRow(acceptance.Id, order++, item));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ContractAcceptance acceptance, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ContractAcceptances
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == acceptance.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Документ о приёмке {acceptance.Id} не найден в хранилище.");

        var storedPositions = await db.AcceptancePositions
            .AsNoTracking()
            .Where(item => item.AcceptanceId == acceptance.Id)
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        var storedDefects = await db.AcceptanceDefects
            .AsNoTracking()
            .Where(item => item.AcceptanceId == acceptance.Id)
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        var storedEvents = await db.AcceptanceEvents
            .AsNoTracking()
            .CountAsync(item => item.AcceptanceId == acceptance.Id, cancellationToken);

        // Ревизия, которую документ получил при чтении, — именно она уходит в
        // WHERE. Свежий xmin из только что прочитанной строки для блокировки не
        // годится: он всегда совпадёт, и конфликт останется незамеченным.
        var expectedRevision = acceptance.Revision;

        MapInto(acceptance, row);
        row.Revision = expectedRevision + 1;

        var entry = db.ContractAcceptances.Update(row);
        entry.Property(item => item.Revision).OriginalValue = expectedRevision;

        SyncPositions(db, acceptance, storedPositions);
        SyncDefects(db, acceptance, storedDefects);
        SyncEvents(db, acceptance, storedEvents);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException error)
        {
            throw new AcceptanceStoreConflictException(
                "Документ уже изменён в другом окне или другим процессом.",
                error);
        }

        acceptance.AcceptRevision(expectedRevision + 1);
    }

    /// <summary>
    /// Позиции и недостатки неизменяемы: правка идёт через удаление и добавление,
    /// а порядок задаёт <c>OrderIndex</c>, а не метка времени.
    /// </summary>
    private static void SyncPositions(
        EisDbContext db,
        ContractAcceptance acceptance,
        IReadOnlyList<AcceptancePositionRow> stored)
    {
        var actual = acceptance.Positions.Select(position => position.Id).ToHashSet();

        foreach (var row in stored.Where(row => !actual.Contains(row.Id)))
        {
            db.AcceptancePositions.Remove(row);
        }

        var known = stored.Select(row => row.Id).ToHashSet();
        var order = 0;

        foreach (var position in acceptance.Positions)
        {
            if (!known.Contains(position.Id))
            {
                db.AcceptancePositions.Add(ToPositionRow(acceptance.Id, order, position));
            }

            order++;
        }
    }

    private static void SyncDefects(
        EisDbContext db,
        ContractAcceptance acceptance,
        IReadOnlyList<AcceptanceDefectRow> stored)
    {
        var actual = acceptance.Defects.Select(defect => defect.Id).ToHashSet();

        foreach (var row in stored.Where(row => !actual.Contains(row.Id)))
        {
            db.AcceptanceDefects.Remove(row);
        }

        var known = stored.Select(row => row.Id).ToHashSet();
        var order = 0;

        foreach (var defect in acceptance.Defects)
        {
            if (!known.Contains(defect.Id))
            {
                db.AcceptanceDefects.Add(ToDefectRow(acceptance.Id, order, defect));
            }

            order++;
        }
    }

    /// <summary>Журнал только дописывается: уже сохранённые события не трогаем.</summary>
    private static void SyncEvents(EisDbContext db, ContractAcceptance acceptance, int storedCount)
    {
        var order = storedCount;

        foreach (var item in acceptance.Events.Skip(storedCount))
        {
            db.AcceptanceEvents.Add(ToEventRow(acceptance.Id, order++, item));
        }
    }

    private static async Task<Dictionary<Guid, List<AcceptancePositionRow>>> LoadPositionsAsync(
        EisDbContext db,
        IEnumerable<Guid> acceptanceIds,
        CancellationToken cancellationToken)
    {
        var ids = acceptanceIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.AcceptancePositions
            .AsNoTracking()
            .Where(item => ids.Contains(item.AcceptanceId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.AcceptanceId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static async Task<Dictionary<Guid, List<AcceptanceDefectRow>>> LoadDefectsAsync(
        EisDbContext db,
        IEnumerable<Guid> acceptanceIds,
        CancellationToken cancellationToken)
    {
        var ids = acceptanceIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.AcceptanceDefects
            .AsNoTracking()
            .Where(item => ids.Contains(item.AcceptanceId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.AcceptanceId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static async Task<Dictionary<Guid, List<AcceptanceEventRow>>> LoadEventsAsync(
        EisDbContext db,
        IEnumerable<Guid> acceptanceIds,
        CancellationToken cancellationToken)
    {
        var ids = acceptanceIds.ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.AcceptanceEvents
            .AsNoTracking()
            .Where(item => ids.Contains(item.AcceptanceId))
            .OrderBy(item => item.OrderIndex)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.AcceptanceId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    private static ContractAcceptance ToDomain(
        ContractAcceptanceRow row,
        IReadOnlyList<AcceptancePositionRow> positions,
        IReadOnlyList<AcceptanceDefectRow> defects,
        IReadOnlyList<AcceptanceEventRow> events)
    {
        var input = new AcceptanceInput(
            row.OrganizationId,
            row.ContractId,
            row.ContractNumber,
            row.ContractName,
            row.ContractPrice,
            row.ContractStatus,
            row.ContractDate,
            row.CustomerName,
            row.SupplierName,
            row.SupplierInn,
            row.Ikz,
            row.EisRegistryNumber,
            row.StageNumber,
            row.StageName,
            Enum.Parse<AcceptanceKind>(row.Kind, ignoreCase: true),
            row.DocumentNumber,
            row.DocumentDate,
            row.CorrectedDocumentNumber,
            row.CorrectedDocumentDate,
            row.CorrectionReason,
            row.AcceptanceTermDays,
            row.ExpertiseRequired,
            row.ExpertiseByThirdParty,
            row.ExpertiseOrganization,
            row.ExpertiseConclusionNumber,
            row.ExpertiseConclusionDate,
            row.CommissionRequired,
            row.CommissionDecisionNumber,
            row.CommissionDecisionDate,
            row.DeliveryPlace,
            row.Notes,
            row.CreatedBy);

        var restoredPositions = positions
            .Select(position => AcceptancePosition.Restore(
                position.Id,
                position.Name,
                position.Okpd2,
                position.Unit,
                position.Quantity,
                position.UnitPrice,
                position.VatRate,
                position.CountryOrigin,
                position.RegistryNumber))
            .ToList();

        var restoredDefects = defects
            .Select(defect => AcceptanceDefect.Restore(
                defect.Id,
                defect.PositionId,
                defect.Description,
                defect.Quantity,
                Enum.Parse<AcceptanceDefectRequirement>(defect.Requirement, ignoreCase: true)))
            .ToList();

        var restoredEvents = events
            .Select(item => new AcceptanceEvent(
                item.At,
                Enum.Parse<AcceptanceStatus>(item.From, ignoreCase: true),
                Enum.Parse<AcceptanceStatus>(item.To, ignoreCase: true),
                item.Action,
                item.Comment,
                item.Actor))
            .ToList();

        return ContractAcceptance.Restore(
            row.Id,
            input,
            Enum.Parse<AcceptanceStatus>(row.Status, ignoreCase: true),
            string.IsNullOrWhiteSpace(row.Result)
                ? null
                : Enum.Parse<AcceptanceResult>(row.Result, ignoreCase: true),
            restoredPositions,
            restoredDefects,
            restoredEvents,
            row.CreatedAt,
            row.UpdatedAt,
            row.SubmittedAt,
            row.ReviewedAt,
            row.SignedAt,
            row.SignedBy,
            row.RegisteredAt,
            row.RegisteredBy,
            row.EisRegistrationNumber,
            row.EisRegistrationDate,
            row.RefusalReason,
            row.ReturnComment,
            row.CancelledReason,
            row.Revision);
    }

    private static ContractAcceptanceRow ToRow(ContractAcceptance acceptance) => new()
    {
        Id = acceptance.Id,
        OrganizationId = acceptance.Input.OrganizationId,
        ContractId = acceptance.Input.ContractId,
        ContractNumber = acceptance.Input.ContractNumber,
        ContractName = acceptance.Input.ContractName,
        ContractPrice = acceptance.Input.ContractPrice,
        ContractStatus = acceptance.Input.ContractStatus,
        ContractDate = acceptance.Input.ContractDate,
        CustomerName = acceptance.Input.CustomerName,
        SupplierName = acceptance.Input.SupplierName,
        SupplierInn = acceptance.Input.SupplierInn,
        Ikz = acceptance.Input.Ikz,
        EisRegistryNumber = acceptance.Input.EisRegistryNumber,
        StageNumber = acceptance.Input.StageNumber,
        StageName = acceptance.Input.StageName,
        Kind = acceptance.Input.Kind.ToString(),
        DocumentNumber = acceptance.Input.DocumentNumber,
        DocumentDate = acceptance.Input.DocumentDate,
        CorrectedDocumentNumber = acceptance.Input.CorrectedDocumentNumber,
        CorrectedDocumentDate = acceptance.Input.CorrectedDocumentDate,
        CorrectionReason = acceptance.Input.CorrectionReason,
        AcceptanceTermDays = acceptance.Input.AcceptanceTermDays,
        ExpertiseRequired = acceptance.Input.ExpertiseRequired,
        ExpertiseByThirdParty = acceptance.Input.ExpertiseByThirdParty,
        ExpertiseOrganization = acceptance.Input.ExpertiseOrganization,
        ExpertiseConclusionNumber = acceptance.Input.ExpertiseConclusionNumber,
        ExpertiseConclusionDate = acceptance.Input.ExpertiseConclusionDate,
        CommissionRequired = acceptance.Input.CommissionRequired,
        CommissionDecisionNumber = acceptance.Input.CommissionDecisionNumber,
        CommissionDecisionDate = acceptance.Input.CommissionDecisionDate,
        DeliveryPlace = acceptance.Input.DeliveryPlace,
        Notes = acceptance.Input.Notes,
        CreatedBy = acceptance.Input.CreatedBy,
        Status = acceptance.Status.ToString(),
        Result = acceptance.Result?.ToString(),
        CreatedAt = acceptance.CreatedAt,
        UpdatedAt = acceptance.UpdatedAt,
        SubmittedAt = acceptance.SubmittedAt,
        ReviewedAt = acceptance.ReviewedAt,
        SignedAt = acceptance.SignedAt,
        SignedBy = acceptance.SignedBy,
        RegisteredAt = acceptance.RegisteredAt,
        RegisteredBy = acceptance.RegisteredBy,
        EisRegistrationNumber = acceptance.EisRegistrationNumber,
        EisRegistrationDate = acceptance.EisRegistrationDate,
        RefusalReason = acceptance.RefusalReason,
        ReturnComment = acceptance.ReturnComment,
        CancelledReason = acceptance.CancelledReason,
        Revision = acceptance.Revision,
    };

    private static void MapInto(ContractAcceptance acceptance, ContractAcceptanceRow row)
    {
        row.OrganizationId = acceptance.Input.OrganizationId;
        row.ContractId = acceptance.Input.ContractId;
        row.ContractNumber = acceptance.Input.ContractNumber;
        row.ContractName = acceptance.Input.ContractName;
        row.ContractPrice = acceptance.Input.ContractPrice;
        row.ContractStatus = acceptance.Input.ContractStatus;
        row.ContractDate = acceptance.Input.ContractDate;
        row.CustomerName = acceptance.Input.CustomerName;
        row.SupplierName = acceptance.Input.SupplierName;
        row.SupplierInn = acceptance.Input.SupplierInn;
        row.Ikz = acceptance.Input.Ikz;
        row.EisRegistryNumber = acceptance.Input.EisRegistryNumber;
        row.StageNumber = acceptance.Input.StageNumber;
        row.StageName = acceptance.Input.StageName;
        row.Kind = acceptance.Input.Kind.ToString();
        row.DocumentNumber = acceptance.Input.DocumentNumber;
        row.DocumentDate = acceptance.Input.DocumentDate;
        row.CorrectedDocumentNumber = acceptance.Input.CorrectedDocumentNumber;
        row.CorrectedDocumentDate = acceptance.Input.CorrectedDocumentDate;
        row.CorrectionReason = acceptance.Input.CorrectionReason;
        row.AcceptanceTermDays = acceptance.Input.AcceptanceTermDays;
        row.ExpertiseRequired = acceptance.Input.ExpertiseRequired;
        row.ExpertiseByThirdParty = acceptance.Input.ExpertiseByThirdParty;
        row.ExpertiseOrganization = acceptance.Input.ExpertiseOrganization;
        row.ExpertiseConclusionNumber = acceptance.Input.ExpertiseConclusionNumber;
        row.ExpertiseConclusionDate = acceptance.Input.ExpertiseConclusionDate;
        row.CommissionRequired = acceptance.Input.CommissionRequired;
        row.CommissionDecisionNumber = acceptance.Input.CommissionDecisionNumber;
        row.CommissionDecisionDate = acceptance.Input.CommissionDecisionDate;
        row.DeliveryPlace = acceptance.Input.DeliveryPlace;
        row.Notes = acceptance.Input.Notes;
        row.CreatedBy = acceptance.Input.CreatedBy;
        row.Status = acceptance.Status.ToString();
        row.Result = acceptance.Result?.ToString();
        row.CreatedAt = acceptance.CreatedAt;
        row.UpdatedAt = acceptance.UpdatedAt;
        row.SubmittedAt = acceptance.SubmittedAt;
        row.ReviewedAt = acceptance.ReviewedAt;
        row.SignedAt = acceptance.SignedAt;
        row.SignedBy = acceptance.SignedBy;
        row.RegisteredAt = acceptance.RegisteredAt;
        row.RegisteredBy = acceptance.RegisteredBy;
        row.EisRegistrationNumber = acceptance.EisRegistrationNumber;
        row.EisRegistrationDate = acceptance.EisRegistrationDate;
        row.RefusalReason = acceptance.RefusalReason;
        row.ReturnComment = acceptance.ReturnComment;
        row.CancelledReason = acceptance.CancelledReason;
    }

    private static AcceptancePositionRow ToPositionRow(
        Guid acceptanceId,
        int order,
        AcceptancePosition position) => new()
    {
        Id = position.Id,
        AcceptanceId = acceptanceId,
        Name = position.Name,
        Okpd2 = position.Okpd2,
        Unit = position.Unit,
        Quantity = position.Quantity,
        UnitPrice = position.UnitPrice,
        VatRate = position.VatRate,
        CountryOrigin = position.CountryOrigin,
        RegistryNumber = position.RegistryNumber,
        OrderIndex = order,
    };

    private static AcceptanceDefectRow ToDefectRow(
        Guid acceptanceId,
        int order,
        AcceptanceDefect defect) => new()
    {
        Id = defect.Id,
        AcceptanceId = acceptanceId,
        PositionId = defect.PositionId,
        Description = defect.Description,
        Quantity = defect.Quantity,
        Requirement = defect.Requirement.ToString(),
        OrderIndex = order,
    };

    private static AcceptanceEventRow ToEventRow(
        Guid acceptanceId,
        int order,
        AcceptanceEvent item) => new()
    {
        Id = Guid.NewGuid(),
        AcceptanceId = acceptanceId,
        At = item.At,
        From = item.From.ToString(),
        To = item.To.ToString(),
        Action = item.Action,
        Comment = item.Comment,
        Actor = item.Actor,
        OrderIndex = order,
    };
}
