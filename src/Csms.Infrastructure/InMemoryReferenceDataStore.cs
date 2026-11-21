using Csms.Application;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище справочников без PostgreSQL: нужно, чтобы локальный запуск и
/// smoke-тесты работали без БД. Данные живут до перезапуска процесса.
/// </summary>
public sealed class InMemoryReferenceDataStore : IReferenceDataStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ReferenceDatasetInfo>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ReferenceItemInfo>> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ReferenceDatasetInfo>> GetDatasetsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var all = _history.Values.SelectMany(versions => versions)
                .OrderByDescending(info => info.IsActive)
                .ThenByDescending(info => info.ImportedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ReferenceDatasetInfo>>(all);
        }
    }

    public Task<ReferenceDatasetInfo?> GetActiveAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var active = _history.TryGetValue(dataset, out var versions)
                ? versions.FirstOrDefault(info => info.IsActive)
                : null;

            return Task.FromResult(active);
        }
    }

    public Task<IReadOnlyList<ReferenceItemInfo>> QueryAsync(
        string dataset,
        string? search,
        string? parentCode,
        int take,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(dataset, out var source))
            {
                return Task.FromResult<IReadOnlyList<ReferenceItemInfo>>([]);
            }

            var query = source.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim();
                query = query.Where(item =>
                    item.Code.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    (item.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(parentCode))
            {
                query = query.Where(item =>
                    item.ParentCode?.Equals(parentCode.Trim(), StringComparison.OrdinalIgnoreCase) == true);
            }

            return Task.FromResult<IReadOnlyList<ReferenceItemInfo>>(
                query.OrderBy(item => item.Code, StringComparer.Ordinal)
                    .Skip(Math.Max(0, skip))
                    .Take(Math.Clamp(take, 1, 500))
                    .ToArray());
        }
    }

    public Task<IReadOnlyList<ReferenceItemInfo>> GetAllAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var source = _items.TryGetValue(dataset, out var items) ? items : [];
            return Task.FromResult<IReadOnlyList<ReferenceItemInfo>>([.. source]);
        }
    }

    public Task<IReadOnlyList<ReferenceItemInfo>> GetAllWithPayloadAsync(
        string dataset,
        CancellationToken cancellationToken = default) => GetAllAsync(dataset, cancellationToken);

    public Task<ReferenceDataUpdateEvent> PublishAsync(
        ReferenceImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var clean = ReferenceImportNormalizer.Normalize(request);        var importedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (!_history.TryGetValue(clean.Dataset, out var versions))
            {
                versions = [];
                _history[clean.Dataset] = versions;
            }

            var duplicate = versions.Any(info =>
                info.Version.Equals(clean.Version, StringComparison.OrdinalIgnoreCase));
            if (duplicate)
            {
                throw new ArgumentException(
                    $"Версия {clean.Version} справочника {clean.Dataset} уже импортирована.");
            }

            var previous = versions.FirstOrDefault(info => info.IsActive);
            if (previous is not null)
            {
                versions[versions.IndexOf(previous)] = previous with { IsActive = false };
            }

            var hash = ReferenceImportNormalizer.ComputeHash(
                clean.Dataset, clean.Version, clean.Items.Count, importedAt);
            var info = new ReferenceDatasetInfo(
                clean.Dataset,
                clean.Version,
                clean.SourceDocument,
                clean.SourceUrl,
                clean.SourceSha256,
                clean.PublishedAt,
                importedAt,
                clean.Items.Count,
                hash,
                IsActive: true);

            versions.Add(info);
            while (versions.Count > 5)
            {
                versions.RemoveAt(0);
            }

            var items = clean.Items.Select(item => new ReferenceItemInfo(
                item.Code!,
                item.ParentCode,
                item.Name,
                item.Actual ?? false,
                ReferenceImportNormalizer.SerializePayload(item.Payload))).ToArray();

            _items[clean.Dataset] = [.. items];

            return Task.FromResult(new ReferenceDataUpdateEvent(
                clean.Dataset,
                previous?.Version ?? "unknown",
                clean.Version,
                items.Length,
                hash,
                importedAt));
        }
    }
}
