using Csms.Domain;

namespace Csms.Application;

/// <summary>
/// Результат сборки документа.
/// <paramref name="SchemaVersion"/> нужен, чтобы отличить настоящий формат ЕИС
/// от чернового: пока схемы нет, версия начинается с "placeholder" и это
/// видно в API.
/// </summary>
public sealed record XmlBuildResult(string Xml, string SchemaVersion)
{
    public bool IsPlaceholder =>
        SchemaVersion.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Сборка XML по формату ЕИС.
///
/// Реальный формат задаётся XSD из Альбома форматов ЕИС. Пока схем нет,
/// реализация возвращает честную заготовку с пометкой внутри XML, а не
/// документ, который выглядит настоящим.
/// </summary>
public interface IEisDocumentXmlBuilder
{
    XmlBuildResult Build(EisDocumentType documentType, string contractEisNumber, string ikz);
}
