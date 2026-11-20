using System.Globalization;
using System.Xml.Linq;
using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Параметры пакета, которых нет в домене приёмки.
///
/// Код типа документа, код СПЗ заказчика и коды НДС приходят из справочников
/// ЕИС. В домене их нет, а подбирать «похожие» значения запрещено: ФЛК ЕИС
/// всё равно отклонит выдуманный код. Поэтому отсутствующие значения либо
/// явно передаются вызывающим кодом, либо поле не заполняется.
/// </summary>
public sealed record AcceptanceXmlContext(
    string DocumentTypeCode,
    string Sender,
    string Receiver,
    DateTimeOffset CreatedAt,
    Guid PacketId,
    string Mode = "TEST",
    string? CustomerSpzRegNum = null,
    string? SignatureBase64 = null,
    string SignatureType = "CAdES-BES",
    IReadOnlyDictionary<decimal, string>? VatCodes = null,
    DateOnly? DeliveryAcceptDateOverride = null);

/// <summary>
/// Маппер документа о приёмке (ст. 94 44-ФЗ) в пакет ЕИС 17.0.
///
/// Корень — <c>ceContractExecutionDocAcceptance</c>: глобальный элемент
/// «Сведения об исполнении контракта по документу о приемке (ЦРК), большая
/// закупка» (<c>fcsIntegration.xsd:6346</c>). Внутри него заполняется
/// <c>data/docAcceptanceInfo</c> и перечень позиций.
///
/// Маппер не меняет домен и не подменяет неизвестные справочники догадками.
/// Если обязательного по XSD значения нет — бросает <see cref="DomainException"/>
/// с названием поля, а не собирает «похожий на правильный» XML.
/// </summary>
public sealed class AcceptanceXmlMapper
{
    /// <summary>Версия Альбома, по которой собран XML.</summary>
    public const string SchemaVersion = "17.0";

    /// <summary>Глобальный корневой элемент интеграционного пакета.</summary>
    public const string RootElementName = "ceContractExecutionDocAcceptance";

    private const string ObjectTypeCrk = "CRK";
    private const string ResponsibleRoleCustomer = "CU";

    private static readonly XNamespace Integration =
        "http://zakupki.gov.ru/oos/integration/1";

    private static readonly XNamespace Ce =
        "http://zakupki.gov.ru/oos/CETypes/1";

    private static readonly XNamespace Base =
        "http://zakupki.gov.ru/oos/base/1";

    private static readonly XNamespace Common =
        "http://zakupki.gov.ru/oos/common/1";

    /// <summary>Собрать пакет по документу о приёмке.</summary>
    public XmlBuildResult Build(ContractAcceptance acceptance, AcceptanceXmlContext context)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(context);

        ValidateContext(context);
        ValidateAcceptance(acceptance);

        var index = BuildIndex(acceptance, context);
        var data = BuildData(acceptance, context);

        var root = new XElement(
            Integration + RootElementName,
            new XAttribute(XNamespace.Xmlns + "ce", Ce.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "base", Base.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cmn", Common.NamespaceName),
            index,
            data);

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        document.Save(writer, SaveOptions.DisableFormatting);

        return new XmlBuildResult(writer.ToString(), SchemaVersion);
    }

    private static XElement BuildIndex(ContractAcceptance acceptance, AcceptanceXmlContext context)
    {
        var index = new XElement(Integration + "index",
            Element("id", context.PacketId.ToString("D")),
            Element("sender", context.Sender.Trim()),
            Element("receiver", context.Receiver.Trim()),
            Element("createDateTime", context.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            Element("objectType", ObjectTypeCrk),
            Element("objectId", acceptance.Input.EisRegistryNumber!),
            new XElement(Integration + "signature",
                new XAttribute("type", context.SignatureType),
                context.SignatureBase64 ?? string.Empty),
            Element("mode", context.Mode));

        return index;
    }

    private static XElement BuildData(ContractAcceptance acceptance, AcceptanceXmlContext context)
    {
        // data — элемент самого пакета (integration/1), а не документа:
        // его дети уже живут в CETypes. schemeVersion обязателен у data.
        var data = new XElement(
            Integration + "data",
            new XAttribute("schemeVersion", SchemaVersion));

        data.Add(CeElement("externalId", acceptance.Id.ToString("D")));
        data.Add(CeElement("regNumber", acceptance.Input.EisRegistryNumber!));

        if (!string.IsNullOrWhiteSpace(context.CustomerSpzRegNum))
        {
            data.Add(new XElement(Ce + "placer",
                new XElement(Ce + "responsibleOrg",
                    new XElement(Common + "regNum", context.CustomerSpzRegNum!.Trim())),
                CeElement("responsibleRole", ResponsibleRoleCustomer)));
        }

        data.Add(BuildAcceptanceInfo(acceptance, context));

        if (acceptance.Input.Kind == AcceptanceKind.Correction &&
            !string.IsNullOrWhiteSpace(acceptance.Input.CorrectionReason))
        {
            data.Add(new XElement(Ce + "modificationReasonInfo",
                CeElement(
                    "modificationReason",
                    RequireText(acceptance.Input.CorrectionReason, 4000, "correctionReason"))));
        }

        return data;
    }

    private static XElement BuildAcceptanceInfo(
        ContractAcceptance acceptance,
        AcceptanceXmlContext context)
    {
        var input = acceptance.Input;

        var info = new XElement(Ce + "docAcceptanceInfo",
            new XElement(Ce + "documentType",
                BaseElement("code", context.DocumentTypeCode.Trim())),
            CeElement("documentDate", input.DocumentDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CeElement("documentNum", RequireText(input.DocumentNumber, 100, "documentNumber")),
            CeElement("deliveryAcceptDate", DeliveryDate(acceptance, context).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            CeElement("fulfilmentSumRUR", Money(acceptance.TotalWithVat)));

        var performanceInfo = PerformanceInfo(acceptance);
        if (!string.IsNullOrEmpty(performanceInfo))
        {
            info.Add(CeElement("defaultPerformanceInfo", performanceInfo));
        }

        // defaultPerformanceInfo и isCalculateVATTotals идут до productsInfo:
        // порядок детей дефинируется sequence в XSD, а не порядком вызовов.
        info.Add(CeElement("isCalculateVATTotals", "true"));

        var productsInfo = new XElement(Ce + "productsInfo");
        var contractInfo = new XElement(Ce + "productsContractInfo");

        foreach (var position in acceptance.Positions)
        {
            contractInfo.Add(BuildProduct(position, context));
        }

        productsInfo.Add(contractInfo);
        info.Add(productsInfo);

        return info;
    }

    private static XElement BuildProduct(AcceptancePosition position, AcceptanceXmlContext context)
    {
        var product = new XElement(Ce + "productInfo",
            CeElement("name", RequireText(position.Name, 2000, "position.name")));

        // OKPD2Info в XSD стоит до количества — sequence нельзя переставлять.
        if (!string.IsNullOrWhiteSpace(position.Okpd2))
        {
            product.Add(new XElement(Ce + "OKPD2Info",
                BaseElement("OKPDCode", RequireText(position.Okpd2, 12, "position.okpd2"))));
        }

        product.Add(CeElement("quantity", Decimal12(position.Quantity, 18, 11, "position.quantity")));
        product.Add(CeElement("unitPrice", Decimal12(position.UnitPrice, 18, 11, "position.unitPrice")));

        if (context.VatCodes is not null &&
            context.VatCodes.TryGetValue(position.VatRate, out var vatCode) &&
            !string.IsNullOrWhiteSpace(vatCode))
        {
            product.Add(new XElement(Ce + "VATRateInfo",
                BaseElement("VATCode", RequireText(vatCode, 10, "vatCode"))));
        }

        // В домене страна происхождения хранится как код. Подставляем его
        // только если он цифровой и укладывается в countryCodeType (1..3).
        if (!string.IsNullOrWhiteSpace(position.CountryOrigin) &&
            position.CountryOrigin.All(char.IsDigit) &&
            position.CountryOrigin.Length is >= 1 and <= 3)
        {
            product.Add(new XElement(Ce + "originCountry",
                BaseElement("countryCode", position.CountryOrigin.Trim())));
        }

        product.Add(CeElement("fulfilledCost", Money(position.TotalWithVat)));

        return product;
    }

    private static DateOnly DeliveryDate(ContractAcceptance acceptance, AcceptanceXmlContext context)
    {
        if (context.DeliveryAcceptDateOverride is not null)
        {
            return context.DeliveryAcceptDateOverride.Value;
        }

        // Дата подписания заказчиком: у принятого документа — ReviewedAt,
        // у зарегистрированного — RegisteredAt. У поставщика SignedAt —
        // это подпись поставщика, для поля заказчика она идёт последней.
        var date = acceptance.ReviewedAt ?? acceptance.RegisteredAt ?? acceptance.SignedAt;

        return date is not null
            ? DateOnly.FromDateTime(date.Value.Date)
            : throw new DomainException(
                "Нет даты рассмотрения документа заказчиком: «deliveryAcceptDate» обязателен для выгрузки в ЕИС.");
    }

    private static string PerformanceInfo(ContractAcceptance acceptance)
    {
        var parts = new List<string>();

        foreach (var defect in acceptance.Defects)
        {
            parts.Add($"{defect.Description} ({defect.Requirement.Ru()})");
        }

        if (!string.IsNullOrWhiteSpace(acceptance.RefusalReason))
        {
            parts.Add(acceptance.RefusalReason.Trim());
        }

        if (!string.IsNullOrWhiteSpace(acceptance.ReturnComment))
        {
            parts.Add(acceptance.ReturnComment.Trim());
        }

        if (!string.IsNullOrWhiteSpace(acceptance.Input.Notes))
        {
            parts.Add(acceptance.Input.Notes.Trim());
        }

        return parts.Count == 0
            ? string.Empty
            : RequireText(string.Join("; ", parts), 2000, "defaultPerformanceInfo");
    }

    private static void ValidateContext(AcceptanceXmlContext context)
    {
        RequireText(context.DocumentTypeCode, 4, "documentTypeCode");
        RequireText(context.Sender, 200, "sender");
        RequireText(context.Receiver, 200, "receiver");

        if (context.PacketId == Guid.Empty)
        {
            throw new DomainException("Не задан packetId: индекс пакета ЕИС требует GUID из 36 символов.");
        }

        if (context.Mode is not ("PROD" or "TEST"))
        {
            throw new DomainException("mode пакета ЕИС должен быть PROD или TEST.");
        }

        if (context.SignatureType is not ("CAdES-BES" or "CAdES-A"))
        {
            throw new DomainException("Неизвестный тип подписи ЕИС: ожидается CAdES-BES или CAdES-A.");
        }

        if (!string.IsNullOrWhiteSpace(context.CustomerSpzRegNum) &&
            (context.CustomerSpzRegNum!.Trim().Length != 11 ||
             !context.CustomerSpzRegNum.Trim().All(char.IsDigit)))
        {
            throw new DomainException("Код заказчика по СПЗ должен содержать ровно 11 цифр.");
        }

        if (!string.IsNullOrEmpty(context.SignatureBase64))
        {
            try
            {
                _ = Convert.FromBase64String(context.SignatureBase64);
            }
            catch (FormatException)
            {
                throw new DomainException("signatureBase64 не является корректной base64-строкой.");
            }
        }

        if (context.VatCodes is not null)
        {
            foreach (var (rate, code) in context.VatCodes)
            {
                if (rate < 0 || rate > 100)
                {
                    throw new DomainException($"Неверная ставка НДС {rate} в справочнике кодов ЕИС.");
                }

                RequireText(code, 10, $"vatCode для ставки {rate}");
            }
        }
    }

    private static void ValidateAcceptance(ContractAcceptance acceptance)
    {
        var input = acceptance.Input;

        var regNumber = input.EisRegistryNumber?.Trim();
        if (regNumber is null || regNumber.Length != 19 || !regNumber.All(char.IsDigit))
        {
            throw new DomainException(
                "У документа о приёмке нет корректного реестрового номера контракта из ЕИС " +
                "(eisRegistryNumber, 19 цифр): поле regNumber обязательно в пакете.");
        }

        if (input.DocumentDate is null)
        {
            throw new DomainException(
                "Не указана дата документа о приёмке: поле documentDate обязательно в пакете ЕИС.");
        }

        RequireText(input.DocumentNumber, 100, "documentNumber");

        if (acceptance.Positions.Count == 0)
        {
            throw new DomainException(
                "В документе о приёмке нет позиций: пакет ЕИС без productsInfo не имеет смысла.");
        }
    }

    private static string RequireText(string? value, int maxLength, string field)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            throw new DomainException($"Не заполнено поле «{field}» — оно обязательно для XML ЕИС.");
        }

        if (text.Length > maxLength)
        {
            throw new DomainException(
                $"Поле «{field}» длиннее {maxLength} символов, разрешённых XSD ЕИС.");
        }

        return text;
    }

    private static string Decimal12(decimal value, int integerDigits, int fractionDigits, string field)
    {
        if (value < 0)
        {
            throw new DomainException($"Поле «{field}» не может быть отрицательным в пакете ЕИС.");
        }

        var rounded = decimal.Round(value, fractionDigits, MidpointRounding.AwayFromZero);
        var format = fractionDigits == 0
            ? "0"
            : "0." + new string('#', fractionDigits);
        var text = rounded.ToString(format, CultureInfo.InvariantCulture);
        var integerPart = text.Split('.')[0];

        if (integerPart.Length > integerDigits)
        {
            throw new DomainException(
                $"Поле «{field}» не укладывается в {integerDigits} цифр целой части XSD ЕИС.");
        }

        return text;
    }

    private static string Money(decimal value) => Decimal12(value, 20, 2, "fulfilledCost");

    private static XElement Element(string name, string value) => new(Integration + name, value);

    private static XElement CeElement(string name, string value) => new(Ce + name, value);

    private static XElement BaseElement(string name, string value) => new(Base + name, value);
}
