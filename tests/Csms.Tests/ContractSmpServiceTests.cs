using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>Сценарии приложения на in-memory хранилище: те же правила, что в API.</summary>
public class ContractSmpServiceTests
{
    private static ContractSmpRecordService CreateService(TestClock clock) =>
        new(new InMemoryContractSmpRecordRepository(), clock);

    [Fact]
    public async Task Create_AndList_FilterByOrganizationAndContract()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract()),
            CancellationToken.None);

        await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract() with
            {
                OrganizationId = "2",
                ContractId = "contract-2",
            }),
            CancellationToken.None);

        var all = await service.ListAsync(null, null, CancellationToken.None);
        var byContract = await service.ListAsync(null, ContractSmpRecordSamples.ContractId, CancellationToken.None);
        var byOrganization = await service.ListAsync("2", null, CancellationToken.None);
        var missing = await service.ListAsync("3", null, CancellationToken.None);

        Assert.Equal(2, all.Count);
        Assert.Single(byContract);
        Assert.Single(byOrganization);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Workflow_AcceptRejectResubmitAndPush()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        var record = await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract()),
            CancellationToken.None);

        await service.AcceptAsync(
            new ContractSmpRecordActionCommand(record.Id, ContractSmpRecordSamples.Actor),
            CancellationToken.None);

        Assert.Equal(ContractSmpStatus.Accepted,
            (await service.GetAsync(record.Id, CancellationToken.None)).Status);

        // Принятую запись нельзя отклонить «просто так» — сначала возвращаем в работу.
        await Assert.ThrowsAsync<DomainException>(() => service.RejectAsync(
            new ContractSmpRecordActionCommand(record.Id, ContractSmpRecordSamples.Actor, "передумали"),
            CancellationToken.None));

        var pushed = await service.PushToEisAsync(
            new PushContractSmpRecordCommand(
                record.Id,
                ContractSmpRecordSamples.Actor,
                new DateOnly(2026, 10, 9),
                "rec-123"),
            CancellationToken.None);

        Assert.Equal(ContractSmpStatus.PushedToEis, pushed.Status);
        Assert.Equal("rec-123", pushed.EisRecordNumber);
    }

    [Fact]
    public async Task Delete_IsAllowedOnlyForUnconfirmedRecord()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        var record = await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract()),
            CancellationToken.None);

        await service.DeleteAsync(record.Id, CancellationToken.None);

        Assert.Empty(await service.ListAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_IsBlockedAfterAcceptance()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        var record = await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract()),
            CancellationToken.None);
        await service.AcceptAsync(
            new ContractSmpRecordActionCommand(record.Id, ContractSmpRecordSamples.Actor),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.DeleteAsync(record.Id, CancellationToken.None));

        Assert.Contains("Принятые", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddDocument_ThenValidate_ReturnsVolumeAndPenalty()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        var record = await service.CreateAsync(
            new CreateContractSmpRecordCommand(ContractSmpRecordSamples.SubContract() with
            {
                Amount = 1_000_000m,
            }),
            CancellationToken.None);

        await service.AddDocumentAsync(
            new AddContractSmpDocumentCommand(
                record.Id,
                ContractSmpDocumentType.Declaration,
                "Декларация о принадлежности к СМП",
                "Декларация от 06.10.2026",
                ContractSmpRecordSamples.SupplierActor),
            CancellationToken.None);

        await service.AcceptAsync(
            new ContractSmpRecordActionCommand(record.Id, ContractSmpRecordSamples.Actor),
            CancellationToken.None);

        var volume = await service.CalculateVolumeAsync(
            ContractSmpRecordSamples.ContractId,
            32_000_000m,
            10m,
            CancellationToken.None);

        Assert.Equal(3_200_000m, volume.Required);
        Assert.Equal(1_000_000m, volume.Actual);
        Assert.Equal(2_200_000m, volume.Shortfall);

        var penalty = ContractSmpVolume.CalculatePenalty(volume, 2);
        Assert.NotNull(penalty);
        Assert.Equal(4_400m, penalty.Amount);
        Assert.Equal(2, penalty.OverdueDays);
    }

    [Fact]
    public void Validate_UsesTheSameRulesAsDomain()
    {
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var service = CreateService(clock);

        var issues = service.Validate(ContractSmpRecordSamples.SubContract() with
        {
            SupplierInn = "abc",
        });

        Assert.Contains(issues, issue => issue.Field == "supplierInn" && issue.IsError);
    }
}
