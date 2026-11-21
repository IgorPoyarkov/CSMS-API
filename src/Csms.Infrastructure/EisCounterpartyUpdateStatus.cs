namespace Csms.Infrastructure;

/// <summary>Снимок состояния ежедневной проверки — то, что видит API.</summary>
public sealed record EisCounterpartyStatusSnapshot(
    string State,
    string? SourceId,
    string? PublishedDate,
    string? Sha256,
    int RowCount,
    DateTimeOffset? LastCheckAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    string Source,
    bool ParsedNow);

/// <summary>
/// Последний результат проверки. Пишется фоновой службой, читается endpoint-ом
/// без ожидания сети.
/// </summary>
public sealed class EisCounterpartyUpdateStatus
{
    private readonly object _gate = new();
    private EisCounterpartyStatusSnapshot _current = new(
        State: "Starting",
        SourceId: null,
        PublishedDate: null,
        Sha256: null,
        RowCount: 0,
        LastCheckAt: null,
        LastSuccessAt: null,
        LastError: null,
        Source: "zakupki.gov.ru",
        ParsedNow: false);

    public EisCounterpartyStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public void Set(EisCounterpartyStatusSnapshot value)
    {
        lock (_gate)
        {
            _current = value;
        }
    }
}
