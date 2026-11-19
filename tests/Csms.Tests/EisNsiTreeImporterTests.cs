using System.Net;
using System.Text;
using Csms.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csms.Tests;

/// <summary>Проверки обхода дерева НСИ на фикстурах ЕИС, без сети.</summary>
public sealed class EisNsiTreeImporterTests
{
    [Fact]
    public async Task Importer_Обходит_дерево_и_сохраняет_родительские_коды()
    {
        var handler = new FakeNsiHandler(url => url switch
        {
            _ when url.Contains("parentId=0") =>
                """
                [
                  {"key":10,"code":"A","name":"Сельское хозяйство","isFolder":true},
                  {"key":11,"code":"B","name":"Добыча","isFolder":true}
                ]
                """,
            _ when url.Contains("parentId=10") =>
                """
                [{"key":101,"code":"A.01","name":"Растениеводство","isFolder":false}]
                """,
            _ when url.Contains("parentId=11") =>
                """
                [{"key":111,"code":"B.05","name":"Рыболовство","isFolder":false}]
                """,
            _ => "[]",
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://zakupki.gov.ru"),
        };
        var importer = new EisNsiTreeImporter(http, NullLogger<EisNsiTreeImporter>.Instance);

        var snapshot = await importer.FetchAsync("nsiOKPD2");

        Assert.Equal(4, snapshot.ItemCount);
        Assert.Equal(64, snapshot.SourceSha256.Length);
        Assert.StartsWith("eis-nsiOKPD2-", snapshot.Request.Version);
        Assert.Equal("https://zakupki.gov.ru/epz/nsi/okpd2/", snapshot.Request.SourceUrl);

        var byCode = snapshot.Request.Items.ToDictionary(item => item.Code!);
        Assert.Equal("A", byCode["A.01"].ParentCode);
        Assert.Equal("B", byCode["B.05"].ParentCode);
        Assert.Equal("Сельское хозяйство", byCode["A"].Name);
        Assert.Contains("A.01", byCode["A.01"].Payload.GetRawText());
    }

    [Fact]
    public async Task Importer_Одинакового_снимка_даёт_одинаковую_версию()
    {
        FakeNsiHandler NewHandler() => new(_ =>
            """
            [{"key":1,"code":"1","name":"Позиция","isFolder":false}]
            """);

        static async Task<string> VersionFrom(FakeNsiHandler handler)
        {
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://zakupki.gov.ru"),
            };
            var importer = new EisNsiTreeImporter(http, NullLogger<EisNsiTreeImporter>.Instance);
            return (await importer.FetchAsync("nsiOKPD2")).Request.Version;
        }

        Assert.Equal(await VersionFrom(NewHandler()), await VersionFrom(NewHandler()));
    }

    [Fact]
    public async Task Importer_Неизвестный_справочник_отклоняется()
    {
        using var http = new HttpClient(new FakeNsiHandler(_ => "[]"))
        {
            BaseAddress = new Uri("https://zakupki.gov.ru"),
        };
        var importer = new EisNsiTreeImporter(http, NullLogger<EisNsiTreeImporter>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => importer.FetchAsync("nsiUNKNOWN"));
    }

    private sealed class FakeNsiHandler(Func<string, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = response(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
