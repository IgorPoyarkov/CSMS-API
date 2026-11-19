using Csms.Application;
using Csms.Infrastructure.Npa;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>Хранилище правил НПА поверх PostgreSQL (схема ref).</summary>
public sealed class EfCoreNpaRuleStore(
    IDbContextFactory<EisDbContext> contextFactory) : INpaRuleStore
{
    public async Task<IReadOnlyList<NpaDocumentInfo>> GetDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.NpaDocuments
            .AsNoTracking()
            .OrderByDescending(row => row.IsActive)
            .ThenByDescending(row => row.FetchedAt)
            .ThenBy(row => row.Code)
            .ToListAsync(cancellationToken);

        return rows.Select(ToInfo).ToArray();
    }

    public async Task<NpaDocumentInfo?> GetActiveAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.NpaDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                document => document.Code == code && document.IsActive,
                cancellationToken);

        return row is null ? null : ToInfo(row);
    }

    public async Task<IReadOnlyList<NpaRuleInfo>> GetRulesAsync(
        string code,
        string? search,
        string? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.NpaDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                document => document.Code == code && document.IsActive,
                cancellationToken);

        if (active is null)
        {
            return [];
        }

        var query = db.NpaRules
            .AsNoTracking()
            .Where(rule => rule.DocumentId == active.Id);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(rule => rule.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(rule =>
                EF.Functions.ILike(rule.Okpd2Code, pattern) ||
                (rule.ConditionText != null && EF.Functions.ILike(rule.ConditionText, pattern)));
        }

        var rows = await query
            .OrderBy(rule => rule.Okpd2Code)
            .ThenBy(rule => rule.IsInherited)
            .Skip(Math.Clamp(skip, 0, 100_000))
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(cancellationToken);

        return rows.Select(rule => ToInfo(code, rule)).ToArray();
    }

    /// <summary>
    /// Действующее правило: идём по предкам от самого кода к корню и берём
    /// первое найденное. Прямые правила и унаследованные лежат в одной таблице,
    /// поэтому порядок «самый длинный префикс первым» решает конфликт.
    /// </summary>
    public async Task<NpaEffectiveRule?> ResolveAsync(
        string code,
        string okpd2Code,
        bool includePending,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.NpaDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                document => document.Code == code && document.IsActive,
                cancellationToken);

        if (active is null)
        {
            return null;
        }

        var normalized = NpaRuleSets.NormalizeCode(okpd2Code);
        var chain = new List<string> { normalized };
        chain.AddRange(NpaRuleHierarchy.Ancestors(normalized));

        var rules = await db.NpaRules
            .AsNoTracking()
            .Where(rule => rule.DocumentId == active.Id && chain.Contains(rule.Okpd2Code))
            .ToListAsync(cancellationToken);

        var applicable = rules
            .Where(rule => includePending ||
                           rule.Status is NpaRuleStatuses.Auto or NpaRuleStatuses.Approved)
            .OrderBy(rule => rule.IsInherited)
            .ToList();

        foreach (var candidate in chain)
        {
            var match = applicable.FirstOrDefault(rule => rule.Okpd2Code == candidate);
            if (match is null)
            {
                continue;
            }

            return new NpaEffectiveRule(
                code,
                normalized,
                match.Okpd2Code,
                match.RuleType,
                match.ConditionText,
                match.IsInherited,
                match.InheritedFrom,
                match.Confidence,
                match.Status,
                active.Version);
        }

        return null;
    }

    public async Task<bool> VersionExistsAsync(
        string code,
        string version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.NpaDocuments
            .AnyAsync(
                row => row.Code == code && row.Version == version,
                cancellationToken);
    }

    public async Task<NpaDocumentUpdateEvent> PublishAsync(
        NpaDocumentImport import,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var duplicate = await db.NpaDocuments
            .AnyAsync(
                row => row.Code == import.Code && row.Version == import.Version,
                cancellationToken);

        if (duplicate)
        {
            throw new ArgumentException(
                $"Версия {import.Version} акта {import.Code} уже загружена.");
        }

        var previous = await db.NpaDocuments
            .FirstOrDefaultAsync(
                row => row.Code == import.Code && row.IsActive,
                cancellationToken);

        if (previous is not null && import.Promote)
        {
            previous.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        var fetchedAt = DateTimeOffset.UtcNow;
        var document = new NpaDocumentRow
        {
            Id = Guid.NewGuid(),
            Code = import.Code,
            Version = import.Version,
            EoNumber = import.EoNumber,
            Number = import.Number,
            Title = import.Title,
            DocumentDate = import.DocumentDate,
            PublishDate = import.PublishDate,
            PagesCount = import.PagesCount,
            SourceUrl = import.SourceUrl,
            PdfSha256 = import.PdfSha256,
            OcrEngine = import.OcrEngine,
            FetchedAt = fetchedAt,
            ItemCount = import.Rules.Count,
            UnresolvedJson = import.UnresolvedJson,
            IsActive = import.Promote,
        };

        foreach (var rule in import.Rules)
        {
            document.Rules.Add(new NpaRuleRow
            {
                Id = Guid.NewGuid(),
                Okpd2Code = rule.Okpd2Code,
                RuleType = rule.RuleType,
                ConditionText = rule.ConditionText,
                Quote = rule.Quote,
                IsInherited = rule.IsInherited,
                InheritedFrom = rule.InheritedFrom,
                Confidence = rule.Confidence,
                Status = rule.Status,
                Note = rule.Note,
                SourcePage = rule.SourcePage,
                SourceRow = rule.SourceRow,
            });
        }

        db.NpaDocuments.Add(document);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var info = ToInfo(document);

        return new NpaDocumentUpdateEvent(
            import.Code,
            previous?.Version ?? "unknown",
            import.Version,
            import.Rules.Count,
            info.Hash,
            fetchedAt);
    }

    public async Task<NpaRuleInfo?> VerifyAsync(
        string code,
        string okpd2Code,
        string status,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var active = await db.NpaDocuments
            .FirstOrDefaultAsync(
                document => document.Code == code && document.IsActive,
                cancellationToken);

        if (active is null)
        {
            return null;
        }

        var normalized = NpaRuleSets.NormalizeCode(okpd2Code);
        var rule = await db.NpaRules
            .Where(row => row.DocumentId == active.Id && row.Okpd2Code == normalized)
            .OrderBy(row => row.IsInherited)
            .FirstOrDefaultAsync(cancellationToken);

        if (rule is null)
        {
            return null;
        }

        rule.Status = status;
        rule.Note = note;
        await db.SaveChangesAsync(cancellationToken);

        return ToInfo(code, rule);
    }

    private static NpaRuleInfo ToInfo(string code, NpaRuleRow rule) => new(
        code,
        rule.Okpd2Code,
        rule.RuleType,
        rule.ConditionText,
        rule.Quote,
        rule.IsInherited,
        rule.InheritedFrom,
        rule.Confidence,
        rule.Status,
        rule.Note,
        rule.SourcePage,
        rule.SourceRow);

    private static NpaDocumentInfo ToInfo(NpaDocumentRow row) => new(
        row.Code,
        row.Version,
        row.EoNumber,
        row.Number,
        row.Title,
        row.DocumentDate,
        row.PublishDate,
        row.PagesCount,
        row.SourceUrl,
        row.PdfSha256,
        row.OcrEngine,
        row.FetchedAt,
        row.ItemCount,
        CountUnresolved(row.UnresolvedJson),
        row.UnresolvedJson,
        ReferenceImportNormalizer.ComputeHash(row.Code, row.Version, row.ItemCount, row.FetchedAt),
        row.IsActive);

    /// <summary>Число неразобранных строк без разбора jsonb на каждом чтении списка.</summary>
    private static int CountUnresolved(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }
}
