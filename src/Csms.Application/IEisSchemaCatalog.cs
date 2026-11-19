namespace Csms.Application;

/// <summary>
/// Каталог XSD-схем ЕИС по версиям Альбома форматов.
///
/// Схемы лежат вне кода: папка <c>schemas/&lt;версия&gt;/*.xsd</c>. Бизнес-логика
/// не знает, как устроены конкретные схемы, и не зависит от сгенерированных
/// классов — это позволяет менять формат ЕИС, не переписывая сценарии пакетов.
/// </summary>
public interface IEisSchemaCatalog
{
    /// <summary>Есть ли для версии хоть одна загруженная схема.</summary>
    bool HasSchema(string schemaVersion);

    /// <summary>
    /// Проверить XML по схемам версии. Пустой список означает «ошибок нет»
    /// только если <see cref="HasSchema"/> вернул <c>true</c>: когда схем нет,
    /// проверяющему нечего сказать, и молчание нельзя принимать за успех.
    /// </summary>
    IReadOnlyList<string> Validate(string xml, string schemaVersion);
}
