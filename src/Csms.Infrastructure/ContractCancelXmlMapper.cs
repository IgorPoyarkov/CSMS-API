using System.Globalization;
using System.Xml.Linq;
using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>Параметры оболочки пакета аннулирования контракта (Б10).</summary>
public sealed record ContractCancelXmlContext(
    string Sender,
    string Receiver,
    DateTimeOffset CreatedAt,
    Guid PacketId,
    string Mode = "TEST",
    string? SignatureBase64 = null,
    string SignatureType = "CAdES-BES");

/// <summary>
/// Маппер аннулирования контракта в пакет ЕИС 17.0 — корень
/// <c>contractCancel</c> с данными <c>zfcs_contractCancel2015Type</c>
/// (<c>fcsIntegration.xsd:730</c>).
///
/// Как и в других мапперах, коды и реквизиты не подбираются «похожими»:
/// реестровый номер контракта обязан прийти из домена, а дата аннулирования —
/// из документа. Отсутствие обязательного значения — <see cref="DomainException"/>.
/// </summary>
public sealed class ContractCancelXmlMapper
{
    public const string SchemaVersion = "17.0";
    public const string RootElementName = "contractCancel";

    private const string ObjectTypeContract = "CT";
    private const string StageAnnuled = "IN";

    private static readonly XNamespace Integration =
        "http://zakupki.gov.ru/oos/integration/1";

    private static readonly XNamespace Oos =
        "http://zakupki.gov.ru/oos/types/1";

    private static readonly XNamespace Base =
        "http://zakupki.gov.ru/oos/base/1";

    /// <summary>Собрать XML аннулирования по документу об изменении/расторжении.</summary>
    public XmlBuildResult Build(ContractChange change, ContractCancelXmlContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);

        if (change.Input.Kind != ContractChangeKind.Termination)
        {
            throw new DomainException(
                "Пакет contractCancel собирается только по расторжению контракта.");
        }

        var regNumber = EisRegistryNumber.Normalize(change.Input.EisRegistryNumber);
        if (!EisRegistryNumber.IsValid(regNumber))
        {
            throw new DomainException(
                $"Реестровый номер контракта должен содержать {EisRegistryNumber.Length} цифр, " +
                "иначе пакет contractCancel нельзя привязать к контракту в ЕИС.");
        }

        if (string.IsNullOrWhiteSpace(context.Sender))
        {
            throw new DomainException("Не заполнен Sender пакета contractCancel.");
        }

        if (string.IsNullOrWhiteSpace(context.Receiver))
        {
            throw new DomainException("Не заполнен Receiver пакета contractCancel.");
        }

        var cancelDate = change.Input.TerminationDate ?? change.Input.ChangeDate;
        var documentBase = BuildDocumentBase(change);

        var index = new XElement(Integration + "index",
            Element("id", context.PacketId.ToString("D")),
            Element("sender", context.Sender.Trim()),
            Element("receiver", context.Receiver.Trim()),
            Element("createDateTime", context.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            Element("objectType", ObjectTypeContract),
            Element("objectId", regNumber),
            new XElement(Integration + "signature",
                new XAttribute("type", context.SignatureType),
                context.SignatureBase64 ?? string.Empty),
            Element("mode", context.Mode));

        var data = new XElement(
            Integration + "data",
            new XAttribute("schemeVersion", SchemaVersion),
            OosElement("regNum", regNumber),
            OosElement("cancelDate", new DateTimeOffset(
                cancelDate, TimeOnly.MinValue, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)),
            OosElement("documentBase", documentBase),
            OosElement("currentContractStage", StageAnnuled));

        var root = new XElement(
            Integration + RootElementName,
            new XAttribute(XNamespace.Xmlns + "oos", Oos.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "base", Base.NamespaceName),
            index,
            data);

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.Save(writer, SaveOptions.DisableFormatting);

        return new XmlBuildResult(writer.ToString(), SchemaVersion);
    }

    /// <summary>Реквизиты документа-основания: причина и приложенные документы.</summary>
    private static string BuildDocumentBase(ContractChange change)
    {
        var reason = (change.Input.Reason ?? string.Empty).Trim();
        var documents = change.Documents
            .Where(document => !string.IsNullOrWhiteSpace(document.Title))
            .Select(document => document.Title.Trim());

        var text = string.Join("; ", new[] { reason }.Concat(documents)
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        if (text.Length == 0)
        {
            throw new DomainException(
                "Для пакета contractCancel заполните reason или приложите документ-основание.");
        }

        if (text.Length > 2000)
        {
            throw new DomainException(
                "Реквизиты документа-основания пакета contractCancel длиннее 2000 символов.");
        }

        return text;
    }

    private static XElement Element(string name, string value) => new(Integration + name, value);

    private static XElement OosElement(string name, string value) => new(Oos + name, value);
}
