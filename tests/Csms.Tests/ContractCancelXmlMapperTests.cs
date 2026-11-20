using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Маппер аннулирования контракта (Б10) проверяется тем же XSD-каталогом,
/// что и остальные пакеты: «похожий на правильный» XML здесь не принимается.
/// </summary>
public sealed class ContractCancelXmlMapperTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_CreatesXsdValidContractCancelPacket()
    {
        var change = ContractChange.Create(ContractChangeSamples.Termination(), Now);

        var result = new ContractCancelXmlMapper().Build(change, ValidContext());

        Assert.Equal("17.0", result.SchemaVersion);
        Assert.Contains("<contractCancel", result.Xml, StringComparison.Ordinal);
        Assert.Contains("cancelDate", result.Xml, StringComparison.Ordinal);
        Assert.Empty(CreateCatalog().Validate(result.Xml, "17.0"));
    }

    [Fact]
    public void Build_RequiresTerminationKind()
    {
        var change = ContractChange.Create(ContractChangeSamples.Modification(), Now);

        var error = Assert.Throws<DomainException>(
            () => new ContractCancelXmlMapper().Build(change, ValidContext()));

        Assert.Contains("расторжению", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RequiresRegistryNumber()
    {
        var change = ContractChange.Create(
            ContractChangeSamples.Termination(eisRegistryNumber: null), Now);

        var error = Assert.Throws<DomainException>(
            () => new ContractCancelXmlMapper().Build(change, ValidContext()));

        Assert.Contains("реестровый номер", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ContractCancelXmlContext ValidContext() =>
        new(
            Sender: "CSMS",
            Receiver: "EIS",
            CreatedAt: Now,
            PacketId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

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
