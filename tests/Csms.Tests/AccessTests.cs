using Csms.Application;
using Csms.Domain;
using Csms.Infrastructure;

namespace Csms.Tests;

public class AccessTests
{
    private const string ServiceJson = """
    {
      "id": 1,
      "organizationId": 1,
      "creationMethod": "employees",
      "head": { "employeeId": 6, "functions": ["planning", "contract_execution"] },
      "members": [
        { "employeeId": 7, "position": "ведущий специалист", "functions": [] },
        { "employeeId": 8, "position": "начальник отдела", "functions": ["contract_conclusion"] }
      ]
    }
    """;

    private static AccessSource Source(
        string employeeId,
        string? serviceJson = ServiceJson,
        string? employeeJson = null,
        IReadOnlyList<string>? roles = null) =>
        new(
            "1",
            "ivanova@gkb7.test",
            PasswordHasher.Hash("qwerty12"),
            "1",
            employeeId,
            """{"id":1,"email":"ivanova@gkb7.test","organizationId":1}""",
            """{"id":1,"inn":"7701234567","shortName":"ГБУЗ"}""",
            employeeJson ?? $$"""{"id":{{employeeId}},"organizationId":1,"position":"ведущий специалист"}""",
            serviceJson,
            roles ?? []);

    [Fact]
    public void PasswordHasher_RoundTrip()
    {
        var hash = PasswordHasher.Hash("qwerty12");

        Assert.StartsWith("$pbkdf2$", hash);
        Assert.True(PasswordHasher.Verify("qwerty12", hash));
        Assert.False(PasswordHasher.Verify("Qwerty12", hash));
        Assert.False(PasswordHasher.Verify("other", hash));
    }

    [Fact]
    public void Head_ReceivesManagementRights()
    {
        var profile = AccessService.BuildProfile(Source("6"));

        Assert.Equal("Руководитель службы (п. 2.3)", profile.MyRole);
        Assert.True(profile.IsHead);
        Assert.True(profile.IsInContractService);
        Assert.True(profile.CanEditPurchaseNote);
        Assert.True(profile.CanDisagreeKbk);
        Assert.True(profile.CanEditCustomerRequisites);
        Assert.Contains("planning", profile.MyFunctions);
        Assert.Contains(AccessRoleCodes.ContractServiceHead, profile.Roles);
        Assert.Contains(AccessPermissionCodes.TasksAssign, profile.Permissions);
    }

    [Fact]
    public void MemberWithoutFunctions_CanOnlyView()
    {
        var profile = AccessService.BuildProfile(Source("7"));

        Assert.Equal("Работник службы", profile.MyRole);
        Assert.True(profile.IsInContractService);
        Assert.False(profile.IsHead);
        Assert.False(profile.CanEditPurchaseNote);
        Assert.False(profile.CanDisagreeKbk);
        Assert.NotNull(profile.PurchaseNoteDeniedReason);
        Assert.Contains("не назначены полномочия", profile.PurchaseNoteDeniedReason, StringComparison.Ordinal);
        Assert.Contains(AccessRoleCodes.ContractServiceMember, profile.Roles);
    }

    [Fact]
    public void DepartmentHeadInRoster_IsTreatedAsHead()
    {
        var profile = AccessService.BuildProfile(Source("8"));

        Assert.Equal("Начальник структурного подразделения КС", profile.MyRole);
        // Как и в фронте, «начальник подразделения» не приравнивается к
        // руководителю службы: у него свои полномочия из состава КС.
        Assert.False(profile.IsHead);
        Assert.True(profile.CanDisagreeKbk);
        Assert.True(profile.CanEditCustomerRequisites);
    }

    [Fact]
    public void EmployeeOutsideContractService_IsPurchaseInitiator()
    {
        var profile = AccessService.BuildProfile(Source("99"));

        Assert.Null(profile.MyRole);
        Assert.False(profile.IsInContractService);
        Assert.True(profile.IsPurchaseInitiator);
        Assert.Contains("не входите в состав контрактной службы", profile.PurchaseNoteDeniedReason, StringComparison.Ordinal);
        Assert.Contains(AccessRoleCodes.PurchaseInitiator, profile.Roles);
    }

    [Fact]
    public void StructuralUnitEmployee_UsesDepartmentRule()
    {
        const string service = """
        {"id":1,"organizationId":1,"creationMethod":"structural_unit","structuralUnitId":15}
        """;
        var profile = AccessService.BuildProfile(
            Source("10", service, """{"id":10,"organizationId":1,"structuralUnitId":15}"""));

        Assert.True(profile.IsInContractService);
        Assert.True(profile.CanEditPurchaseNote);
        Assert.True(profile.CanDisagreeKbk);
        Assert.True(profile.CanEditCustomerRequisites);
    }

    [Fact]
    public void MissingService_ExplainsWhyEditingIsDisabled()
    {
        var profile = AccessService.BuildProfile(Source("6", serviceJson: null));

        Assert.False(profile.CanEditPurchaseNote);
        Assert.Contains("не утверждена контрактная служба", profile.PurchaseNoteDeniedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InMemoryStore_ImportLoginAndSession()
    {
        var clock = TimeProvider.System;
        var store = new InMemoryAccessStore(clock);
        var service = new AccessService(store, clock);

        await store.ImportAsync(new AccessImportRequest(
            [new ImportedOrganization("1", """{"id":1,"inn":"7701234567"}""")],
            [new ImportedUser(
                "1", "ivanova@gkb7.test", "qwerty12", "1", "6",
                """{"id":1,"email":"ivanova@gkb7.test"}""", ["administrator"])],
            [new ImportedEmployee("6", "1", "1", """{"id":6,"userId":1}""")],
            [new ImportedContractService("1", "1", ServiceJson)],
            [],
            []));

        var (token, profile) = await service.LoginAsync(" IVANOVA@gkb7.test ", "qwerty12", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(profile.IsHead);
        Assert.Contains(AccessRoleCodes.Administrator, profile.Roles);
        Assert.Contains(AccessPermissionCodes.AccessManage, profile.Permissions);

        var restored = await service.GetByTokenAsync(token, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal(profile.MyRole, restored.MyRole);

        await service.LogoutAsync(token, CancellationToken.None);
        Assert.Null(await service.GetByTokenAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryStore_RejectsWrongPassword()
    {
        var store = new InMemoryAccessStore(TimeProvider.System);
        await store.ImportAsync(new AccessImportRequest(
            [],
            [new ImportedUser("1", "test@test.test", "qwerty12", "1", null, """{"id":1}""")],
            [], [], [], []));

        var service = new AccessService(store, TimeProvider.System);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.LoginAsync("test@test.test", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryStore_CatalogContainsAllRoles()
    {
        var roles = await new InMemoryAccessStore(TimeProvider.System).GetRolesAsync();

        Assert.Equal(AccessRoleCodes.All.Count, roles.Count);
        Assert.All(roles, role => Assert.NotEmpty(role.Name));
    }
}
