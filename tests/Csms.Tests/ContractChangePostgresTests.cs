using System.Data.Common;
using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Csms.Tests;

/// <summary>
/// Интеграционная проверка EF-хранилища изменений и расторжений на том же
/// локальном PostgreSQL, что и тесты пакетов ЕИС. Пропускается без
/// <c>EIS_TEST_DB</c>.
///
/// Смысл теста — документ, его редакции и журнал должны переживать перезапуск
/// процесса: читаем их новым экземпляром репозитория, а не из кеша объектов.
/// </summary>
public class ContractChangePostgresTests
{
    private sealed class TestContextFactory(DbContextOptions<EisDbContext> options)
        : IDbContextFactory<EisDbContext>
    {
        public EisDbContext CreateDbContext() => new(options);
    }

    private const string LocalConnectionString =
        "Host=localhost;Port=5433;Database=csms_eis;Username=postgres";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("EIS_TEST_DB") ?? LocalConnectionString;

    private static TestContextFactory CreateFactory() =>
        new(new DbContextOptionsBuilder<EisDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    private static async Task EnsureSchemaAsync(IDbContextFactory<EisDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();

        try
        {
            await db.Database.MigrateAsync();
        }
        catch (DbException error) when (error.SqlState == "42P07")
        {
            // Таблицы уже созданы (например, схема применена вне истории
            // миграций EF). Для теста достаточно самого наличия схемы.
        }
    }

    private static async Task CleanupAsync(IDbContextFactory<EisDbContext> factory, string contractId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var changes = await db.ContractChanges
            .Where(row => row.ContractId == contractId)
            .ToListAsync();

        if (changes.Count == 0)
        {
            return;
        }

        var ids = changes.Select(row => row.Id).ToArray();

        db.ContractChangeDocuments.RemoveRange(
            await db.ContractChangeDocuments.Where(row => ids.Contains(row.ChangeId)).ToListAsync());
        db.ContractChangeEvents.RemoveRange(
            await db.ContractChangeEvents.Where(row => ids.Contains(row.ChangeId)).ToListAsync());
        db.ContractChanges.RemoveRange(changes);

        await db.SaveChangesAsync();
    }

    private static string NewContractId() => "test-" + Guid.NewGuid().ToString("N");

    [EisPackagePostgresTests.PostgresFact]
    public async Task Workflow_SurvivesRepositoryRestart()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var input = ContractChangeSamples.Modification() with { ContractId = contractId };
        var clock = new TestClock(ContractChangeSamples.Now);

        try
        {
            var service = new ContractChangeService(new EfCoreContractChangeRepository(factory), clock);

            var change = await service.CreateAsync(
                new CreateContractChangeCommand(input),
                CancellationToken.None);

            change = await service.AddDocumentAsync(
                new AddContractChangeDocumentCommand(
                    change.Id,
                    ContractChangeDocumentType.Agreement,
                    "Соглашение об изменении цены",
                    "Цена контракта снижается до 90 000 рублей.",
                    ContractChangeSamples.Actor),
                CancellationToken.None);

            change = await service.SubmitAsync(
                new ContractChangeActionCommand(change.Id, ContractChangeSamples.Actor),
                CancellationToken.None);

            change = await service.ApproveAsync(
                new ContractChangeActionCommand(change.Id, ContractChangeSamples.Approver),
                CancellationToken.None);

            change = await service.SignAsync(
                new ContractChangeActionCommand(change.Id, ContractChangeSamples.Approver),
                CancellationToken.None);

            change = await service.RegisterAsync(
                new RegisterContractChangeCommand(
                    change.Id,
                    ContractChangeSamples.Approver,
                    "Э-2026/15",
                    new DateOnly(2026, 11, 26)),
                CancellationToken.None);

            // Новый экземпляр репозитория — как после перезапуска API.
            var restarted = new ContractChangeService(new EfCoreContractChangeRepository(factory), clock);
            var reloaded = await restarted.GetAsync(change.Id, CancellationToken.None);

            Assert.Equal(ContractChangeStatus.RegisteredInEis, reloaded.Status);
            Assert.Equal(ContractChangeSamples.Approver, reloaded.SignedBy);
            Assert.Equal("Э-2026/15", reloaded.EisRegistrationNumber);
            Assert.Equal(new DateOnly(2026, 11, 26), reloaded.EisRegistrationDate);
            Assert.Equal(contractId, reloaded.Input.ContractId);
            Assert.Equal(90_000m, reloaded.Input.NewPrice);

            // Каждая запись увеличивает ревизию: создание и пять переходов.
            Assert.Equal(6, reloaded.Revision);
            Assert.Equal(change.Revision, reloaded.Revision);

            var document = Assert.Single(reloaded.Documents);
            Assert.Equal(ContractChangeDocumentType.Agreement, document.Type);
            Assert.True(document.MatchesHash());
            Assert.Equal(1, document.Version);

            Assert.Equal(
                change.Events.Select(entry => (entry.From, entry.To, entry.Action)),
                reloaded.Events.Select(entry => (entry.From, entry.To, entry.Action)));

            Assert.DoesNotContain(reloaded.Validate(), issue => issue.IsError);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }

    [EisPackagePostgresTests.PostgresFact]
    public async Task ListAsync_FiltersByOrganizationAndContract()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var otherContractId = NewContractId();
        var clock = new TestClock(ContractChangeSamples.Now);

        try
        {
            var service = new ContractChangeService(new EfCoreContractChangeRepository(factory), clock);

            await service.CreateAsync(
                new CreateContractChangeCommand(
                    ContractChangeSamples.Modification() with { ContractId = contractId }),
                CancellationToken.None);

            clock.Advance(TimeSpan.FromMinutes(1));

            await service.CreateAsync(
                new CreateContractChangeCommand(
                    ContractChangeSamples.Modification() with { ContractId = otherContractId }),
                CancellationToken.None);

            clock.Advance(TimeSpan.FromMinutes(1));

            await service.CreateAsync(
                new CreateContractChangeCommand(
                    ContractChangeSamples.Modification() with { OrganizationId = "org-2", ContractId = otherContractId }),
                CancellationToken.None);

            var byOrganization = await service.ListAsync("org-1", null, CancellationToken.None);
            Assert.Equal(2, byOrganization.Count);
            Assert.Equal(otherContractId, byOrganization[0].Input.ContractId);

            var byContract = await service.ListAsync("org-1", contractId, CancellationToken.None);
            var only = Assert.Single(byContract);
            Assert.Equal(contractId, only.Input.ContractId);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
            await CleanupAsync(factory, otherContractId);
        }
    }

    [EisPackagePostgresTests.PostgresFact]
    public async Task ConcurrentEdit_ReportedAsConflict()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var repository = new EfCoreContractChangeRepository(factory);

        try
        {
            var change = ContractChange.Create(
                ContractChangeSamples.Modification() with { ContractId = contractId },
                ContractChangeSamples.Now);

            await repository.AddAsync(change, CancellationToken.None);

            var first = await repository.FindAsync(change.Id, CancellationToken.None);
            var second = await repository.FindAsync(change.Id, CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);

            first!.AddDocument(
                ContractChangeDocumentType.Agreement,
                string.Empty,
                "Редакция первого окна.",
                ContractChangeSamples.Actor,
                ContractChangeSamples.Now);
            await repository.SaveAsync(first, CancellationToken.None);

            second!.AddDocument(
                ContractChangeDocumentType.Agreement,
                string.Empty,
                "Редакция второго окна.",
                ContractChangeSamples.Approver,
                ContractChangeSamples.Now);

            await Assert.ThrowsAsync<ContractChangeStoreConflictException>(() =>
                repository.SaveAsync(second, CancellationToken.None));

            var stored = await repository.FindAsync(change.Id, CancellationToken.None);
            var document = Assert.Single(stored!.Documents);
            Assert.Equal("Редакция первого окна.", document.Content);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }
}
