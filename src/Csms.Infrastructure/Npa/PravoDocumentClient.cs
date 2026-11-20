using System.Text.Json;

namespace Csms.Infrastructure.Npa;

/// <summary>Ссылка на опубликованный акт с портала publication.pravo.gov.ru.</summary>
public sealed record PravoDocumentRef(
    string EoNumber,
    string? Number,
    string Title,
    DateOnly? DocumentDate,
    DateOnly? PublishDate,
    int PagesCount,
    long PdfFileLength)
{
    public string PdfUrl => $"/file/pdf?eoNumber={EoNumber}";
}

/// <summary>
/// Официальный источник текста актов: раздел «Официальное опубликование»
/// (publication.pravo.gov.ru). API только читает; PDF — подписанный скан,
/// поэтому текст из него достаётся распознаванием.
/// </summary>
public sealed class PravoDocumentClient(HttpClient http)
{
    /// <summary>
    /// Портал нестабилен: в пик нагрузки отдаёт 500.30 примерно в половине
    /// запросов. Повторяем с нарастающей паузой, чтобы ночной цикл не падал
    /// на транзиент, но и не висел бесконечно.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20),
    ];

    /// <summary>Все публикации с указанным номером акта (включая одноимённые акты других лет).</summary>
    public async Task<IReadOnlyList<PravoDocumentRef>> FindAsync(
        string number,
        string block,
        CancellationToken cancellationToken = default)
    {
        var url = "/api/Documents?Block=" + Uri.EscapeDataString(block) +
                  "&Number=" + Uri.EscapeDataString(number) +
                  // Портал отдаёт 400 на PageSize > 30 — это его предел.
                  "&NumberSearchType=0&PageSize=30";

        using var response = await GetWithRetryAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PravoDocumentRef>();
        foreach (var item in items.EnumerateArray())
        {
            var eoNumber = Text(item, "eoNumber");
            if (string.IsNullOrWhiteSpace(eoNumber))
            {
                continue;
            }

            result.Add(new PravoDocumentRef(
                eoNumber,
                Text(item, "number"),
                Text(item, "complexName") ?? Text(item, "name") ?? string.Empty,
                DateOnly(item, "documentDate"),
                DateOnly(item, "publishDateShort"),
                (int)(Number(item, "pagesCount") ?? 0),
                (long)(Number(item, "pdfFileLength") ?? 0)));
        }

        return result;
    }

    /// <summary>Скачивает PDF официального опубликования.</summary>
    public async Task<byte[]> DownloadPdfAsync(string eoNumber, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(
            "/file/pdf?eoNumber=" + Uri.EscapeDataString(eoNumber),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Повторяем только сетевые ошибки и 5xx; 4xx — ошибка запроса, её не лечим.</summary>
    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string url,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await http.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode is >= 500 and <= 599 && attempt < RetryDelays.Length)
                {
                    response.Dispose();
                    await Task.Delay(RetryDelays[attempt], cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateOnly? DateOnly(JsonElement element, string name)
    {
        var text = Text(element, name);
        return System.DateOnly.TryParse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }
}
