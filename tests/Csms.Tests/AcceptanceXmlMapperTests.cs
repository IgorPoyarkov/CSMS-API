using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Маппер приёмки проверяется не «на глаз», а тем же XSD-каталогом, который
/// используется шлюзом: XML обязан проходить валидацию Альбома 17.0.
/// </summary>
public sealed class AcceptanceXmlMapperTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_CreatesXsdValidPacket()
    {
        var acceptance = SignedAcceptance();
        var context = ValidContext();

        var result = new AcceptanceXmlMapper().Build(acceptance, context);

        Assert.Equal("17.0", result.SchemaVersion);
        Assert.False(result.IsPlaceholder);
        Assert.Contains(AcceptanceXmlMapper.RootElementName, result.Xml, StringComparison.Ordinal);
        Assert.Contains("docAcceptanceInfo", result.Xml, StringComparison.Ordinal);
        Assert.Empty(CreateCatalog().Validate(result.Xml, "17.0"));
    }

    [Fact]
    public void Build_WritesExplicitReferenceCodesWithoutGuessing()
    {
        var result = new AcceptanceXmlMapper().Build(SignedAcceptance(), ValidContext());

        // Коды пришли из контекста: маппер не подбирает их по наименованию.
        Assert.Contains("<base:code>1</base:code>", result.Xml, StringComparison.Ordinal);
        Assert.Contains("<base:VATCode>20</base:VATCode>", result.Xml, StringComparison.Ordinal);
        Assert.Contains("<cmn:regNum>12345678901</cmn:regNum>", result.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RequiresDocumentTypeCodeFromReference()
    {
        var context = ValidContext() with { DocumentTypeCode = "  " };

        var error = Assert.Throws<DomainException>(
            () => new AcceptanceXmlMapper().Build(SignedAcceptance(), context));

        Assert.Contains("documentTypeCode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RequiresContractRegistryNumber()
    {
        var acceptance = SignedAcceptance(eisRegistryNumber: null);

        var error = Assert.Throws<DomainException>(
            () => new AcceptanceXmlMapper().Build(acceptance, ValidContext()));

        Assert.Contains("eisRegistryNumber", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RequiresAtLeastOnePosition()
    {
        var acceptance = ContractAcceptance.Create(AcceptanceInput(), Now);

        var error = Assert.Throws<DomainException>(
            () => new AcceptanceXmlMapper().Build(acceptance, ValidContext()));

        Assert.Contains("позиций", error.Message, StringComparison.Ordinal);
    }

    private static ContractAcceptance SignedAcceptance(string? eisRegistryNumber = "0123456789012345678")
    {
        var acceptance = ContractAcceptance.Create(
            AcceptanceInput(eisRegistryNumber), Now);

        acceptance.AddPosition(
            "Принтер", "26.20.15.000", "шт", 2m, 45000m, 20m, "643", null,
            "supplier", Now.AddMinutes(1));
        acceptance.Submit("supplier", Now.AddMinutes(2));
        acceptance.Accept(AcceptanceResult.Accepted, "customer", null, Now.AddMinutes(3));

        return acceptance;
    }

    private static AcceptanceInput AcceptanceInput(string? eisRegistryNumber = "0123456789012345678") =>
        new(
            OrganizationId: "org-1",
            ContractId: "contract-1",
            ContractNumber: "01234567890123456789012",
            ContractName: "Поставка оргтехники",
            ContractPrice: 100000m,
            ContractStatus: "in_execution",
            ContractDate: new DateOnly(2026, 1, 10),
            CustomerName: "Государственный заказчик",
            SupplierName: "ООО «Поставщик»",
            SupplierInn: "7700000000",
            Ikz: "01234567890123456789012345678901",
            EisRegistryNumber: eisRegistryNumber,
            StageNumber: 1,
            StageName: "Первый этап",
            Kind: AcceptanceKind.Primary,
            DocumentNumber: "АКТ-1",
            DocumentDate: new DateOnly(2026, 11, 15),
            CorrectedDocumentNumber: null,
            CorrectedDocumentDate: null,
            CorrectionReason: null,
            AcceptanceTermDays: 10,
            ExpertiseRequired: false,
            ExpertiseByThirdParty: false,
            ExpertiseOrganization: null,
            ExpertiseConclusionNumber: null,
            ExpertiseConclusionDate: null,
            CommissionRequired: false,
            CommissionDecisionNumber: null,
            CommissionDecisionDate: null,
            DeliveryPlace: "Москва",
            Notes: null,
            CreatedBy: "supplier");

    private static AcceptanceXmlContext ValidContext() =>
        new(
            DocumentTypeCode: "1",
            Sender: "CSMS",
            Receiver: "EIS",
            CreatedAt: Now.AddMinutes(4),
            PacketId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Mode: "TEST",
            CustomerSpzRegNum: "12345678901",
            SignatureBase64: null,
            VatCodes: new Dictionary<decimal, string> { [20m] = "20" });

    private static EisSchemaCatalog CreateCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "schemas", "17.0", "fcsIntegration.xsd")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Не найден каталог schemas/17.0 с XSD ЕИС.");

        return new EisSchemaCatalog(Path.Combine(directory!.FullName, "schemas"));
    }
}
