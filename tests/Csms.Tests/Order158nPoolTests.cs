using Csms.Application;
using Csms.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csms.Tests;

/// <summary>
/// Пул 158н живёт в хранилище справочников. Тесты проверяют, что эталон
/// попадает в БД один раз, а Reads идут именно из неё, без TS-массива на клиенте.
/// </summary>
public sealed class Order158nPoolTests
{
    [Fact]
    public async Task Каталог_загружает_все_41_пункт_и_не_перезаписывает_версию()
    {
        var store = new InMemoryReferenceDataStore();
        var catalog = new Order158nPoolCatalog(store, NullLogger<Order158nPoolCatalog>.Instance);

        var first = await catalog.GetAsync();
        var second = await catalog.GetAsync();

        Assert.Equal("order-158n-2020-07-31-v1", first.Version);
        Assert.Equal(41, first.Items.Count);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(41, second.Items.Count);
        Assert.Equal(41, first.Items.Select(item => item.Id).Distinct().Count());

        var datasets = await store.GetDatasetsAsync();
        Assert.Single(datasets, dataset =>
            dataset.Code == Order158nPoolCatalog.DatasetCode && dataset.IsActive);
        Assert.Equal(41, datasets.Single(dataset => dataset.IsActive).ItemCount);
        Assert.Equal(64, first.SourceSha256?.Length);
        Assert.Contains("publication.pravo.gov.ru", first.SourceUrl ?? string.Empty);

        var firstItem = Assert.Single(first.Items, item => item.Id == "158n-3-1-1");
        Assert.Equal("п. 3.1.1", firstItem.Clause);
        Assert.Equal("planning", firstItem.FunctionCode);
        Assert.Contains("ОКПД2", firstItem.DefaultTaskDescription);
        Assert.NotEmpty(firstItem.RequiredInputs);
        Assert.NotEmpty(firstItem.Steps);
        Assert.Equal(0, firstItem.SortIndex);
    }

    [Fact]
    public async Task Каталог_читает_уже_записанную_версию_без_импорта_эталона()
    {
        var store = new InMemoryReferenceDataStore();
        var catalog = new Order158nPoolCatalog(store, NullLogger<Order158nPoolCatalog>.Instance);

        await catalog.GetAsync();

        // Заменяем весь набор в хранилище: если бы каталог читал stage-данные
        // напрямую, он вернул бы исходные 41 пункт вместо одного.
        await store.PublishAsync(new ReferenceImportRequest(
            Order158nPoolCatalog.DatasetCode,
            "custom-version",
            "Тестовая версия",
            null,
            null,
            DateTimeOffset.UtcNow,
            [
                new ReferenceImportItem(
                    "custom-1", "planning", "п. X", true,
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new Order158nPoolItemInfo(
                            "custom-1", "п. X", "X", "planning", "Действие",
                            "Задача", "Описание", 1, "с даты", "п. X", null, null,
                            [], "Критерий", [], 0))),
            ]));

        var result = await catalog.GetAsync();

        Assert.Equal("custom-version", result.Version);
        var item = Assert.Single(result.Items);
        Assert.Equal("custom-1", item.Id);
    }
}
