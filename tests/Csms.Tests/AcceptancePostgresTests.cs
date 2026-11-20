using System.Data.Common;
using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Csms.Tests;

/// <summary>
/// Интеграционная проверка EF-хранилища документов о приёмке на том же
/// локальном PostgreSQL, что и тесты пакетов ЕИС. Пропускается без
/// <c>EIS_TEST_DB</c>.
///
/// Смысл теста — документ, его позиции, недостатки и журнал должны переживать
/// перезапуск процесса: читаем их новым экземпляром репозитория, а не из кеша
/// объектов.
/// </summary>
public class AcceptancePostgresTests
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

        var rows = await db.ContractAcceptances
            .Where(row => row.ContractId == contractId)
            .ToListAsync();

        if (rows.Count == 0)
        {
            return;
        }

        // Дочерние строки удаляются каскадом на уровне БД.
        db.ContractAcceptances.RemoveRange(rows);
        await db.SaveChangesAsync();
    }

    private static string NewContractId() => "test-" + Guid.NewGuid().ToString("N");

    [EisPackagePostgresTests.PostgresFact]
    public async Task Workflow_SurvivesRepositoryRestart()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var input = AcceptanceSamples.Primary() with { ContractId = contractId };
        var clock = new TestClock(AcceptanceSamples.Now);

        try
        {
            var service = new AcceptanceService(new EfCoreAcceptanceRepository(factory), clock);

            var acceptance = await service.CreateAsync(
                new CreateAcceptanceCommand(input),
                CancellationToken.None);

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

            acceptance = await service.SubmitAsync(
                new AcceptanceActionCommand(acceptance.Id, AcceptanceSamples.Actor),
                CancellationToken.None);

            acceptance = await service.AddDefectAsync(
                new AddAcceptanceDefectCommand(
                    acceptance.Id,
                    acceptance.Positions[0].Id,
                    "Пять шприцев поставлены с истёкшим сроком годности.",
                    5m,
                    AcceptanceDefectRequirement.Replace,
                    AcceptanceSamples.Customer),
                CancellationToken.None);

            acceptance = await service.AcceptAsync(
                new AcceptAcceptanceCommand(
                    acceptance.Id,
                    AcceptanceSamples.Customer,
                    AcceptanceResult.AcceptedWithDiscrepancies,
                    "Принять с заменой пяти шприцев"),
                CancellationToken.None);

            acceptance = await service.RegisterAsync(
                new RegisterAcceptanceCommand(
                    acceptance.Id,
                    AcceptanceSamples.Customer,
                    "Э-2026/15",
                    new DateOnly(2026, 11, 25)),
                CancellationToken.None);

            // Новый экземпляр репозитория — как после перезапуска API.
            var restarted = new AcceptanceService(new EfCoreAcceptanceRepository(factory), clock);
            var reloaded = await restarted.GetAsync(acceptance.Id, CancellationToken.None);

            Assert.Equal(AcceptanceStatus.RegisteredInEis, reloaded.Status);
            Assert.Equal(AcceptanceResult.AcceptedWithDiscrepancies, reloaded.Result);
            Assert.Equal(AcceptanceSamples.Customer, reloaded.SignedBy);
            Assert.Equal("Э-2026/15", reloaded.EisRegistrationNumber);
            Assert.Equal(contractId, reloaded.Input.ContractId);

            var position = Assert.Single(reloaded.Positions);
            Assert.Equal("Шприц 5 мл", position.Name);
            Assert.Equal(100m, position.Quantity);
            Assert.Equal(20_000m, position.TotalWithVat);
            Assert.Equal(20_000m, reloaded.TotalWithVat);
            Assert.Equal(3_333.33m, reloaded.VatAmount);

            var defect = Assert.Single(reloaded.Defects);
            Assert.Equal(AcceptanceDefectRequirement.Replace, defect.Requirement);
            Assert.Equal(position.Id, defect.PositionId);

            // Создание + пять изменений.
            Assert.Equal(6, reloaded.Revision);
            Assert.Equal(acceptance.Revision, reloaded.Revision);

            Assert.Equal(
                acceptance.Events.Select(entry => (entry.From, entry.To, entry.Action)),
                reloaded.Events.Select(entry => (entry.From, entry.To, entry.Action)));

            Assert.DoesNotContain(reloaded.Validate(), issue => issue.IsError);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }

    [EisPackagePostgresTests.PostgresFact]
    public async Task ListAsync_FiltersByContractAndStatus()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var otherContractId = NewContractId();
        var clock = new TestClock(AcceptanceSamples.Now);

        try
        {
            var service = new AcceptanceService(new EfCoreAcceptanceRepository(factory), clock);

            var first = await service.CreateAsync(
                new CreateAcceptanceCommand(AcceptanceSamples.Primary() with { ContractId = contractId }),
                CancellationToken.None);

            clock.Advance(TimeSpan.FromMinutes(1));

            await service.CreateAsync(
                new CreateAcceptanceCommand(AcceptanceSamples.Primary() with { ContractId = otherContractId }),
                CancellationToken.None);

            clock.Advance(TimeSpan.FromMinutes(1));

            await service.CreateAsync(
                new CreateAcceptanceCommand(
                    AcceptanceSamples.Primary() with { OrganizationId = "org-2", ContractId = otherContractId }),
                CancellationToken.None);

            var byOrganization = await service.ListAsync("org-1", null, null, CancellationToken.None);
            Assert.Equal(2, byOrganization.Count);
            Assert.Equal(otherContractId, byOrganization[0].Input.ContractId);

            var byContract = await service.ListAsync("org-1", contractId, null, CancellationToken.None);
            var only = Assert.Single(byContract);
            Assert.Equal(contractId, only.Input.ContractId);

            await service.AddPositionAsync(
                new AddAcceptancePositionCommand(
                    first.Id, "Шприц 5 мл", null, "шт.", 100m, 200m, 20m, null, null,
                    AcceptanceSamples.Actor),
                CancellationToken.None);

            await service.SubmitAsync(
                new AcceptanceActionCommand(first.Id, AcceptanceSamples.Actor),
                CancellationToken.None);

            var submitted = await service.ListAsync("org-1", null, AcceptanceStatus.Submitted, CancellationToken.None);
            Assert.Single(submitted);

            var drafts = await service.ListAsync("org-1", null, AcceptanceStatus.Draft, CancellationToken.None);
            Assert.Single(drafts);
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
        var repository = new EfCoreAcceptanceRepository(factory);

        try
        {
            var acceptance = ContractAcceptance.Create(
                AcceptanceSamples.Primary() with { ContractId = contractId },
                AcceptanceSamples.Now);

            await repository.AddAsync(acceptance, CancellationToken.None);

            var first = await repository.FindAsync(acceptance.Id, CancellationToken.None);
            var second = await repository.FindAsync(acceptance.Id, CancellationToken.None);

            Assert.NotNull(first);
            Assert.NotNull(second);

            first!.AddPosition(
                "Шприц 5 мл", null, "шт.", 100m, 200m, 20m, null, null,
                AcceptanceSamples.Actor, AcceptanceSamples.Now);
            await repository.SaveAsync(first, CancellationToken.None);

            second!.AddPosition(
                "Перчатки", null, "пара", 50m, 150m, 10m, null, null,
                AcceptanceSamples.Actor, AcceptanceSamples.Now);

            await Assert.ThrowsAsync<AcceptanceStoreConflictException>(() =>
                repository.SaveAsync(second, CancellationToken.None));

            var stored = await repository.FindAsync(acceptance.Id, CancellationToken.None);
            var position = Assert.Single(stored!.Positions);
            Assert.Equal("Шприц 5 мл", position.Name);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }

    [EisPackagePostgresTests.PostgresFact]
    public async Task PositionsAndDefects_AreSyncedOnSave()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var repository = new EfCoreAcceptanceRepository(factory);

        try
        {
            var acceptance = ContractAcceptance.Create(
                AcceptanceSamples.Primary() with { ContractId = contractId },
                AcceptanceSamples.Now);

            acceptance.AddPosition(
                "Шприц 5 мл", null, "шт.", 100m, 200m, 20m, null, null,
                AcceptanceSamples.Actor, AcceptanceSamples.Now);
            acceptance.AddPosition(
                "Перчатки", null, "пара", 50m, 150m, 10m, null, null,
                AcceptanceSamples.Actor, AcceptanceSamples.Now);

            await repository.AddAsync(acceptance, CancellationToken.None);

            var loaded = await repository.FindAsync(acceptance.Id, CancellationToken.None);
            Assert.Equal(2, loaded!.Positions.Count);

            // Удаление позиции и добавление недостатка: дочерние строки должны
            // синхронизироваться, а не копиться.
            loaded.RemovePosition(loaded.Positions[1].Id, AcceptanceSamples.Actor, AcceptanceSamples.Now);
            loaded.AddDefect(
                loaded.Positions[0].Id,
                "Пять шприцев поставлены с истёкшим сроком годности.",
                5m,
                AcceptanceDefectRequirement.Replace,
                AcceptanceSamples.Customer,
                AcceptanceSamples.Now);

            await repository.SaveAsync(loaded, CancellationToken.None);

            var reloaded = await repository.FindAsync(acceptance.Id, CancellationToken.None);

            var position = Assert.Single(reloaded!.Positions);
            Assert.Equal("Шприц 5 мл", position.Name);

            var defect = Assert.Single(reloaded.Defects);
            Assert.Equal(position.Id, defect.PositionId);
            Assert.Equal(AcceptanceDefectRequirement.Replace, defect.Requirement);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }
}
