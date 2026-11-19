namespace Csms.Infrastructure;

/// <summary>
/// Настройки автообновления Альбома форматов ЕИС.
///
/// Источник — публичная страница документов ЕИС, где выкладываются архивы
/// «Интеграционные схемы ЕИС версии X.Y». У архива стабильная ссылка вида
/// downloadDocument.html?id=..., но сам id меняется с каждой версией, поэтому
/// сначала читается страница, а номер версии и id берутся из неё.
///
/// Параметры лежат в appsettings.json (раздел EisSchemas:Update) и могу быть
/// переопределены переменными окружения EisSchemas__Update__*.
/// </summary>
public sealed class EisSchemaSourceOptions
{
    public const string SectionName = "EisSchemas:Update";

    /// <summary>Включить фоновую проверку обновлений.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Страница документов ЕИС с перечнем архивов схем.</summary>
    public string ListingUrl { get; set; } =
        "https://zakupki.gov.ru/epz/main/public/document/view.html" +
        "?sectionId=432&strictEqual=false&pageNumber=1&pageSize=50";

    /// <summary>Префикс ссылки на скачивание; к нему приклеивается id из страницы.</summary>
    public string DownloadUrlPrefix { get; set; } =
        "https://zakupki.gov.ru/epz/main/public/download/downloadDocument.html?id=";

    /// <summary>Как часто проверять, появилась ли новая версия.</summary>
    public int CheckIntervalMinutes { get; set; } = 720;

    /// <summary>Таймаут одного HTTP-запроса.</summary>
    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Защита от подмены архива очень большим файлом.</summary>
    public long MaxArchiveBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Защита от ZIP-бомбы: сколько файлов и байт распаковываем.</summary>
    public int MaxEntries { get; set; } = 500;

    public long MaxExtractedBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Устанавливать скачанную схему сразу. Если false — архив проверяется и
    /// остаётся в .incoming/&lt;версия&gt;, а ответственный решает, когда
    /// подменить активную схему.
    /// </summary>
    public bool AutoPromote { get; set; } = true;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(ListingUrl, UriKind.Absolute, out var listing) ||
            (listing.Scheme != Uri.UriSchemeHttps && !listing.IsLoopback))
        {
            throw new InvalidOperationException(
                "EisSchemas:Update:ListingUrl должен быть абсолютным HTTPS-адресом.");
        }

        if (!Uri.TryCreate(DownloadUrlPrefix, UriKind.Absolute, out var download) ||
            (download.Scheme != Uri.UriSchemeHttps && !download.IsLoopback))
        {
            throw new InvalidOperationException(
                "EisSchemas:Update:DownloadUrlPrefix должен быть абсолютным HTTPS-адресом.");
        }

        if (CheckIntervalMinutes < 1)
        {
            throw new InvalidOperationException(
                "EisSchemas:Update:CheckIntervalMinutes должен быть не меньше 1.");
        }
    }
}
