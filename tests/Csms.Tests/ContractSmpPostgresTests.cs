using System.Data.Common;
using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Csms.Tests;

/// <summary>
/// Интеграционная проверка EF-хранилища СМП на локальном PostgreSQL: запись,
/// документы и журнал должны переживать перезапуск репозитория.
/// </summary>
public class ContractSmpPostgresTests
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
            // Таблицы уже созданы вне истории миграций — для теста достаточно их наличия.
        }
    }

    private static async Task CleanupAsync(IDbContextFactory<EisDbContext> factory, string contractId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var records = await db.ContractSmpRecords
            .Where(row => row.ContractId == contractId)
            .ToListAsync();

        if (records.Count == 0)
        {
            return;
        }

        var ids = records.Select(row => row.Id).ToArray();

        db.ContractSmpDocuments.RemoveRange(
            await db.ContractSmpDocuments.Where(row => ids.Contains(row.RecordId)).ToListAsync());
        db.ContractSmpEvents.RemoveRange(
            await db.ContractSmpEvents.Where(row => ids.Contains(row.RecordId)).ToListAsync());
        db.ContractSmpRecords.RemoveRange(records);

        await db.SaveChangesAsync();
    }

    private static string NewContractId() => "smp-test-" + Guid.NewGuid().ToString("N");

    [EisPackagePostgresTests.PostgresFact]
    public async Task Workflow_SurvivesRepositoryRestart()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var input = ContractSmpRecordSamples.SubContract() with { ContractId = contractId };

        try
        {
            var service = new ContractSmpRecordService(
                new EfCoreContractSmpRecordRepository(factory), clock);

            var record = await service.CreateAsync(
                new CreateContractSmpRecordCommand(input),
                CancellationToken.None);

            record = await service.AddDocumentAsync(
                new AddContractSmpDocumentCommand(
                    record.Id,
                    ContractSmpDocumentType.Declaration,
                    "Декларация о принадлежности к СМП",
                    "Декларация ООО «РосДорМонтаж» от 06.10.2026",
                    ContractSmpRecordSamples.SupplierActor),
                CancellationToken.None);

            record = await service.AcceptAsync(
                new ContractSmpRecordActionCommand(record.Id, ContractSmpRecordSamples.Actor),
                CancellationToken.None);

            record = await service.PushToEisAsync(
                new PushContractSmpRecordCommand(
                    record.Id,
                    ContractSmpRecordSamples.Actor,
                    new DateOnly(2026, 10, 9),
                    "rec-pg-123"),
                CancellationToken.None);

            // Новый экземпляр репозитория — как после перезапуска API.
            var restarted = new ContractSmpRecordService(
                new EfCoreContractSmpRecordRepository(factory), clock);
            var reloaded = await restarted.GetAsync(record.Id, CancellationToken.None);

            Assert.Equal(ContractSmpStatus.PushedToEis, reloaded.Status);
            Assert.Equal(contractId, reloaded.Input.ContractId);
            Assert.Equal("rec-pg-123", reloaded.EisRecordNumber);
            Assert.Equal(new DateOnly(2026, 10, 9), reloaded.EisPublishDate);
            Assert.Equal(record.Revision, reloaded.Revision);

            var document = Assert.Single(reloaded.Documents);
            Assert.True(document.MatchesHash());
            Assert.Equal(4, reloaded.Events.Count);
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }

    [EisPackagePostgresTests.PostgresFact]
    public async Task StaleRevision_IsRejected()
    {
        var factory = CreateFactory();
        await EnsureSchemaAsync(factory);

        var contractId = NewContractId();
        var clock = new TestClock(ContractSmpRecordSamples.Now);
        var firstRepository = new EfCoreContractSmpRecordRepository(factory);
        var secondRepository = new EfCoreContractSmpRecordRepository(factory);
        var input = ContractSmpRecordSamples.SubContract() with { ContractId = contractId };

        try
        {
            var created = ContractSmpRecord.Create(input, ContractSmpRecordSamples.Now);
            await firstRepository.AddAsync(created, CancellationToken.None);

            var first = (await firstRepository.FindAsync(created.Id, CancellationToken.None))!;
            var second = (await secondRepository.FindAsync(created.Id, CancellationToken.None))!;

            first.Update(input with { Comment = "правка из первого окна" },
                ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now.AddMinutes(1));
            await firstRepository.SaveAsync(first, CancellationToken.None);

            second.Update(input with { Comment = "правка из второго окна" },
                ContractSmpRecordSamples.Actor, ContractSmpRecordSamples.Now.AddMinutes(2));

            await Assert.ThrowsAsync<ContractSmpStoreConflictException>(() =>
                secondRepository.SaveAsync(second, CancellationToken.None));
        }
        finally
        {
            await CleanupAsync(factory, contractId);
        }
    }
}
