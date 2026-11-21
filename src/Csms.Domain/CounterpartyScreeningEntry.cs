namespace Csms.Domain;

/// <summary>
/// Строка публичной выгрузки ЕИС о привлечении участника закупки к
/// административной ответственности по ст. 19.28 КоАП. Даты берутся из
/// файла; ИНН, ОГРН и КПП хранятся строками — встречаются аналоги ИНН.
/// </summary>
public sealed record CounterpartyScreeningEntry(
    string Inn,
    string Ogrn,
    string Kpp,
    string Name,
    string ParticipantType,
    string Court,
    string CaseNumber,
    DateOnly DecisionDate,
    DateOnly EffectiveDate)
{
    /// <summary>Признак одной и той же записи при объединении поиска по разным реквизитам.</summary>
    public string Key => string.Join("|", Inn, Ogrn, CaseNumber, DecisionDate.ToString("yyyy-MM-dd"));
}
