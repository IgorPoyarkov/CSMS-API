using System.Text.Json;
using Csms.Application;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>Проверки импорта справочников без обращения к PostgreSQL и ЕИС.</summary>
public sealed class ReferenceDataTests
{
    [Fact]
    public void Normalizer_Достаёт_коды_из_вложенного_payload_КТРУ()
    {
        using var document = JsonDocument.Parse("""
        {
          "position": {
            "data": {
              "code": "26.20.11.110",
              "name": "Компьютер",
              "OKPD2": { "code": "26.20.11" }
            }
          },
          "actual": true
        }
        """);

        var request = new ReferenceImportRequest(
            "nsiKTRU", "1", null, null, null, null,
            [new ReferenceImportItem(null, null, null, null, document.RootElement)]);

        var clean = ReferenceImportNormalizer.Normalize(request);

        Assert.Equal("26.20.11.110", clean.Items[0].Code);
        Assert.Equal("26.20.11", clean.Items[0].ParentCode);
        Assert.Equal("Компьютер", clean.Items[0].Name);
        Assert.True(clean.Items[0].Actual);
        Assert.Contains("26.20.11.110", ReferenceImportNormalizer.SerializePayload(clean.Items[0].Payload));
    }

    [Theory]
    [InlineData("nsiOKEI", "{\"OKEI\":\"796\",\"name\":\"штука\"}", "796", "штука")]
    [InlineData("nsiOKSM", "{\"OKSM\":\"643\",\"shortName\":\"Россия\"}", "643", "Россия")]
    [InlineData("nsiOKTMO", "{\"OKTMO\":\"45301000\",\"fullName\":\"Москва\"}", "45301000", "Москва")]
    [InlineData("nsiKBK", "{\"KBK\":\"00000000000000000\",\"fullName\":\"Доход\"}", "00000000000000000", "Доход")]
    [InlineData("nsiCurrency", "{\"digitalCode\":\"840\",\"shortName\":\"Доллар США\"}", "840", "Доллар США")]
    public void Normalizer_Понимает_поля_слабых_справочников(
        string dataset,
        string json,
        string expectedCode,
        string expectedName)
    {
        using var document = JsonDocument.Parse(json);
        var request = new ReferenceImportRequest(
            dataset, "1", null, null, null, null,
            [new ReferenceImportItem(null, null, null, null, document.RootElement)]);

        var clean = ReferenceImportNormalizer.Normalize(request);

        Assert.Equal(expectedCode, clean.Items[0].Code);
        Assert.Equal(expectedName, clean.Items[0].Name);
    }

    [Fact]
    public void Catalog_Описывает_все_датасеты_цепочки_ЕИС()
    {
        var codes = ReferenceDatasetCatalog.All.Select(item => item.Code).ToArray();

        Assert.Contains("nsiOKPD2", codes);
        Assert.Contains("nsiKTRU", codes);
        Assert.Contains("nsiKLADR", codes);
        Assert.Contains("nsiOKSM", codes);
        Assert.Contains("nsiOKTMO", codes);
        Assert.Contains("nsiKBK", codes);
        Assert.Contains("nsiOKEI", codes);
        Assert.Contains("nsiCurrency", codes);
        Assert.Equal("OKEI", ReferenceDatasetCatalog.CodeFields("nsiOKEI")[0]);
    }

    [Fact]
    public async Task Store_Публикует_версию_и_оставляет_старую_в_истории()
    {
        var store = new InMemoryReferenceDataStore();

        var first = await store.PublishAsync(Request("nsiOKPD2", "1",
            Item("32.50.13.190", "Средства диагностики", "32.50.13")));
        var second = await store.PublishAsync(Request("nsiOKPD2", "2",
            Item("26.20.11.110", "Компьютеры", "26.20.11")));

        Assert.Equal("unknown", first.FromVersion);
        Assert.Equal("1", second.FromVersion);
        Assert.Equal("2", (await store.GetActiveAsync("nsiOKPD2"))!.Version);

        var all = await store.GetDatasetsAsync();
        Assert.Contains(all, item => item.Version == "1" && !item.IsActive);
        Assert.Contains(all, item => item.Version == "2" && item.IsActive);

        var found = await store.QueryAsync("nsiOKPD2", "компьютер", "26.20.11", 10);
        Assert.Single(found);
        Assert.Equal("26.20.11.110", found[0].Code);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PublishAsync(Request("nsiOKPD2", "2", Item("1", "Дубль"))));
    }

    private static ReferenceImportRequest Request(string dataset, string version, params ReferenceImportItem[] items) =>
        new(dataset, version, "тестовая выгрузка", "https://example.test/source", "sha", null, items);

    private static ReferenceImportItem Item(string code, string name, string? parentCode = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            code,
            name,
            parentCode,
            actual = true,
        }));

        return new ReferenceImportItem(code, parentCode, name, true, document.RootElement.Clone());
    }
}
