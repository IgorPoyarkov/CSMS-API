namespace Csms.Infrastructure;

/// <summary>Снимок состояния автообновления схем — то, что видит API.</summary>
public sealed record EisSchemaStatusSnapshot(
    string State,
    string? Version,
    string? SourceId,
    string? Sha256,
    int FileCount,
    DateTimeOffset? LastCheckAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    string Source);

/// <summary>
/// Хранилище последнего результата проверки. Обновляется фоновой службой,
/// читается endpoint-ом — без блокировок на время сетевого запроса.
/// </summary>
public sealed class EisSchemaUpdateStatus
{
    private readonly object _gate = new();
    private EisSchemaStatusSnapshot _current = new(
        State: "Starting",
        Version: null,
        SourceId: null,
        Sha256: null,
        FileCount: 0,
        LastCheckAt: null,
        LastSuccessAt: null,
        LastError: null,
        Source: "zakupki.gov.ru");

    public EisSchemaStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public void Set(EisSchemaStatusSnapshot value)
    {
        lock (_gate)
        {
            _current = value;
        }
    }
}
