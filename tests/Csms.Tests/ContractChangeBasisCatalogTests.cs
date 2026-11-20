using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Каталог оснований — часть правил, а не подсказка интерфейса. Тесты держат
/// инварианты: коды уникальны, вид не смешивается, умолчания существуют.
/// </summary>
public class ContractChangeBasisCatalogTests
{
    [Fact]
    public void Codes_AreUniqueAndNotEmpty()
    {
        var codes = ContractChangeBasisCatalog.All.Select(basis => basis.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(code)));
    }

    [Fact]
    public void BothKinds_HaveAtLeastOneBasis()
    {
        Assert.NotEmpty(ContractChangeBasisCatalog.ForKind(ContractChangeKind.Modification));
        Assert.NotEmpty(ContractChangeBasisCatalog.ForKind(ContractChangeKind.Termination));
    }

    [Fact]
    public void ForKind_ReturnsOnlyMatchingBases()
    {
        var termination = ContractChangeBasisCatalog.ForKind(ContractChangeKind.Termination);

        Assert.All(termination, basis => Assert.Equal(ContractChangeKind.Termination, basis.Kind));
    }

    [Theory]
    [InlineData("price_reduction")]
    [InlineData("  PRICE_REDUCTION  ")]
    [InlineData("Price_Reduction")]
    public void Find_IgnoresCaseAndSpaces(string code)
    {
        var basis = ContractChangeBasisCatalog.Find(code);

        Assert.NotNull(basis);
        Assert.Equal("price_reduction", basis!.Code);
    }

    [Fact]
    public void Find_UnknownCode_ReturnsNull()
    {
        Assert.Null(ContractChangeBasisCatalog.Find("unknown"));
        Assert.Null(ContractChangeBasisCatalog.Find(null));
    }

    [Fact]
    public void Default_MatchesRequestedKind()
    {
        Assert.Equal(
            ContractChangeKind.Modification,
            ContractChangeBasisCatalog.Default(ContractChangeKind.Modification).Kind);
        Assert.Equal(
            ContractChangeKind.Termination,
            ContractChangeBasisCatalog.Default(ContractChangeKind.Termination).Kind);
    }

    [Fact]
    public void QuantityBasis_KeepsTenPercentLimit()
    {
        var basis = ContractChangeBasisCatalog.Find("quantity_10");

        Assert.NotNull(basis);
        Assert.True(basis!.AllowsQuantityChange);
        Assert.Equal(10m, basis.MaxQuantityPercent);
        Assert.Equal(10m, basis.MaxPriceIncreasePercent);
    }

    [Fact]
    public void UnilateralBases_RequireNoticeAndEisRegistration()
    {
        foreach (var code in new[] { "customer_unilateral", "supplier_unilateral" })
        {
            var basis = ContractChangeBasisCatalog.Find(code);

            Assert.NotNull(basis);
            Assert.True(basis!.RequiresUnilateralNotice, code);
            Assert.True(basis.RequiresEisRegistration, code);
        }
    }

    [Fact]
    public void CourtBases_RequireJudicialAct()
    {
        foreach (var code in new[] { "court_decision", "force_majeure" })
        {
            var basis = ContractChangeBasisCatalog.Find(code);

            Assert.NotNull(basis);
            Assert.True(basis!.RequiresCourtDecision, code);
        }
    }

    [Fact]
    public void PriceReduction_DoesNotAllowGrowth()
    {
        var basis = ContractChangeBasisCatalog.Find("price_reduction");

        Assert.NotNull(basis);
        Assert.True(basis!.OnlyPriceReduction);
        Assert.Equal(0m, basis.MaxPriceIncreasePercent);
        Assert.False(basis.AllowsQuantityChange);
    }
}
