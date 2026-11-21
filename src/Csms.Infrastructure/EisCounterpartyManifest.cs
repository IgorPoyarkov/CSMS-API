using Csms.Application;

namespace Csms.Infrastructure;

/// <summary>Манифест установленной выгрузки — лежит рядом с current/document.xlsx.</summary>
public sealed record EisCounterpartyManifest(
    string SourceId,
    string PublishedDate,
    string Sha256,
    long ByteLength,
    int RowCount,
    DateTimeOffset InstalledAt,
    string SourceUrl)
{
    public CounterpartyScreeningSource ToSource() =>
        new(SourceId, PublishedDate, Sha256, RowCount, InstalledAt);
}
