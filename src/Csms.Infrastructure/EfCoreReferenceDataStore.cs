using Csms.Application;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>Хранилище справочников НСИ поверх PostgreSQL (схема ref).</summary>
public sealed class EfCoreReferenceDataStore(
    IDbContextFactory<EisDbContext> contextFactory) : IReferenceDataStore
{
    public async Task<IReadOnlyList<ReferenceDatasetInfo>> GetDatasetsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.ReferenceDatasets
            .AsNoTracking()
            .OrderByDescending(row => row.IsActive)
            .ThenByDescending(row => row.ImportedAt)
            .ThenBy(row => row.Code)
            .ToListAsync(cancellationToken);

        return rows.Select(ToInfo).ToArray();
    }

    public async Task<ReferenceDatasetInfo?> GetActiveAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.ReferenceDatasets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Code == dataset && item.IsActive,
                cancellationToken);

        return row is null ? null : ToInfo(row);
    }

    public async Task<IReadOnlyList<ReferenceItemInfo>> QueryAsync(
        string dataset,
        string? search,
        string? parentCode,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.ReferenceDatasets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Code == dataset && row.IsActive,
                cancellationToken);

        if (active is null)
        {
            return [];
        }

        var query = db.ReferenceItems
            .AsNoTracking()
            .Where(row => row.DatasetId == active.Id);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.Code, pattern) ||
                (row.Name != null && EF.Functions.ILike(row.Name, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(parentCode))
        {
            query = query.Where(row => row.ParentCode == parentCode.Trim());
        }

        var rows = await query
            .OrderBy(row => row.Code)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ReferenceItemInfo(
            row.Code,
            row.ParentCode,
            row.Name,
            row.Actual,
            row.PayloadJson)).ToArray();
    }

    public async Task<IReadOnlyList<ReferenceItemInfo>> GetAllAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.ReferenceDatasets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Code == dataset && row.IsActive,
                cancellationToken);

        if (active is null)
        {
            return [];
        }

        // payload не нужен: у ОКПД2 это десятки мегабайт jsonb, а агенту
        // разметки требуются только код, родитель и наименование.
        var rows = await db.ReferenceItems
            .AsNoTracking()
            .Where(row => row.DatasetId == active.Id)
            .Select(row => new { row.Code, row.ParentCode, row.Name, row.Actual })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ReferenceItemInfo(
            row.Code,
            row.ParentCode,
            row.Name,
            row.Actual,
            "{}")).ToArray();
    }

    public async Task<IReadOnlyList<ReferenceItemInfo>> GetAllWithPayloadAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.ReferenceDatasets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Code == dataset && row.IsActive,
                cancellationToken);

        if (active is null)
        {
            return [];
        }

        var rows = await db.ReferenceItems
            .AsNoTracking()
            .Where(row => row.DatasetId == active.Id)
            .Select(row => new { row.Code, row.ParentCode, row.Name, row.Actual, row.PayloadJson })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ReferenceItemInfo(
            row.Code,
            row.ParentCode,
            row.Name,
            row.Actual,
            row.PayloadJson)).ToArray();
    }

    public async Task<ReferenceDataUpdateEvent> PublishAsync(
        ReferenceImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var clean = ReferenceImportNormalizer.Normalize(request);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var duplicate = await db.ReferenceDatasets
            .AnyAsync(
                row => row.Code == clean.Dataset && row.Version == clean.Version,
                cancellationToken);
        if (duplicate)
        {
            throw new ArgumentException(
                $"Версия {clean.Version} справочника {clean.Dataset} уже импортирована.");
        }

        var previous = await db.ReferenceDatasets
            .FirstOrDefaultAsync(
                row => row.Code == clean.Dataset && row.IsActive,
                cancellationToken);

        // Сначала снимаем флаг активности: на уровне БД стоит уникальный
        // фильтр «одна активная версия на справочник».
        if (previous is not null)
        {
            previous.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        var importedAt = DateTimeOffset.UtcNow;
        var dataset = new ReferenceDatasetRow
        {
            Id = Guid.NewGuid(),
            Code = clean.Dataset,
            Version = clean.Version,
            SourceDocument = clean.SourceDocument,
            SourceUrl = clean.SourceUrl,
            SourceSha256 = clean.SourceSha256,
            PublishedAt = clean.PublishedAt,
            ImportedAt = importedAt,
            ItemCount = clean.Items.Count,
            IsActive = true,
        };

        foreach (var item in clean.Items)
        {
            dataset.Items.Add(new ReferenceItemRow
            {
                Id = Guid.NewGuid(),
                Code = item.Code!,
                ParentCode = item.ParentCode,
                Name = item.Name,
                Actual = item.Actual ?? false,
                PayloadJson = ReferenceImportNormalizer.SerializePayload(item.Payload),
            });
        }

        db.ReferenceDatasets.Add(dataset);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var info = ToInfo(dataset);
        return new ReferenceDataUpdateEvent(
            clean.Dataset,
            previous?.Version ?? "unknown",
            clean.Version,
            dataset.ItemCount,
            info.Hash,
            importedAt);
    }

    private static ReferenceDatasetInfo ToInfo(ReferenceDatasetRow row) => new(
        row.Code,
        row.Version,
        row.SourceDocument,
        row.SourceUrl,
        row.SourceSha256,
        row.PublishedAt,
        row.ImportedAt,
        row.ItemCount,
        ReferenceImportNormalizer.ComputeHash(row.Code, row.Version, row.ItemCount, row.ImportedAt),
        row.IsActive);
}
