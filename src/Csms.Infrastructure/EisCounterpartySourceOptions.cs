namespace Csms.Infrastructure;

/// <summary>
/// Настройки ежедневной проверки публичной выгрузки ЕИС о привлечении по
/// ст. 19.28 КоАП. Раздел конфигурации EisCounterparties:Update.
/// </summary>
public sealed class EisCounterpartySourceOptions
{
    public const string SectionName = "EisCounterparties:Update";

    public bool Enabled { get; set; } = true;

    /// <summary>Страница документа ЕИС с перечнем выгрузок.</summary>
    public string ListingUrl { get; set; } =
        "https://zakupki.gov.ru/epz/main/public/document/view.html" +
        "?sectionId=2369&strictEqual=false";

    /// <summary>Префикс ссылки на скачивание; id берётся со страницы списка.</summary>
    public string DownloadUrlPrefix { get; set; } =
        "https://zakupki.gov.ru/epz/main/public/download/downloadDocument.html?id=";

    /// <summary>Раз в сутки — публикация выгрузки не чаще этого.</summary>
    public int CheckIntervalMinutes { get; set; } = 1440;

    public int HttpTimeoutSeconds { get; set; } = 60;

    /// <summary>Защита от подмены файла очень большим объёмом.</summary>
    public long MaxFileBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Ниже этого числа строк выгрузку считаем урезанной и не устанавливаем.
    /// </summary>
    public int MinRowCount { get; set; } = 100;

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
                "EisCounterparties:Update:ListingUrl должен быть абсолютным HTTPS-адресом.");
        }

        if (!Uri.TryCreate(DownloadUrlPrefix, UriKind.Absolute, out var download) ||
            (download.Scheme != Uri.UriSchemeHttps && !download.IsLoopback))
        {
            throw new InvalidOperationException(
                "EisCounterparties:Update:DownloadUrlPrefix должен быть абсолютным HTTPS-адресом.");
        }

        if (CheckIntervalMinutes < 1)
        {
            throw new InvalidOperationException(
                "EisCounterparties:Update:CheckIntervalMinutes должен быть не меньше 1.");
        }

        if (MinRowCount < 1)
        {
            throw new InvalidOperationException(
                "EisCounterparties:Update:MinRowCount должен быть не меньше 1.");
        }

        if (MaxFileBytes < 1024)
        {
            throw new InvalidOperationException(
                "EisCounterparties:Update:MaxFileBytes должен быть не меньше 1024.");
        }
    }
}
