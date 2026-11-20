using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

/// <summary>
/// Сценарии модуля на хранилище в памяти: те же вызовы, что делает API.
/// Время берётся из <see cref="TestClock"/>, поэтому проверяются и метки
/// статусов, а не только переходы.
/// </summary>
public class ContractChangeServiceTests
{
    private sealed class Harness
    {
        public TestClock Clock { get; } = new(ContractChangeSamples.Now);
        public InMemoryContractChangeRepository Repository { get; } = new();
        public ContractChangeService Service { get; }

        public Harness()
        {
            Service = new ContractChangeService(Repository, Clock);
        }
    }

    /// <summary>Хранилище, которое сообщает о параллельной правке.</summary>
    private sealed class ConflictingRepository(ContractChange change) : IContractChangeRepository
    {
        public Task<IReadOnlyList<ContractChange>> ListAsync(
            string? organizationId,
            string? contractId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContractChange>>([change]);

        public Task<ContractChange?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ContractChange?>(id == change.Id ? change : null);

        public Task AddAsync(ContractChange item, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveAsync(ContractChange item, CancellationToken cancellationToken) =>
            throw new ContractChangeStoreConflictException("Строка изменена другим процессом.");
    }

    [Fact]
    public async Task CreateAsync_SavesDraftWithClockTimestamp()
    {
        var harness = new Harness();

        var change = await harness.Service.CreateAsync(
            new CreateContractChangeCommand(ContractChangeSamples.Modification()),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.Draft, change.Status);
        Assert.Equal(ContractChangeSamples.Now, change.CreatedAt);

        var stored = await harness.Service.ListAsync(null, null, CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal(change.Id, stored[0].Id);
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
    public async Task ListAsync_FiltersByOrganizationAndContract()
    {
        var harness = new Harness();

        await harness.Service.CreateAsync(
            new CreateContractChangeCommand(ContractChangeSamples.Modification()),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Service.CreateAsync(
            new CreateContractChangeCommand(
                ContractChangeSamples.Modification() with { OrganizationId = "org-2", ContractId = "contract-2" }),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Service.CreateAsync(
            new CreateContractChangeCommand(
                ContractChangeSamples.Modification() with { ContractId = "contract-2" }),
            CancellationToken.None);

        var byOrganization = await harness.Service.ListAsync("org-1", null, CancellationToken.None);
        Assert.Equal(2, byOrganization.Count);

        var byContract = await harness.Service.ListAsync("org-1", "contract-2", CancellationToken.None);
        var only = Assert.Single(byContract);
        Assert.Equal("contract-2", only.Input.ContractId);

        // Свежие сверху: последний созданный документ идёт первым.
        Assert.Equal("contract-2", byOrganization[0].Input.ContractId);
    }

    [Fact]
    public async Task FullScenario_UsesClockAndReachesEisRegistration()
    {
        var harness = new Harness();
        var service = harness.Service;

        var change = await service.CreateAsync(
            new CreateContractChangeCommand(ContractChangeSamples.Modification()),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        change = await service.AddDocumentAsync(
            new AddContractChangeDocumentCommand(
                change.Id,
                ContractChangeDocumentType.Agreement,
                string.Empty,
                "Соглашение об изменении цены контракта.",
                ContractChangeSamples.Actor),
            CancellationToken.None);

        Assert.Equal(harness.Clock.UtcNow, change.Documents[0].CreatedAt);
        Assert.True(change.Documents[0].MatchesHash());

        change = await service.SubmitAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Actor),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.OnApproval, change.Status);
        Assert.Equal(harness.Clock.UtcNow, change.SubmittedAt);

        harness.Clock.Advance(TimeSpan.FromHours(1));

        change = await service.ApproveAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Approver),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.Approved, change.Status);

        change = await service.SignAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Approver),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.Signed, change.Status);
        Assert.Equal(ContractChangeSamples.Approver, change.SignedBy);

        change = await service.RegisterAsync(
            new RegisterContractChangeCommand(
                change.Id,
                ContractChangeSamples.Approver,
                "Э-2026/15",
                new DateOnly(2026, 11, 26)),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.RegisteredInEis, change.Status);
        Assert.Equal("Э-2026/15", change.EisRegistrationNumber);

        var reloaded = await service.GetAsync(change.Id, CancellationToken.None);
        Assert.Equal(ContractChangeStatus.RegisteredInEis, reloaded.Status);
        Assert.DoesNotContain(reloaded.Validate(), issue => issue.IsError);
    }

    [Fact]
    public async Task RejectThenRework_StaysConsistent()
    {
        var harness = new Harness();
        var service = harness.Service;

        var change = await service.CreateAsync(
            new CreateContractChangeCommand(ContractChangeSamples.Modification()),
            CancellationToken.None);

        await service.AddDocumentAsync(
            new AddContractChangeDocumentCommand(
                change.Id,
                ContractChangeDocumentType.Agreement,
                "Соглашение",
                "Первая редакция соглашения.",
                ContractChangeSamples.Actor),
            CancellationToken.None);

        await service.SubmitAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Actor),
            CancellationToken.None);

        var rejected = await service.RejectAsync(
            new ContractChangeActionCommand(
                change.Id,
                ContractChangeSamples.Approver,
                "Не указано основание изменения цены."),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.Rejected, rejected.Status);
        Assert.Equal("Не указано основание изменения цены.", rejected.RejectedReason);

        var reworked = await service.UpdateAsync(
            new UpdateContractChangeCommand(
                change.Id,
                ContractChangeSamples.Modification(newPrice: 95_000m),
                ContractChangeSamples.Actor),
            CancellationToken.None);

        Assert.Null(reworked.RejectedReason);

        var resubmitted = await service.SubmitAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Actor),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.OnApproval, resubmitted.Status);
    }

    [Fact]
    public async Task CancelAsync_KeepsReason()
    {
        var harness = new Harness();

        var change = await harness.Service.CreateAsync(
            new CreateContractChangeCommand(ContractChangeSamples.Modification()),
            CancellationToken.None);

        var cancelled = await harness.Service.CancelAsync(
            new ContractChangeActionCommand(change.Id, ContractChangeSamples.Actor, "Создан ошибочно."),
            CancellationToken.None);

        Assert.Equal(ContractChangeStatus.Cancelled, cancelled.Status);
        Assert.Equal("Создан ошибочно.", cancelled.CancelledReason);
    }

    [Fact]
    public async Task SaveConflict_TranslatedToActionableError()
    {
        var change = ContractChange.Create(ContractChangeSamples.Modification(), ContractChangeSamples.Now);
        var service = new ContractChangeService(new ConflictingRepository(change), new TestClock(ContractChangeSamples.Now));

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.UpdateAsync(
                new UpdateContractChangeCommand(
                    change.Id,
                    ContractChangeSamples.Modification(newPrice: 80_000m),
                    ContractChangeSamples.Actor),
                CancellationToken.None));

        Assert.Contains("Обновите страницу", error.Message);
    }

    [Fact]
    public void Validate_ReturnsIssuesInsteadOfThrowing()
    {
        var harness = new Harness();

        var issues = harness.Service.Validate(ContractChangeSamples.Modification(contractPrice: 0));

        Assert.Contains(issues, issue => issue.IsError && issue.Field == "contractPrice");
    }
}
