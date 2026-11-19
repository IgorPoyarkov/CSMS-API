using System.IO.Compression;
using System.Net;
using System.Text;
using Csms.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csms.Tests;

/// <summary>
/// Автообновление проверяем на поддельной странице ЕИС и синтетических XSD:
/// настоящий Альбом скачивается вручную, а тесты не зависят от сети.
/// </summary>
public sealed class EisSchemaDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "csms-schema-update-tests-" + Guid.NewGuid().ToString("N"));

    private const string Schema = """
        <?xml version="1.0" encoding="utf-8"?>
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="doc">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="number" type="xs:string"/>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string Listing = """
        <html><body>
          <a href="/epz/main/public/download/downloadDocument.html?id=44609">
            Интеграционные схемы ЕИС версии 16.3.3
          </a>
          <a href="/epz/main/public/download/downloadDocument.html?id=44668">
            Интеграционные схемы ЕИС версии 17.0
          </a>
          <a href="/epz/main/public/download/downloadDocument.html?id=44667">
            Описание изменений к версии 17.0
          </a>
        </body></html>
        """;

    [Fact]
    public async Task Берётся_самая_новая_версия_и_устанавливается()
    {
        var handler = new FakeHandler(Listing, CreateZip("Версия 17.0", ("doc.xsd", Schema)));
        var downloader = CreateDownloader(handler);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", result.State);
        Assert.Equal("17.0", result.Version);
        Assert.Equal(1, result.FileCount);
        Assert.Equal("44668", result.SourceId);
        Assert.True(File.Exists(Path.Combine(_root, "17.0", "doc.xsd")));

        var catalog = new EisSchemaCatalog(_root);
        Assert.True(catalog.HasSchema("17.0"));
        Assert.Empty(catalog.Validate("<doc><number>1</number></doc>", "17.0"));
    }

    [Fact]
    public async Task Неизменившийся_архив_не_скачивается_повторно()
    {
        var archive = CreateZip("Версия 17.0", ("doc.xsd", Schema));
        var handler = new FakeHandler(Listing, archive, archive);
        var downloader = CreateDownloader(handler);

        var first = await downloader.CheckAndInstallAsync(CancellationToken.None);
        var manifest = Path.Combine(_root, "17.0", ".manifest.json");
        Assert.True(File.Exists(manifest), $"Манифест отсутствует: {manifest}");
        Assert.NotEmpty(File.ReadAllText(manifest));
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", first.State);
        Assert.True(second.State == "UpToDate", second.LastError);
        Assert.Equal(1, handler.ArchiveRequests);
        Assert.Equal(2, handler.ListingRequests);
    }

    [Fact]
    public async Task Бракованные_XSD_не_заменяют_уже_установленную_версию()
    {
        // Та же версия, но новая редакция документа ЕИС — id 44669.
        var newerListing = Listing.Replace("id=44668", "id=44669", StringComparison.Ordinal);
        var bad = CreateZip("Версия 17.0", ("broken.xsd", "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'>"));
        var handler = new FakeHandler(
            new[] { Listing, newerListing },
            CreateZip("Версия 17.0", ("doc.xsd", Schema)),
            bad);
        var downloader = CreateDownloader(handler);

        await downloader.CheckAndInstallAsync(CancellationToken.None);
        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Error", result.State);
        Assert.Contains("не компилируются", result.LastError, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "17.0", "doc.xsd")));
        Assert.False(File.Exists(Path.Combine(_root, "17.0", "broken.xsd")));
    }

    [Fact]
    public async Task Выход_за_каталог_из_архива_не_создаёт_файлы_снаружи()
    {
        var zip = CreateZip("Версия 17.0", ("doc.xsd", Schema), ("../evil.xsd", Schema));
        var handler = new FakeHandler(Listing, zip);
        var downloader = CreateDownloader(handler);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", result.State);
        Assert.Equal(1, result.FileCount);
        Assert.False(File.Exists(Path.Combine(_root, "evil.xsd")));
        Assert.False(File.Exists(Path.Combine(_root, ".incoming", "evil.xsd")));
    }

    [Fact]
    public async Task Без_auto_promote_архив_только_подготавливается()
    {
        var handler = new FakeHandler(Listing, CreateZip("Версия 17.0", ("doc.xsd", Schema)));
        var downloader = CreateDownloader(handler, autoPromote: false);

        var first = await downloader.CheckAndInstallAsync(CancellationToken.None);
        var second = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.True(first.State == "Staged", $"Первый вызов: {first.State}, {first.LastError}");
        Assert.True(second.State == "Staged", $"Второй вызов: {second.State}, {second.LastError}");
        Assert.Equal(1, handler.ArchiveRequests);
        Assert.False(File.Exists(Path.Combine(_root, "17.0", "doc.xsd")));

        var staged = Directory.EnumerateFiles(
            Path.Combine(_root, ".incoming"), "doc.xsd", SearchOption.AllDirectories);
        Assert.Single(staged);
    }

    /// <summary>
    /// Проверка на настоящем архиве ЕИС без сети: путь к скачанному ZIP даётся
    /// переменной EIS_XSD_ZIP. Без неё тест пропускается — репозиторий не
    /// должен тащить официальный Альбом в состав репозитория.
    /// </summary>
    [Fact]
    public async Task Настоящий_архив_ЕИС_устанавливается_и_компилируется()
    {
        var zipPath = Environment.GetEnvironmentVariable("EIS_XSD_ZIP");

        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return;
        }

        var handler = new FakeHandler(Listing, File.ReadAllBytes(zipPath));
        var downloader = CreateDownloader(handler);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Installed", result.State);
        Assert.True(result.FileCount > 20, result.LastError);
        Assert.Empty(EisSchemaCatalog.CheckDirectory(Path.Combine(_root, "17.0")));
    }

    [Fact]
    public async Task Отключённое_обновление_не_обращается_к_сети()
    {
        var handler = new FakeHandler(Listing, CreateZip("Версия 17.0", ("doc.xsd", Schema)));
        var downloader = CreateDownloader(handler, enabled: false);

        var result = await downloader.CheckAndInstallAsync(CancellationToken.None);

        Assert.Equal("Disabled", result.State);
        Assert.Equal(0, handler.ListingRequests);
        Assert.Equal(0, handler.ArchiveRequests);
    }

    private EisSchemaDownloader CreateDownloader(
        FakeHandler handler,
        bool autoPromote = true,
        bool enabled = true)
    {
        // HttpClient живёт до конца процесса теста: обновлятор держит ссылку.
        var http = new HttpClient(handler);
        var options = new EisSchemaSourceOptions
        {
            Enabled = enabled,
            AutoPromote = autoPromote,
            ListingUrl = "https://example.test/list",
            DownloadUrlPrefix = "https://example.test/doc?id=",
            HttpTimeoutSeconds = 10,
        };

        return new EisSchemaDownloader(
            http,
            options,
            new EisSchemaUpdateStatus(),
            NullLogger<EisSchemaDownloader>.Instance,
            _root);
    }

    private static byte[] CreateZip(string folder, params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(folder + "/" + file.Name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Поддельная страница ЕИС и очередь ответов с архивами.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _listings;
        private readonly Queue<byte[]> _archives;

        public FakeHandler(string listing, params byte[][] archives)
            : this(new[] { listing }, archives)
        {
        }

        public FakeHandler(IEnumerable<string> listings, params byte[][] archives)
        {
            _listings = new Queue<string>(listings);
            _archives = new Queue<byte[]>(archives);
        }

        public int ListingRequests { get; private set; }

        public int ArchiveRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/list", StringComparison.Ordinal))
            {
                ListingRequests++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_listings.Count > 0 ? _listings.Dequeue() : Listing),
                });
            }

            ArchiveRequests++;
            var bytes = _archives.Count > 0 ? _archives.Dequeue() : [];

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }
}
