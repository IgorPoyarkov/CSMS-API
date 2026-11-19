using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure;

/// <summary>Снимок официального справочника, готовый к публикации в хранилище.</summary>
public sealed record EisNsiSnapshot(
    ReferenceImportRequest Request,
    int ItemCount,
    string SourceSha256);

/// <summary>
/// Загружает значения справочников НСИ с официального сайта ЕИС.
/// Использует те же публичные endpoint-ы, что и карточка закупки:
/// дерево строится вызовом children.html?parentId=<key> для каждой папки.
/// Никакие локальные моки или придуманные форматы здесь не участвуют.
/// </summary>
public sealed class EisNsiTreeImporter
{
    private const string BaseUrl = "https://zakupki.gov.ru";
    private const int MaxDepth = 12;
    private const int Concurrency = 8;
    private const int MaxNodes = 50000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<EisNsiTreeImporter> _logger;

    public EisNsiTreeImporter(HttpClient http, ILogger<EisNsiTreeImporter> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Коды справочников, для которых известен официальный источник.
    /// KLADR через дерево ЕИС не берём: там сотни тысяч узлов, такой обход
    /// требует отдельной выгрузки ФИАС/ГАР и потоковой загрузки в БД.
    /// </summary>
    public static IReadOnlyCollection<string> SupportedDatasets => ["nsiOKPD2"];

    public bool Supports(string dataset) => SupportedDatasets.Contains(dataset);

    public async Task<EisNsiSnapshot> FetchAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        var path = dataset switch
        {
            "nsiOKPD2" => "/epz/nsi/okpd2/",
            _ => throw new ArgumentException(
                $"Для справочника {dataset} нет официального источника. " +
                $"Поддерживаются: {string.Join(", ", SupportedDatasets)}."),
        };

        var nodes = await TraverseAsync(path, cancellationToken);
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException(
                $"ЕИС вернула пустое дерево справочника {dataset}.");
        }

        // Хеш считаем по стабильно упорядоченному снимку: повторный импорт
        // того же состояния даст ту же версию и будет отклонён как дубль.
        var canonical = new StringBuilder();
        foreach (var node in nodes.OrderBy(node => node.Code, StringComparer.Ordinal))
        {
            canonical.Append(node.Code).Append('\t').Append(node.Name).Append('\t')
                .Append(node.Raw.GetRawText()).Append('\n');
        }

        var sha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        var items = nodes
            .OrderBy(node => node.Code, StringComparer.Ordinal)
            .Select(node => new ReferenceImportItem(
                node.Code,
                node.ParentCode,
                node.Name,
                true,
                node.Raw))
            .ToArray();

        var request = new ReferenceImportRequest(
            dataset,
            $"eis-{dataset}-{sha256[..16].ToLowerInvariant()}",
            "Официальный сайт ЕИС zakupki.gov.ru, публичное дерево НСИ",
            BaseUrl + path,
            sha256,
            DateTimeOffset.UtcNow,
            items);

        return new EisNsiSnapshot(request, items.Length, sha256);
    }

    private async Task<List<TreeNode>> TraverseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var all = new List<TreeNode>();
        var visited = new HashSet<long>();
        var frontier = new List<Branch> { new(0, null) };

        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var loaded = new ConcurrentBag<TreeNode>();
            var next = new List<Branch>();

            _logger.LogInformation(
                "ЕИС НСИ {Path}: уровень {Depth}, папок {Count}",
                path, depth, frontier.Count);

            await Parallel.ForEachAsync(
                frontier,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Concurrency,
                    CancellationToken = cancellationToken,
                },
                async (branch, token) =>
                {
                    var url = $"{path}children.html?parentId={branch.ParentKey}";
                    var bytes = await _http.GetByteArrayAsync(url, token);
                    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));

                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return;
                    }

                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        var key = ReadKey(element);
                        var code = ReadText(element, "code");
                        if (key is null || string.IsNullOrWhiteSpace(code))
                        {
                            continue;
                        }

                        loaded.Add(new TreeNode(
                            key.Value,
                            code,
                            ReadText(element, "name"),
                            ReadBool(element, "isFolder"),
                            branch.ParentCode,
                            element.Clone()));
                    }
                });

            foreach (var node in loaded)
            {
                all.Add(node);
                if (all.Count > MaxNodes)
                {
                    throw new InvalidOperationException(
                        $"Дерево НСИ превысило предел {MaxNodes} узлов: " +
                        "нужна потоковая выгрузка, а не обход через API.");
                }

                if (node.IsFolder && node.Key > 0 && visited.Add(node.Key))
                {
                    next.Add(new Branch(node.Key, node.Code));
                }
            }

            frontier = next;
        }

        return all;
    }

    private static long? ReadKey(JsonElement element)
    {
        if (!element.TryGetProperty("key", out var key))
        {
            return null;
        }

        return key.ValueKind switch
        {
            JsonValueKind.Number when key.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(key.GetString(), out var number) => number,
            _ => null,
        };
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private sealed record Branch(long ParentKey, string? ParentCode);

    private sealed record TreeNode(
        long Key,
        string Code,
        string? Name,
        bool IsFolder,
        string? ParentCode,
        JsonElement Raw);
}
