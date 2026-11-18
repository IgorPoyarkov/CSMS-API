using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Csms.Tests;

/// <summary>
/// Интеграционный сценарий на настоящей PostgreSQL: пакет и журнал должны
/// переживать перезапуск хранилища — то есть жить в таблицах, а не в памяти
/// процесса.
///
/// Запуск — с переменной окружения <c>EIS_TEST_DB</c>: строка подключения к
/// локальному кластеру шлюза. Без неё тест пропускается, а единые тесты
/// (домен и сценарии на памяти) не требуют БД.
/// </summary>
public sealed class EisPackagePostgresTests
{
    public sealed class PostgresFactAttribute : FactAttribute
    {
        public override string? Skip { get; set; }

        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EIS_TEST_DB")))
            {
                Skip = "EIS_TEST_DB не задан — интеграционный тест на PostgreSQL пропущен.";
            }
        }
    }

    private sealed class EisDbContextFactory(DbContextOptions<EisDbContext> options)
        : IDbContextFactory<EisDbContext>
    {
        public EisDbContext CreateDbContext() => new(options);
    }

    private const string LocalConnectionString =
        "Host=localhost;Port=5433;Database=csms_eis;Username=postgres";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("EIS_TEST_DB") ?? LocalConnectionString;

    private static EisPackageService CreateService()
    {
        var options = new DbContextOptionsBuilder<EisDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new EisPackageService(
            new EfCoreEisPackageRepository(new EisDbContextFactory(options)),
            new MockEisTransport(),
            new PlaceholderXmlBuilder(),
            TimeProvider.System);
    }

    private static async Task CleanupAsync(string contractNumber)
    {
        await using var context = new EisDbContext(
            new DbContextOptionsBuilder<EisDbContext>()
                .UseNpgsql(ConnectionString)
                .Options);

        var rows = await context.Packages
            .AsNoTracking()
            .Where(row => row.ContractEisNumber == contractNumber)
            .ToListAsync();

        context.Packages.RemoveRange(rows);
        await context.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task CreateAndSave_RoundTripThroughDatabase()
    {
        var service = CreateService();
        var contract = "0373100000123000001";
        var ikz = "2612345678901234567890";

        var created = await service.CreateAsync(
            new CreatePackageCommand(EisDocumentType.SmpAttraction, contract, ikz),
            CancellationToken.None);

        var reloaded = await service.GetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(EisPackageStatus.Draft, reloaded.Status);
        Assert.Equal(created.PayloadSha256, reloaded.PayloadSha256);
        Assert.Equal(created.Ikz, reloaded.Ikz);

        await CleanupAsync(contract);
    }

    [PostgresFact]
    public async Task Workflow_StatusAndJournal_StoredInDatabase()
    {
        // Номер оканчивается на 0000: мок ФЛК отклонит пакет после отправки,
        // а это позволяет проверить журнал и сохранение статуса
        var service = CreateService();
        var contract = "0373100000123000000";
        var ikz = "2612345678901234567890";

        var created = await service.CreateAsync(
            new CreatePackageCommand(EisDocumentType.SmpAttraction, contract, ikz),
            CancellationToken.None);

        var task = await service.PrepareSignatureAsync(created.Id, CancellationToken.None);

        var signed = await service.AttachSignatureAsync(
            created.Id,
            new AttachSignatureCommand(
                "MIIC0TCCAbkCAQAwCgYIKoUDBwEBAgEwHjEcMBoGA1UEAxMT",
                "thumbprint",
                task.PayloadSha256),
            CancellationToken.None);

        Assert.Equal(EisPackageStatus.Signed, signed.Status);

        var sent = await service.SendAsync(created.Id, CancellationToken.None);

        Assert.Equal(EisPackageStatus.SentToEis, sent.Status);
        Assert.False(string.IsNullOrWhiteSpace(sent.EisGuid));

        var reloaded = await service.GetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(EisPackageStatus.SentToEis, reloaded.Status);
        Assert.Equal(sent.EisGuid, reloaded.EisGuid);
        Assert.Equal(created.PayloadSha256, reloaded.PayloadSha256);
        Assert.True(reloaded.History.Count >= 3);

        await CleanupAsync(contract);
    }
}
