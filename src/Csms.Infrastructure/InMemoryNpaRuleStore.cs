using Csms.Application;
using Csms.Infrastructure.Npa;

namespace Csms.Infrastructure;

/// <summary>
/// Правила НПА в памяти: локальный запуск и тесты без PostgreSQL. Данные живут
/// до перезапуска процесса — это тот же режим, что и у справочников НСИ.
/// </summary>
public sealed class InMemoryNpaRuleStore : INpaRuleStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<NpaDocumentInfo>> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<NpaRuleInfo>> _rules = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<NpaDocumentInfo>> GetDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<NpaDocumentInfo>>(
                _documents.Values.SelectMany(versions => versions)
                    .OrderByDescending(info => info.IsActive)
                    .ThenByDescending(info => info.FetchedAt)
                    .ToArray());
        }
    }

    public Task<NpaDocumentInfo?> GetActiveAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var active = Active(code);
            return Task.FromResult(active);
        }
    }

    public Task<IReadOnlyList<NpaRuleInfo>> GetRulesAsync(
        string code,
        string? search,
        string? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var active = Active(code);
            if (active is null || !_rules.TryGetValue(Key(active.Code, active.Version), out var rules))
            {
                return Task.FromResult<IReadOnlyList<NpaRuleInfo>>([]);
            }

            var query = rules.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(rule => rule.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim();
                query = query.Where(rule =>
                    rule.Okpd2Code.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    (rule.ConditionText?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            return Task.FromResult<IReadOnlyList<NpaRuleInfo>>(
                query.OrderBy(rule => rule.Okpd2Code, StringComparer.Ordinal)
                    .ThenBy(rule => rule.IsInherited)
                    .Skip(Math.Clamp(skip, 0, 100_000))
                    .Take(Math.Clamp(take, 1, 5000))
                    .ToArray());
        }
    }

    public Task<NpaEffectiveRule?> ResolveAsync(
        string code,
        string okpd2Code,
        bool includePending,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var active = Active(code);
            if (active is null || !_rules.TryGetValue(Key(active.Code, active.Version), out var rules))
            {
                return Task.FromResult<NpaEffectiveRule?>(null);
            }

            var normalized = NpaRuleSets.NormalizeCode(okpd2Code);
            var direct = rules
                .Where(rule => includePending ||
                               rule.Status is NpaRuleStatuses.Auto or NpaRuleStatuses.Approved)
                .ToList();

            var match = NpaRuleHierarchy.Resolve(direct, normalized);
            return Task.FromResult(match is null
                ? null
                : new NpaEffectiveRule(
                    code,
                    normalized,
                    match.Okpd2Code,
                    match.RuleType,
                    match.ConditionText,
                    match.IsInherited,
                    match.InheritedFrom,
                    match.Confidence,
                    match.Status,
                    active.Version));
        }
    }

    public Task<bool> VersionExistsAsync(
        string code,
        string version,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _documents.TryGetValue(code, out var versions) &&
                versions.Any(info => info.Version.Equals(version, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<NpaDocumentUpdateEvent> PublishAsync(
        NpaDocumentImport import,
        CancellationToken cancellationToken = default)
    {
        var hash = NpaRuleSets.ComputeHash(import);
        var fetchedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (!_documents.TryGetValue(import.Code, out var versions))
            {
                versions = [];
                _documents[import.Code] = versions;
            }

            if (versions.Any(info => info.Version.Equals(import.Version, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Версия {import.Version} акта {import.Code} уже загружена.");
            }

            var previous = versions.FirstOrDefault(info => info.IsActive);
            if (previous is not null && import.Promote)
            {
                versions[versions.IndexOf(previous)] = previous with { IsActive = false };
            }

            var info = new NpaDocumentInfo(
                import.Code,
                import.Version,
                import.EoNumber,
                import.Number,
                import.Title,
                import.DocumentDate,
                import.PublishDate,
                import.PagesCount,
                import.SourceUrl,
                import.PdfSha256,
                import.OcrEngine,
                fetchedAt,
                import.Rules.Count,
                CountUnresolved(import.UnresolvedJson),
                import.UnresolvedJson,
                hash,
                import.Promote);

            versions.Add(info);
            _rules[Key(import.Code, import.Version)] = import.Rules
                .Select(rule => new NpaRuleInfo(
                    import.Code,
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
                    rule.SourceRow))
                .ToList();

            return Task.FromResult(new NpaDocumentUpdateEvent(
                import.Code,
                previous?.Version ?? "unknown",
                import.Version,
                import.Rules.Count,
                hash,
                fetchedAt));
        }
    }

    public Task<NpaRuleInfo?> VerifyAsync(
        string code,
        string okpd2Code,
        string status,
        string? note,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var active = Active(code);
            if (active is null || !_rules.TryGetValue(Key(active.Code, active.Version), out var rules))
            {
                return Task.FromResult<NpaRuleInfo?>(null);
            }

            var normalized = NpaRuleSets.NormalizeCode(okpd2Code);
            var rule = rules
                .Where(item => item.Okpd2Code == normalized)
                .OrderBy(item => item.IsInherited)
                .FirstOrDefault();

            if (rule is null)
            {
                return Task.FromResult<NpaRuleInfo?>(null);
            }

            var updated = rule with { Status = status, Note = note };
            rules[rules.IndexOf(rule)] = updated;
            return Task.FromResult<NpaRuleInfo?>(updated);
        }
    }

    private NpaDocumentInfo? Active(string code)
    {
        var active = _documents.TryGetValue(code, out var versions)
            ? versions.FirstOrDefault(info => info.IsActive)
            : null;

        // Npa:AutoPromote=false оставляет прежнюю версию активной, но если
        // активной нет вовсе — показываем последнюю подготовленную.
        return active ?? (versions is { Count: > 0 } ? versions[^1] : null);
    }

    private static string Key(string code, string version) => code + "|" + version;

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
