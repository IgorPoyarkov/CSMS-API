namespace Csms.Domain;

/// <summary>
/// Вид документа CSMS, из которого собран пакет ЕИС.
///
/// Перечисление Tasks/СЗ/извещений фиксирует источник на уровне домена, чтобы
/// карточку пакета нельзя было «привязать» к произвольной строке из запроса.
/// </summary>
public enum EisSourceDocumentKind
{
    Notice,
    ContractProject,
    Contract,
    SmpReport,
    ContractExecution,
}

/// <summary>
/// Ссылка на документ-источник: по ней пакет можно найти и пересобрать без
/// человека. Заполняется фронтом при создании, после этого не меняется.
/// </summary>
public sealed record EisPackageSource(
    EisSourceDocumentKind DocumentKind,
    string DocumentId,
    string? DocumentNumber = null);

/// <summary>
/// Снимок реквизитов заказчика на момент создания пакета.
///
/// Карточку организации правят, а подпись ставилась под конкретным набором
/// реквизитов: снимок объясняет расхождение с текущей карточкой. Все поля
/// опциональны — фронт передаёт только то, что есть в его форме.
/// </summary>
public sealed record EisCustomerSnapshot(
    string? FullName = null,
    string? ShortName = null,
    string? Inn = null,
    string? Kpp = null,
    string? Okopf = null,
    string? Okfs = null,
    string? Oktmo = null,
    string? OktmoPpo = null,
    string? RegNumber = null);
