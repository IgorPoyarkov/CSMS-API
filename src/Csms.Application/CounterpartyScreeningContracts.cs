using Csms.Domain;

namespace Csms.Application;

/// <summary>Источник, на котором выполнена проверка контрагента.</summary>
public sealed record CounterpartyScreeningSource(
    string? SourceId,
    string? PublishedDate,
    string? Sha256,
    int RowCount,
    DateTimeOffset? InstalledAt);

/// <summary>Результат поиска контрагента в публичной выгрузке.</summary>
public sealed record CounterpartyScreeningResult(
    bool IsListed,
    IReadOnlyList<CounterpartyScreeningEntry> Matches,
    CounterpartyScreeningSource? Source,
    DateTimeOffset CheckedAt);

/// <summary>
/// Индекс публичной выгрузки в памяти процесса. Обновляется загрузчиком
/// только после успешной проверки и установки нового файла.
/// </summary>
public interface ICounterpartyScreeningCatalog
{
    CounterpartyScreeningSource? Source { get; }

    void Replace(
        CounterpartyScreeningSource source,
        IReadOnlyList<CounterpartyScreeningEntry> entries);

    CounterpartyScreeningResult Check(string? inn, string? ogrn, string? kpp);
}
