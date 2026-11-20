using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Сценарии электронного актирования на хранилище в памяти: те же вызовы, что
/// делает API. Время берётся из <see cref="TestClock"/>, поэтому проверяются и
/// метки статусов, а не только переходы.
/// </summary>
public class AcceptanceServiceTests
{
    private sealed class Harness
    {
        public TestClock Clock { get; } = new(AcceptanceSamples.Now);
        public InMemoryAcceptanceRepository Repository { get; } = new();
        public AcceptanceService Service { get; }

        public Harness()
        {
            Service = new AcceptanceService(Repository, Clock);
        }
    }

    /// <summary>Хранилище, которое сообщает о параллельной правке.</summary>
    private sealed class ConflictingRepository(ContractAcceptance acceptance) : IAcceptanceRepository
    {
        public Task<IReadOnlyList<ContractAcceptance>> ListAsync(
            string? organizationId,
            string? contractId,
            AcceptanceStatus? status,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContractAcceptance>>([acceptance]);

        public Task<ContractAcceptance?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ContractAcceptance?>(id == acceptance.Id ? acceptance : null);

        public Task AddAsync(ContractAcceptance item, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveAsync(ContractAcceptance item, CancellationToken cancellationToken) =>
            throw new AcceptanceStoreConflictException("Строка изменена другим процессом.");
    }

    [Fact]
    public async Task CreateAsync_SavesDraftWithClockTimestamp()
    {
        var harness = new Harness();

        var acceptance = await harness.Service.CreateAsync(
            new CreateAcceptanceCommand(AcceptanceSamples.Primary()),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.Draft, acceptance.Status);
        Assert.Equal(AcceptanceSamples.Now, acceptance.CreatedAt);

        var stored = await harness.Service.ListAsync(null, null, null, CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal(acceptance.Id, stored[0].Id);
    }

    [Fact]
    public async Task GetAsync_UnknownId_Throws()
    {
        var harness = new Harness();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            harness.Service.GetAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("не найден", error.Message);
    }

    [Fact]
    public async Task ListAsync_FiltersByOrganizationContractAndStatus()
    {
        var harness = new Harness();

        var first = await harness.Service.CreateAsync(
            new CreateAcceptanceCommand(AcceptanceSamples.Primary()),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Service.CreateAsync(
            new CreateAcceptanceCommand(
                AcceptanceSamples.Primary() with { OrganizationId = "org-2", ContractId = "contract-2" }),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Service.CreateAsync(
            new CreateAcceptanceCommand(
                AcceptanceSamples.Primary() with { ContractId = "contract-2" }),
            CancellationToken.None);

        var byOrganization = await harness.Service.ListAsync("org-1", null, null, CancellationToken.None);
        Assert.Equal(2, byOrganization.Count);

        var byContract = await harness.Service.ListAsync("org-1", "contract-2", null, CancellationToken.None);
        var only = Assert.Single(byContract);
        Assert.Equal("contract-2", only.Input.ContractId);

        // Свежие сверху: последний созданный документ идёт первым.
        Assert.Equal("contract-2", byOrganization[0].Input.ContractId);

        var drafts = await harness.Service.ListAsync("org-1", null, AcceptanceStatus.Draft, CancellationToken.None);
        Assert.Equal(2, drafts.Count);

        var accepted = await harness.Service.ListAsync("org-1", null, AcceptanceStatus.Accepted, CancellationToken.None);
        Assert.Empty(accepted);

        await harness.Service.AddPositionAsync(
            new AddAcceptancePositionCommand(
                first.Id, "Шприц 5 мл", "32.50.13.190", "шт.", 100m, 200m, 20m, null, null,
                AcceptanceSamples.Actor),
            CancellationToken.None);

        await harness.Service.SubmitAsync(
            new AcceptanceActionCommand(first.Id, AcceptanceSamples.Actor),
            CancellationToken.None);

        var submitted = await harness.Service.ListAsync("org-1", null, AcceptanceStatus.Submitted, CancellationToken.None);
        Assert.Single(submitted);
    }

    [Fact]
    public async Task FullScenario_ReachesEisRegistration()
    {
        var harness = new Harness();
        var service = harness.Service;

        var acceptance = await service.CreateAsync(
            new CreateAcceptanceCommand(AcceptanceSamples.Primary()),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        acceptance = await service.AddPositionAsync(
            new AddAcceptancePositionCommand(
                acceptance.Id,
                "Шприц 5 мл",
                "32.50.13.190",
                "шт.",
                100m,
                200m,
                20m,
                "Россия",
                "1234/2026",
                AcceptanceSamples.Actor),
            CancellationToken.None);

        Assert.Equal(20_000m, acceptance.TotalWithVat);
        Assert.Equal(3_333.33m, acceptance.VatAmount);
        Assert.Equal(16_666.67m, acceptance.TotalWithoutVat);

        acceptance = await service.SubmitAsync(
            new AcceptanceActionCommand(acceptance.Id, AcceptanceSamples.Actor),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.Submitted, acceptance.Status);
        Assert.Equal(new DateOnly(2026, 11, 24), acceptance.ReviewDeadline);

        harness.Clock.Advance(TimeSpan.FromHours(2));

        acceptance = await service.AcceptAsync(
            new AcceptAcceptanceCommand(
                acceptance.Id,
                AcceptanceSamples.Customer,
                AcceptanceResult.Accepted,
                "Принято без замечаний"),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.Accepted, acceptance.Status);
        Assert.Equal(AcceptanceResult.Accepted, acceptance.Result);
        Assert.Equal(AcceptanceSamples.Customer, acceptance.SignedBy);

        acceptance = await service.RegisterAsync(
            new RegisterAcceptanceCommand(
                acceptance.Id,
                AcceptanceSamples.Customer,
                "Э-2026/15",
                new DateOnly(2026, 11, 25)),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.RegisteredInEis, acceptance.Status);
        Assert.Equal("Э-2026/15", acceptance.EisRegistrationNumber);

        var reloaded = await service.GetAsync(acceptance.Id, CancellationToken.None);
        Assert.Equal(AcceptanceStatus.RegisteredInEis, reloaded.Status);
        Assert.Single(reloaded.Positions);
        Assert.DoesNotContain(reloaded.Validate(), issue => issue.IsError);
    }

    [Fact]
    public async Task RefuseThenRework_StaysConsistent()
    {
        var harness = new Harness();
        var service = harness.Service;

        var acceptance = await service.CreateAsync(
            new CreateAcceptanceCommand(AcceptanceSamples.Primary()),
            CancellationToken.None);

        acceptance = await service.AddPositionAsync(
            new AddAcceptancePositionCommand(
                acceptance.Id, "Шприц 5 мл", null, "шт.", 100m, 200m, 20m, null, null,
                AcceptanceSamples.Actor),
            CancellationToken.None);

        acceptance = await service.SubmitAsync(
            new AcceptanceActionCommand(acceptance.Id, AcceptanceSamples.Actor),
            CancellationToken.None);

        acceptance = await service.AddDefectAsync(
            new AddAcceptanceDefectCommand(
                acceptance.Id,
                acceptance.Positions[0].Id,
                "Шприцы не соответствуют требованиям к стерильности.",
                100m,
                AcceptanceDefectRequirement.Reject,
                AcceptanceSamples.Customer),
            CancellationToken.None);

        var refused = await service.RefuseAsync(
            new AcceptanceActionCommand(
                acceptance.Id,
                AcceptanceSamples.Customer,
                "Товар не соответствует условиям контракта по качеству."),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.Refused, refused.Status);
        Assert.Equal(AcceptanceResult.Refused, refused.Result);

        // Отклонённый документ не правится: оформляется новый, поэтому
        // проверяем только неизменность статуса.
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateAsync(
                new UpdateAcceptanceCommand(
                    acceptance.Id,
                    AcceptanceSamples.Primary(),
                    AcceptanceSamples.Actor),
                CancellationToken.None));

        Assert.Contains("«Мотивированный отказ»", error.Message);
    }

    [Fact]
    public async Task ReturnForRevision_AllowsRework()
    {
        var harness = new Harness();
        var service = harness.Service;

        var acceptance = await service.CreateAsync(
            new CreateAcceptanceCommand(AcceptanceSamples.Primary()),
            CancellationToken.None);

        await service.AddPositionAsync(
            new AddAcceptancePositionCommand(
                acceptance.Id, "Шприц 5 мл", null, "шт.", 100m, 200m, 20m, null, null,
                AcceptanceSamples.Actor),
            CancellationToken.None);

        await service.SubmitAsync(
            new AcceptanceActionCommand(acceptance.Id, AcceptanceSamples.Actor),
            CancellationToken.None);

        var returned = await service.ReturnAsync(
            new AcceptanceActionCommand(
                acceptance.Id,
                AcceptanceSamples.Customer,
                "Не указана страна происхождения товара."),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.ReturnedForRevision, returned.Status);

        var updated = await service.UpdateAsync(
            new UpdateAcceptanceCommand(
                acceptance.Id,
                AcceptanceSamples.Primary() with { Notes = "Страна происхождения: Россия" },
                AcceptanceSamples.Actor),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.ReturnedForRevision, updated.Status);

        var resubmitted = await service.SubmitAsync(
            new AcceptanceActionCommand(acceptance.Id, AcceptanceSamples.Actor),
            CancellationToken.None);

        Assert.Equal(AcceptanceStatus.Submitted, resubmitted.Status);
        Assert.Null(resubmitted.ReturnComment);
    }

    [Fact]
    public async Task SaveConflict_TranslatedToActionableError()
    {
        var acceptance = AcceptanceSamples.Draft();
        var service = new AcceptanceService(
            new ConflictingRepository(acceptance),
            new TestClock(AcceptanceSamples.Now));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateAsync(
                new UpdateAcceptanceCommand(
                    acceptance.Id,
                    AcceptanceSamples.Primary(contractPrice: 90_000m),
                    AcceptanceSamples.Actor),
                CancellationToken.None));

        Assert.Contains("Обновите страницу", error.Message);
    }

    [Fact]
    public void Validate_ReturnsIssuesInsteadOfThrowing()
    {
        var harness = new Harness();

        var issues = harness.Service.Validate(AcceptanceSamples.Primary(contractPrice: 0));

        Assert.Contains(issues, issue => issue.IsError && issue.Field == "contractPrice");
    }
}
