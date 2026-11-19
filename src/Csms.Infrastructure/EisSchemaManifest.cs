namespace Csms.Infrastructure;

/// <summary>
/// Манифест установленной (или подготовленной) версии XSD ЕИС. Лежит рядом
/// с файлами схем в <c>.manifest.json</c> и позволяет не скачивать архив
/// повторно, если его контрольная сумма уже есть на диске.
/// </summary>
public sealed record EisSchemaManifest(
    string Version,
    string SourceId,
    string Sha256,
    int FileCount,
    DateTimeOffset InstalledAt,
    string SourceUrl);
