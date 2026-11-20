using Csms.Domain;

namespace Csms.Tests;

/// <summary>
/// Правила attracting СМП: ошибки должны быть одинаковыми в форме (POST
/// /validate), при подаче и при каждом переходе статуса.
/// </summary>
public class ContractSmpRulesTests
{
    [Fact]
    public void ValidRecord_HasNoErrors()
    {
        var issues = ContractSmpRules.Validate(
            ContractSmpRecordSamples.SubContract(),
            [],
            ContractSmpStatus.Submitted,
            ContractSmpRecordSamples.Now);

        Assert.DoesNotContain(issues, issue => issue.IsError);
    }

    [Fact]
    public void MissingRequisites_AreReported()
    {
        var input = ContractSmpRecordSamples.SubContract() with
        {
            OrganizationId = string.Empty,
            ContractId = string.Empty,
            DocumentNumber = string.Empty,
            DocumentDate = new DateOnly(2026, 10, 7),
            Amount = null,
            Okpd2Code = "43/31",
            SupplierName = "ОО",
            SupplierInn = "3123",
            SupplierKpp = "123",
            ReceivedAt = ContractSmpRecordSamples.Now.AddDays(-2),
        };

        var issues = ContractSmpRules.Validate(
            input, [], ContractSmpStatus.Submitted, ContractSmpRecordSamples.Now);

        Assert.Contains(issues, issue => issue.Field == "organizationId" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "contractId" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "documentNumber" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "amount" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "okpd2Code" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "supplierName" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "supplierInn" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "supplierKpp" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "documentDate" && issue.IsError);
    }

    [Fact]
    public void Accept_RequiresBothConfirmations()
    {
        var input = ContractSmpRecordSamples.SubContract() with
        {
            SupplierConfirmedAt = null,
            SupplierConfirmedBy = null,
            SupplierConfirmedDocument = null,
            CustomerConfirmedAt = null,
            CustomerConfirmedBy = null,
            CustomerConfirmedDocument = null,
        };

        var issues = ContractSmpRules.Validate(
            input, [], ContractSmpStatus.Accepted, ContractSmpRecordSamples.Now);

        Assert.Contains(issues, issue => issue.Field == "supplierConfirmed" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "customerConfirmed" && issue.IsError);
    }

    [Fact]
    public void SupportingDocument_RequiresLinkToSubContractWhenAccepted()
    {
        var input = ContractSmpRecordSamples.SupportingDocument(
            ContractSmpDocumentType.Acceptance, string.Empty);

        var issues = ContractSmpRules.Validate(
            input, [], ContractSmpStatus.Accepted, ContractSmpRecordSamples.Now);

        Assert.Contains(
            issues,
            issue => issue.Field == "subContractRecordId" && issue.IsError);
    }

    [Fact]
    public void PushToEis_RequiresEisRequisites()
    {
        var input = ContractSmpRecordSamples.SubContract() with
        {
            EisRegistryNumber = "123",
            SupplierKpp = null,
            Okpd2Code = null,
            EisPublishDate = null,
            EisRecordNumber = null,
        };

        var issues = ContractSmpRules.Validate(
            input, [], ContractSmpStatus.PushedToEis, ContractSmpRecordSamples.Now);

        Assert.Contains(issues, issue => issue.Field == "eisRegistryNumber" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "supplierKpp" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "okpd2Code" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "eisPublishDate" && issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "eisRecordNumber" && issue.IsError);
    }

    [Fact]
    public void NonSmpSubContract_IsWarningButNotError()
    {
        var input = ContractSmpRecordSamples.SubContract() with { IsSmpSubject = false };

        var issues = ContractSmpRules.Validate(
            input, [], ContractSmpStatus.Submitted, ContractSmpRecordSamples.Now);

        Assert.DoesNotContain(issues, issue => issue.IsError);
        Assert.Contains(issues, issue => issue.Field == "isSmpSubject" && !issue.IsError);
    }

    [Fact]
    public void Volume_CountsOnlyAcceptedSmpSubContracts()
    {
        var accepted = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract() with { Amount = 4_000_000m },
            ContractSmpRecordSamples.Now);
        accepted.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now);

        var submitted = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract() with { Amount = 3_000_000m },
            ContractSmpRecordSamples.Now);

        var declaration = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract() with
            {
                DocumentType = ContractSmpDocumentType.Declaration,
                Amount = 5_000_000m,
                SubContractRecordId = accepted.Id.ToString(),
            },
            ContractSmpRecordSamples.Now);
        declaration.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now);

        var volume = ContractSmpVolume.Calculate([accepted, submitted, declaration], 10_000_000m, 50m);

        Assert.Equal(5_000_000m, volume.Required);
        Assert.Equal(4_000_000m, volume.Actual);
        Assert.Equal(1_000_000m, volume.Shortfall);
        Assert.Equal(80m, volume.Progress);
        Assert.False(volume.IsComplete);

        var penalty = ContractSmpVolume.CalculatePenalty(volume);

        Assert.NotNull(penalty);
        Assert.Equal(1_000m, penalty.Amount);
        Assert.Equal(1, penalty.OverdueDays);
        Assert.Contains("ПП РФ", penalty.Reference);
    }

    [Fact]
    public void Volume_IsComplete_WhenAllSmpSubContractsAccepted()
    {
        var record = ContractSmpRecord.Create(
            ContractSmpRecordSamples.SubContract() with { Amount = 3_200_000m },
            ContractSmpRecordSamples.Now);
        record.Accept(ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now);

        var volume = ContractSmpVolume.Calculate([record], 32_000_000m, 10m);

        Assert.True(volume.IsComplete);
        Assert.Equal(0m, volume.Shortfall);
        Assert.Equal(100m, volume.Progress);
        Assert.Null(ContractSmpVolume.CalculatePenalty(volume));
    }

    [Fact]
    public void DeadlineCatalog_Contains1466AndCustomerRules()
    {
        Assert.Equal(5, ContractSmpDeadlineCatalog.Rules.Count);
        Assert.Equal(5, ContractSmpDeadlineCatalog.Find("p2")?.WorkingDays);
        Assert.Equal(5, ContractSmpDeadlineCatalog.Find("p3")?.WorkingDays);
        Assert.Equal(10, ContractSmpDeadlineCatalog.Find("p4a")?.WorkingDays);
        Assert.Equal(5, ContractSmpDeadlineCatalog.Find("p4b")?.WorkingDays);
        Assert.Equal(7, ContractSmpDeadlineCatalog.Find("p5")?.WorkingDays);
        Assert.Equal(3, ContractSmpDeadlineCatalog.CustomerEisRule.WorkingDays);
        Assert.Equal(10, ContractSmpDeadlineCatalog.EarlyPaymentWorkingDays);
    }
}
