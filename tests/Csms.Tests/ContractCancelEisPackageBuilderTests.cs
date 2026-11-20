using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>Выгрузка расторжения: только подписанный документ и только XSD-валидный XML.</summary>
public sealed class ContractCancelEisPackageBuilderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_SavesValidatedCancelPackage()
    {
        var (builder, changes, packages) = CreateBuilder();
        var change = SignedTermination();
        await changes.AddAsync(change, CancellationToken.None);

        var package = await builder.CreateAsync(change.Id, new ContractCancelPackageInput(), CancellationToken.None);

        Assert.Equal(EisDocumentType.ContractExecution, package.DocumentType);
        Assert.Equal("17.0", package.PayloadSchemaVersion);
        Assert.Equal(change.Id.ToString("D"), package.Source!.DocumentId);
        Assert.Single(await packages.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsDraft_WithoutCreatingPackage()
    {
        var (builder, changes, packages) = CreateBuilder();
        var change = ContractChange.Create(ContractChangeSamples.Termination(), Now);
        await changes.AddAsync(change, CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            builder.CreateAsync(change.Id, new ContractCancelPackageInput(), CancellationToken.None));

        Assert.Contains("подписанное расторжение", error.Message, StringComparison.Ordinal);
        Assert.Empty(await packages.ListAsync(CancellationToken.None));
    }

    private static (
        ContractCancelEisPackageBuilder Builder,
        IContractChangeRepository Changes,
        IEisPackageRepository Packages) CreateBuilder()
    {
        IContractChangeRepository changes = new InMemoryContractChangeRepository();
        IEisPackageRepository packages = new InMemoryEisPackageRepository();
        var catalog = CreateCatalog();
        var clock = new FixedTimeProvider(Now);
        var packageService = new EisPackageService(
            packages,
            new MockEisTransport(),
            new PlaceholderXmlBuilder(),
            catalog,
            clock);

        var builder = new ContractCancelEisPackageBuilder(
            changes,
            new ContractCancelXmlMapper(),
            packageService,
            catalog,
            clock);

        return (builder, changes, packages);
    }

    private static ContractChange SignedTermination()
    {
        var change = ContractChange.Create(ContractChangeSamples.Termination(), Now);
        change.AddDocument(
            ContractChangeDocumentType.Agreement,
            "Соглашение о расторжении",
            "Стороны согласовали прекращение обязательств.",
            "supplier",
            Now);
        change.Submit("supplier", Now.AddMinutes(1));
        change.Approve("customer", Now.AddMinutes(2));
        change.Sign("customer", Now.AddMinutes(3));
        return change;
    }

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
