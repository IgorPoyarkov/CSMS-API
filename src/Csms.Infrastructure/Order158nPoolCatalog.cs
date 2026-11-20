using System.Text.Json;
using Csms.Application;
using Microsoft.Extensions.Logging;

namespace Csms.Infrastructure;

/// <summary>
/// Пул задач по Приказу № 158н как версионируемый справочник.
///
/// Каталог не держит пункты в коде: он читает активную версию из
/// <see cref="IReferenceDataStore"/> (память или PostgreSQL). Если версии ещё
/// нет, один раз импортирует эталон из официального приказа. Все последующие
/// обращения идут в БД, включая поиск конкретного пункта для агентов и форм.
/// </summary>
public sealed class Order158nPoolCatalog(
    IReferenceDataStore store,
    ILogger<Order158nPoolCatalog> logger) : IOrder158nPoolCatalog
{
    /// <summary>Код справочника в ref.datasets, видимый в /api/references/datasets.</summary>
    public const string DatasetCode = "order158nPool";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _seedGate = new(1, 1);

    public async Task<Order158nPoolSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSeededAsync(cancellationToken);

        var active = await store.GetActiveAsync(DatasetCode, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Активная версия справочника {DatasetCode} не найдена.");
        // Пул — небольшой справочник, каждая строка хранит весь объект пункта.
        // Обычный GetAllAsync намеренно не тянет payload (там ОКПД2 на десятки МБ).
        var rows = await store.GetAllWithPayloadAsync(DatasetCode, cancellationToken);

        var items = rows
            .Select(row => JsonSerializer.Deserialize<Order158nPoolItemInfo>(row.PayloadJson, Options)
                ?? throw new InvalidOperationException(
                    $"Пункт {row.Code} справочника {DatasetCode} не разбирается."))
            .OrderBy(item => item.SortIndex)
            .ToArray();

        return new Order158nPoolSnapshot(
            active.Version,
            active.SourceDocument ?? "Приказ Минфина России от 31.07.2020 N 158н",
            active.SourceUrl,
            active.SourceSha256,
            active.ImportedAt,
            items);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (await store.GetActiveAsync(DatasetCode, cancellationToken) is not null)
        {
            return;
        }

        await _seedGate.WaitAsync(cancellationToken);
        try
        {
            // Второй процесс мог импортировать версию, пока ждали семафор.
            if (await store.GetActiveAsync(DatasetCode, cancellationToken) is not null)
            {
                return;
            }

            var (seed, sha256) = Order158nPoolSeed.Load();
            var request = new ReferenceImportRequest(
                DatasetCode,
                seed.Version,
                seed.SourceDocument,
                seed.SourceUrl,
                sha256,
                // Npgsql для timestamp with time zone принимает только offset 0.
                seed.PublishedAt.ToUniversalTime(),
                seed.Items.Select(item => new ReferenceImportItem(
                    item.Id,
                    item.FunctionCode,
                    item.Clause,
                    true,
                    JsonSerializer.SerializeToElement(item, Options))).ToArray());

            try
            {
                await store.PublishAsync(request, cancellationToken);
                logger.LogInformation(
                    "Пул 158н импортирован в БД: версия {Version}, пунктов {Count}",
                    seed.Version,
                    seed.Items.Count);
            }
            catch (ArgumentException)
            {
                // Гонка двух инстансов: версия уже установлена, работаем с ней.
                if (await store.GetActiveAsync(DatasetCode, cancellationToken) is null)
                {
                    throw;
                }
            }
        }
        finally
        {
            _seedGate.Release();
        }
    }
}
