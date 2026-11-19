using Csms.Application;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Csms.Tests;

/// <summary>
/// Интеграционная проверка контура доступа на том же локальном PostgreSQL,
/// что и тесты пакетов. Пропускается без EIS_TEST_DB.
/// </summary>
public class AccessPostgresTests
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

    [EisPackagePostgresTests.PostgresFact]
    public async Task ImportAndLogin_RoundTripThroughPostgres()
    {
        var options = new DbContextOptionsBuilder<EisDbContext>()
            .UseNpgsql(ConnectionString).Options;
        var factory = new TestContextFactory(options);
        var store = new EfCoreAccessStore(factory, TimeProvider.System);
        var service = new AccessService(store, TimeProvider.System);

        await CleanupAsync(factory);

        try
        {
            await store.ImportAsync(new AccessImportRequest(
                [new ImportedOrganization("99901", """{"id":99901,"inn":"7700000000"}""")],
                [new ImportedUser(
                    "99901",
                    "access-test@csms.local",
                    "qwerty12",
                    "99901",
                    "99901",
                    """{"id":99901,"email":"access-test@csms.local"}""",
                    ["administrator"])],
                [new ImportedEmployee("99901", "99901", "99901", """{"id":99901,"userId":99901}""")],
                [new ImportedContractService("99901", "99901", """
                    {"id":99901,"organizationId":99901,"creationMethod":"contract_manager",
                     "contractManager":{"employeeId":99901,"functions":["planning"]}}
                    """)],
                [],
                []));

            var (token, profile) = await service.LoginAsync(
                "access-test@csms.local", "qwerty12", CancellationToken.None);

            Assert.True(profile.IsHead);
            Assert.Equal("Контрактный управляющий (ч. 21 ст. 38 44-ФЗ)", profile.MyRole);
            Assert.Contains("administrator", profile.Roles);
            Assert.Contains("access.manage", profile.Permissions);

            var restored = await service.GetByTokenAsync(token, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.True(restored.CanEditPurchaseNote);

            var roles = await store.GetRolesAsync();
            Assert.Equal(7, roles.Count);
        }
        finally
        {
            await CleanupAsync(factory);
        }
    }

    private static async Task CleanupAsync(IDbContextFactory<EisDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();

        var users = await db.AccessUsers.Where(row => row.ExternalKey == "99901").ToListAsync();
        var userKeys = users.Select(row => row.ExternalKey).ToArray();
        db.AccessSessions.RemoveRange(
            await db.AccessSessions.Where(row => userKeys.Contains(row.UserKey)).ToListAsync());
        db.AccessUserRoles.RemoveRange(
            await db.AccessUserRoles.Where(row => userKeys.Contains(row.UserKey)).ToListAsync());
        db.AccessUsers.RemoveRange(users);

        var employees = await db.AccessEmployees.Where(row => row.ExternalKey == "99901").ToListAsync();
        db.AccessEmployees.RemoveRange(employees);

        var services = await db.AccessContractServices.Where(row => row.ExternalKey == "99901").ToListAsync();
        db.AccessContractServices.RemoveRange(services);

        var organizations = await db.AccessOrganizations.Where(row => row.ExternalKey == "99901").ToListAsync();
        db.AccessOrganizations.RemoveRange(organizations);

        await db.SaveChangesAsync();
    }
}
