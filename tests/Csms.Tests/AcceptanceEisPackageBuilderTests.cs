using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Сборка пакета приёмки (Б7) проверяется на уровне сервиса: валидный документ
/// даёт XSD-валидный пакет в хранилище, а невалидный не оставляет мусора.
/// </summary>
public sealed class AcceptanceEisPackageBuilderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_CreatesValidatedPackageFromAcceptedAcceptance()
    {
        var (builder, acceptances, packages) = CreateBuilder();
        var acceptance = SignedAcceptance();
        await acceptances.AddAsync(acceptance, CancellationToken.None);

        var package = await builder.CreateAsync(acceptance.Id, ValidInput(), CancellationToken.None);

        Assert.Equal(EisDocumentType.ContractExecution, package.DocumentType);
        Assert.Equal("0123456789012345678", package.ContractEisNumber);
        Assert.Equal("17.0", package.PayloadSchemaVersion);
        Assert.Equal(acceptance.Id.ToString("D"), package.Source!.DocumentId);
        Assert.Equal("0123456789012345678", package.ContractEisNumber);
        Assert.Single(await packages.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsDraftAcceptance_WithoutCreatingPackage()
    {
        var (builder, acceptances, packages) = CreateBuilder();
        var acceptance = ContractAcceptance.Create(AcceptanceInput(), Now);
        await acceptances.AddAsync(acceptance, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            builder.CreateAsync(acceptance.Id, ValidInput(), CancellationToken.None));

        Assert.Contains("принятый документ", error.Message, StringComparison.Ordinal);
        Assert.Empty(await packages.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsRequestWithoutReferenceCodes()
    {
        var (builder, acceptances, packages) = CreateBuilder();
        var acceptance = SignedAcceptance();
        await acceptances.AddAsync(acceptance, CancellationToken.None);
        var input = ValidInput() with { DocumentTypeCode = string.Empty };

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            builder.CreateAsync(acceptance.Id, input, CancellationToken.None));

        Assert.Contains("documentTypeCode", error.Message, StringComparison.Ordinal);
        Assert.Empty(await packages.ListAsync(CancellationToken.None));
    }

    private static (
        AcceptanceEisPackageBuilder Builder,
        IAcceptanceRepository Acceptances,
        IEisPackageRepository Packages) CreateBuilder()
    {
        IAcceptanceRepository acceptances = new InMemoryAcceptanceRepository();
        IEisPackageRepository packages = new InMemoryEisPackageRepository();
        var transport = new MockEisTransport();
        var packageService = new EisPackageService(
            packages,
            transport,
            new PlaceholderXmlBuilder(),
            SchemaCatalog(),
            new FixedTimeProvider(Now));

        var builder = new AcceptanceEisPackageBuilder(
            acceptances,
            new AcceptanceXmlMapper(),
            packageService,
            SchemaCatalog(),
            new FixedTimeProvider(Now));

        return (builder, acceptances, packages);
    }

    private static EisSchemaCatalog SchemaCatalog()
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

    private static ContractAcceptance SignedAcceptance()
    {
        var acceptance = ContractAcceptance.Create(AcceptanceInput(), Now);
        acceptance.AddPosition(
            "Принтер", "26.20.15.000", "шт", 2m, 45000m, 20m, "643", null,
            "supplier", Now.AddMinutes(1));
        acceptance.Submit("supplier", Now.AddMinutes(2));
        acceptance.Accept(AcceptanceResult.Accepted, "customer", null, Now.AddMinutes(3));
        return acceptance;
    }

    private static AcceptanceInput AcceptanceInput() =>
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
            EisRegistryNumber: "0123456789012345678",
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

    private static AcceptancePackageInput ValidInput() =>
        new(
            DocumentTypeCode: "1",
            CustomerSpzRegNum: "12345678901",
            VatCodes: new Dictionary<decimal, string> { [20m] = "20" });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
