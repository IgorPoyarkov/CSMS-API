using System.Net;
using System.Security.Cryptography;
using System.Text;
using Csms.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csms.Tests;

/// <summary>
/// Проверку и установку тестируем на поддельной странице ЕИС. Настоящий XLSX
/// можно прогнать отдельно через EIS_COUNTERPARTIES_XLSX в тестах парсера.
/// </summary>
public sealed class EisCounterpartyDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "csms-counterparties-tests-" + Guid.NewGuid().ToString("N"));

    private static string Listing(string id = "44571", string date = "20 ноября 2026") => $"""
        <html><body>
          <article class="docs-group">
            <div href="/epz/main/public/document/view.html?sectionId={id}"></div>
            <span class="docs-date">{date}</span>
            <img alt="Microsoft Excel Document" src="/epz/static/img/icons/type/xlsx.svg" />
            <a class="docs-title"
               href="/epz/main/public/download/downloadDocument.html?id={id}">
              Информация о привлечении участника закупки к административной
              ответственности по ст. 19.28 КоАП
            </a>
          </article>
        </body></html>
        """;

    private static byte[] Xlsx(
        string inn = "1650098034",
        string decision = "46080",
        string effective = "46279") => EisCounterpartyXlsxFixture.Create(
        new EisCounterpartyXlsxFixture.Row(
            inn,
            "1031616000503",
            "166101001",
            "ООО «Пример»",
            "Юридическое лицо РФ",
            "Суд",
            "5-2-224/2026",
            decision,
            effective));

    [Fact]
    public async Task Новая_выгрузка_устанавливается_и_индексируется()
    {
        var handler = new FakeHandler(Listing(), Xlsx());
        var (downloader, catalog, _) = Create(handler);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", result.State);
        Assert.True(result.ParsedNow, result.LastError);
        Assert.Equal("44571", result.SourceId);
        Assert.Equal("20 ноября 2026", result.PublishedDate);
        Assert.Equal("44571", catalog.Source?.SourceId);
        Assert.True(catalog.Check("1650098034", null, null).IsListed);
        Assert.True(File.Exists(Path.Combine(_root, "current", "document.xlsx")));
        Assert.True(File.Exists(Path.Combine(_root, "current", "entries.json")));
        Assert.True(File.Exists(Path.Combine(_root, "current", ".manifest.json")));
        Assert.Equal(new Uri("https://example.test/list"), handler.LastReferer);
    }

    [Fact]
    public async Task Неизменившиеся_id_и_sha_не_парсятся_повторно()
    {
        var file = Xlsx();
        var handler = new FakeHandler(Listing(), file, file);
        var (downloader, catalog, _) = Create(handler);

        var first = await downloader.CheckAndInstallAsync(CancellationToken.None);
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", first.State);
        Assert.Equal("UpToDate", second.State);
        Assert.False(second.ParsedNow);
        Assert.Equal(2, handler.FileRequests);
        Assert.Equal("44571", catalog.Source?.SourceId);
    }

    [Fact]
    public async Task Изменение_id_при_том_же_файле_устанавливает_новый_снимок()
    {
        var file = Xlsx();
        var handler = new FakeHandler(
            [Listing("44571"), Listing("44572")], file, file);
        var (downloader, catalog, _) = Create(handler);

        await downloader.CheckAndInstallAsync(CancellationToken.None);
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", second.State);
        Assert.True(second.ParsedNow);
        Assert.Equal("44572", second.SourceId);
        Assert.Equal("44572", catalog.Source?.SourceId);
    }

    [Fact]
    public async Task Изменение_sha_при_том_же_id_заменяет_старые_данные()
    {
        var handler = new FakeHandler(
            Listing(), Xlsx("1650098034"), Xlsx("0100002636"));
        var (downloader, catalog, _) = Create(handler);

        await downloader.CheckAndInstallAsync(CancellationToken.None);
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", second.State);
        Assert.True(second.ParsedNow);
        Assert.False(catalog.Check("1650098034", null, null).IsListed);
        Assert.True(catalog.Check("0100002636", null, null).IsListed);
    }

    [Fact]
    public async Task Бракованная_выгрузка_не_заменяет_установленный_снимок()
    {
        var good = Xlsx();
        var handler = new FakeHandler(
            [Listing("44571"), Listing("44572")],
            good,
            Encoding.UTF8.GetBytes("<html>404</html>"));
        var (downloader, catalog, _) = Create(handler);

        await downloader.CheckAndInstallAsync(CancellationToken.None);
        var before = await File.ReadAllBytesAsync(Path.Combine(_root, "current", "document.xlsx"));
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Error", second.State);
        Assert.Contains("не XLSX", second.LastError, StringComparison.Ordinal);
        Assert.Equal("44571", catalog.Source?.SourceId);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(good)),
            Convert.ToHexString(SHA256.HashData(before)));
        Assert.False(Directory.Exists(Path.Combine(_root, "current.previous")));
    }

    [Fact]
    public async Task Слишком_короткая_выгрузка_не_устанавливается()
    {
        var handler = new FakeHandler(Listing(), Xlsx());
        var (downloader, catalog, _) = Create(handler, minRowCount: 100);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Error", result.State);
        Assert.Contains("меньше порога", result.LastError, StringComparison.Ordinal);
        Assert.Null(catalog.Source);
    }

    [Fact]
    public async Task Отключённая_проверка_не_обращается_к_сети()
    {
        var handler = new FakeHandler(Listing(), Xlsx());
        var (downloader, _, _) = Create(handler, enabled: false);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Disabled", result.State);
        Assert.Equal(0, handler.ListingRequests);
        Assert.Equal(0, handler.FileRequests);
    }

    private (
        EisCounterpartyDownloader Downloader,
        EisCounterpartyCatalog Catalog,
        EisCounterpartyUpdateStatus Status) Create(
            FakeHandler handler,
            bool enabled = true,
            int minRowCount = 1)
    {
        var options = new EisCounterpartySourceOptions
        {
            Enabled = enabled,
            ListingUrl = "https://example.test/list",
            DownloadUrlPrefix = "https://example.test/doc?id=",
            HttpTimeoutSeconds = 10,
            MinRowCount = minRowCount,
        };
        var status = new EisCounterpartyUpdateStatus();
        var catalog = new EisCounterpartyCatalog(_root);
        var downloader = new EisCounterpartyDownloader(
            new HttpClient(handler),
            options,
            status,
            catalog,
            NullLogger<EisCounterpartyDownloader>.Instance,
            _root);

        return (downloader, catalog, status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Поддельная страница ЕИС и очередь ответов с файлами.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _listings;
        private readonly Queue<byte[]> _files;

        public FakeHandler(string listing, params byte[][] files)
            : this([listing], files)
        {
        }

        public FakeHandler(IEnumerable<string> listings, params byte[][] files)
        {
            _listings = new Queue<string>(listings);
            _files = new Queue<byte[]>(files);
        }

        public int ListingRequests { get; private set; }

        public int FileRequests { get; private set; }

        public Uri? LastReferer { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal))
            {
                ListingRequests++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        _listings.Count > 0 ? _listings.Dequeue() : Listing()),
                });
            }

            FileRequests++;
            LastReferer = request.Headers.Referrer;
            var bytes = _files.Count > 0 ? _files.Dequeue() : Xlsx();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }
}
