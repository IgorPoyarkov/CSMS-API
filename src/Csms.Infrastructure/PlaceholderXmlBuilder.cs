using System.Text;
using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Временная сборка документа.
///
/// Настоящий формат задаётся XSD из Альбома форматов ЕИС, которых у нас пока
/// нет. Без схемы любой «правильный на вид» XML будет отклонён ФЛК, поэтому
/// результат честно помечен: и комментарием внутри документа, и версией схемы
/// <c>placeholder-v0</c>, которая видна в ответе API.
///
/// Когда появятся XSD, меняется только этот класс: генерация по схеме,
/// осмысленный <c>SchemaVersion</c> (например <c>14.2</c>), настоящие поля.
/// Остальной код — статусы, подпись, транспорт — не трогается.
/// </summary>
public sealed class PlaceholderXmlBuilder : IEisDocumentXmlBuilder
{
    public const string PlaceholderVersion = "placeholder-v0";

    public XmlBuildResult Build(EisDocumentType documentType, string contractEisNumber, string ikz)
    {
        var xml = new StringBuilder();

        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<!-- ЧЕРНОВИК: формат ЕИС задаётся XSD из Альбома форматов. Пока схемы нет, этот документ не пройдёт ФЛК и не должен считаться файлом обмена. -->");
        xml.AppendLine($"<eisDraft schemaVersion=\"{PlaceholderVersion}\" documentType=\"{documentType}\">");
        xml.AppendLine($"  <contractEisNumber>{Escape(contractEisNumber)}</contractEisNumber>");
        xml.AppendLine($"  <ikz>{Escape(ikz)}</ikz>");
        xml.AppendLine("</eisDraft>");

        return new XmlBuildResult(xml.ToString(), PlaceholderVersion);
    }

    private static string Escape(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
