using Csms.Application;
using Csms.Domain;
using Microsoft.EntityFrameworkCore;

namespace Csms.Infrastructure;

/// <summary>PostgreSQL-реализация хранилища доступа (EIS_STORE=postgres).</summary>
public sealed class EfCoreAccessStore(
    IDbContextFactory<EisDbContext> contextFactory,
    TimeProvider clock) : IAccessStore
{
    public async Task<AccessSource?> FindByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AccessUsers.AsNoTracking().FirstOrDefaultAsync(
            row => row.Email == email && row.IsActive, cancellationToken);
        return await ToSourceAsync(db, user, cancellationToken);
    }

    public async Task<AccessSource?> FindByUserKeyAsync(
        string userKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.AccessUsers.AsNoTracking().FirstOrDefaultAsync(
            row => row.ExternalKey == userKey, cancellationToken);
        return await ToSourceAsync(db, user, cancellationToken);
    }

    public async Task<AccessSource?> FindBySessionAsync(
        string token, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.AccessSessions.AsNoTracking().FirstOrDefaultAsync(
            row => row.Token == token && row.ExpiresAt > clock.GetUtcNow(), cancellationToken);
        if (session is null) return null;

        var user = await db.AccessUsers.AsNoTracking().FirstOrDefaultAsync(
            row => row.ExternalKey == session.UserKey && row.IsActive, cancellationToken);
        return await ToSourceAsync(db, user, cancellationToken);
    }

    public async Task<string> CreateSessionAsync(
        string userKey, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.AccessUsers.AnyAsync(row => row.ExternalKey == userKey, cancellationToken))
            throw new DomainException($"Пользователь {userKey} не найден.");

        var token = Guid.NewGuid().ToString("N");
        db.AccessSessions.Add(new AccessSessionRow
        {
            Token = token,
            UserKey = userKey,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task RevokeSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.AccessSessions.FirstOrDefaultAsync(
            row => row.Token == token, cancellationToken);
        if (session is null) return;
        db.AccessSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ImportAsync(AccessImportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var organization in request.Organizations)
        {
            var data = AccessImportValidator.EnsureJson(organization.Data, $"organization {organization.Id}");
            var row = await db.AccessOrganizations.FirstOrDefaultAsync(
                item => item.ExternalKey == organization.Id, cancellationToken);
            if (row is null)
            {
                db.AccessOrganizations.Add(new AccessOrganizationRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = organization.Id,
                    Inn = ReadString(data, "inn"),
                    Data = data,
                });
            }
            else
            {
                row.Inn = ReadString(data, "inn");
                row.Data = data;
            }
        }

        foreach (var employee in request.Employees)
        {
            var data = AccessImportValidator.EnsureJson(employee.Data, $"employee {employee.Id}");
            var row = await db.AccessEmployees.FirstOrDefaultAsync(
                item => item.ExternalKey == employee.Id, cancellationToken);
            if (row is null)
            {
                db.AccessEmployees.Add(new AccessEmployeeRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = employee.Id,
                    OrganizationKey = employee.OrganizationId,
                    UserKey = string.IsNullOrWhiteSpace(employee.UserId) ? null : employee.UserId,
                    Data = data,
                });
            }
            else
            {
                row.OrganizationKey = employee.OrganizationId;
                row.UserKey = string.IsNullOrWhiteSpace(employee.UserId) ? null : employee.UserId;
                row.Data = data;
            }
        }

        foreach (var user in request.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
                throw new DomainException($"У пользователя {user.Id} не задан email.");

            var data = AccessImportValidator.EnsureJson(user.Data, $"user {user.Id}");
            var employeeKey = string.IsNullOrWhiteSpace(user.EmployeeId)
                ? await db.AccessEmployees.Where(row => row.UserKey == user.Id)
                    .Select(row => row.ExternalKey).FirstOrDefaultAsync(cancellationToken)
                : user.EmployeeId;

            var row = await db.AccessUsers.FirstOrDefaultAsync(
                item => item.ExternalKey == user.Id, cancellationToken);
            if (row is null)
            {
                db.AccessUsers.Add(new AccessUserRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = user.Id,
                    Email = AccessService.NormalizeEmail(user.Email),
                    PasswordHash = PasswordHasher.Hash(user.Password),
                    OrganizationKey = user.OrganizationId,
                    EmployeeKey = employeeKey,
                    IsActive = true,
                    Data = data,
                });
            }
            else
            {
                row.Email = AccessService.NormalizeEmail(user.Email);
                row.PasswordHash = PasswordHasher.Hash(user.Password);
                row.OrganizationKey = user.OrganizationId;
                row.EmployeeKey = employeeKey;
                row.Data = data;
            }

            if (user.RoleCodes is { Count: > 0 })
            {
                AccessImportValidator.EnsureRoles(user.RoleCodes);
                var existing = await db.AccessUserRoles
                    .Where(item => item.UserKey == user.Id)
                    .ToListAsync(cancellationToken);
                db.AccessUserRoles.RemoveRange(existing);
                foreach (var code in user.RoleCodes.Distinct(StringComparer.Ordinal))
                    db.AccessUserRoles.Add(new AccessUserRoleRow { UserKey = user.Id, RoleCode = code });
            }
        }

        foreach (var service in request.ContractServices)
        {
            var data = AccessImportValidator.EnsureJson(service.Data, $"contractService {service.Id}");
            var row = await db.AccessContractServices.FirstOrDefaultAsync(
                item => item.ExternalKey == service.Id, cancellationToken);
            if (row is null)
            {
                db.AccessContractServices.Add(new AccessContractServiceRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = service.Id,
                    OrganizationKey = service.OrganizationId,
                    Data = data,
                });
            }
            else
            {
                row.OrganizationKey = service.OrganizationId;
                row.Data = data;
            }
        }

        foreach (var function in request.Functions)
        {
            var data = AccessImportValidator.EnsureJson(function.Data, $"function {function.Id}");
            var row = await db.AccessFunctions.FirstOrDefaultAsync(
                item => item.Code == function.Code, cancellationToken);
            if (row is null)
            {
                db.AccessFunctions.Add(new AccessFunctionRow
                {
                    Id = Guid.NewGuid(),
                    Code = function.Code,
                    IsSystem = true,
                    Data = data,
                });
            }
            else
            {
                row.Data = data;
            }
        }

        foreach (var accessRequest in request.Requests)
        {
            var data = AccessImportValidator.EnsureJson(accessRequest.Data, $"accessRequest {accessRequest.Id}");
            var row = await db.AccessRequests.FirstOrDefaultAsync(
                item => item.ExternalKey == accessRequest.Id, cancellationToken);
            if (row is null)
            {
                db.AccessRequests.Add(new AccessRequestRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = accessRequest.Id,
                    OrganizationKey = accessRequest.OrganizationId,
                    EmployeeKey = accessRequest.EmployeeId,
                    Status = accessRequest.Status,
                    Data = data,
                });
            }
            else
            {
                row.Status = accessRequest.Status;
                row.Data = data;
            }
        }

        await SeedCatalogAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var roles = await db.AccessRoles.AsNoTracking().ToListAsync(cancellationToken);
        var links = await db.AccessRolePermissions.AsNoTracking().ToListAsync(cancellationToken);

        return roles
            .OrderBy(role => role.Code, StringComparer.Ordinal)
            .Select(role => new AccessRoleInfo(
                role.Code,
                role.Name,
                links.Where(link => link.RoleCode == role.Code)
                    .Select(link => link.PermissionCode)
                    .ToArray()))
            .ToArray();
    }

    public async Task<IReadOnlyList<AccessPermissionInfo>> GetPermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AccessPermissions.AsNoTracking()
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .Select(row => new AccessPermissionInfo(row.Code, row.Name))
            .ToArrayAsync(cancellationToken);
    }

    public async Task SetUserRolesAsync(
        string userKey, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default)
    {
        AccessImportValidator.EnsureRoles(roleCodes);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.AccessUsers.AnyAsync(row => row.ExternalKey == userKey, cancellationToken))
            throw new DomainException($"Пользователь {userKey} не найден.");

        var existing = await db.AccessUserRoles
            .Where(row => row.UserKey == userKey)
            .ToListAsync(cancellationToken);
        db.AccessUserRoles.RemoveRange(existing);

        foreach (var code in roleCodes.Distinct(StringComparer.Ordinal))
            db.AccessUserRoles.Add(new AccessUserRoleRow { UserKey = userKey, RoleCode = code });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<AccessSource?> ToSourceAsync(
        EisDbContext db, AccessUserRow? user, CancellationToken cancellationToken)
    {
        if (user is null) return null;

        var organization = await db.AccessOrganizations.AsNoTracking().FirstOrDefaultAsync(
            row => row.ExternalKey == user.OrganizationKey, cancellationToken);

        AccessEmployeeRow? employee = null;
        if (!string.IsNullOrEmpty(user.EmployeeKey))
            employee = await db.AccessEmployees.AsNoTracking().FirstOrDefaultAsync(
                row => row.ExternalKey == user.EmployeeKey, cancellationToken);
        employee ??= await db.AccessEmployees.AsNoTracking().FirstOrDefaultAsync(
            row => row.UserKey == user.ExternalKey, cancellationToken);

        var service = await db.AccessContractServices.AsNoTracking().FirstOrDefaultAsync(
            row => row.OrganizationKey == user.OrganizationKey, cancellationToken);

        var roles = await db.AccessUserRoles.AsNoTracking()
            .Where(row => row.UserKey == user.ExternalKey)
            .Select(row => row.RoleCode)
            .ToListAsync(cancellationToken);

        return new AccessSource(
            user.ExternalKey,
            user.Email,
            user.PasswordHash,
            user.OrganizationKey,
            employee?.ExternalKey,
            user.Data,
            organization?.Data ?? "{}",
            employee?.Data,
            service?.Data,
            roles);
    }

    private static async Task SeedCatalogAsync(EisDbContext db, CancellationToken cancellationToken)
    {
        foreach (var permission in AccessCatalog.Permissions)
        {
            if (!await db.AccessPermissions.AnyAsync(row => row.Code == permission.Code, cancellationToken))
                db.AccessPermissions.Add(new AccessPermissionRow
                {
                    Id = Guid.NewGuid(),
                    Code = permission.Code,
                    Name = permission.Name,
                });
        }

        foreach (var role in AccessCatalog.Roles)
        {
            if (!await db.AccessRoles.AnyAsync(row => row.Code == role.Code, cancellationToken))
                db.AccessRoles.Add(new AccessRoleRow
                {
                    Id = Guid.NewGuid(),
                    Code = role.Code,
                    Name = role.Name,
                    IsSystem = true,
                });

            foreach (var permissionCode in role.PermissionCodes)
            {
                var exists = await db.AccessRolePermissions.AnyAsync(
                    row => row.RoleCode == role.Code && row.PermissionCode == permissionCode,
                    cancellationToken);
                if (!exists)
                    db.AccessRolePermissions.Add(new AccessRolePermissionRow
                    {
                        RoleCode = role.Code,
                        PermissionCode = permissionCode,
                    });
            }
        }
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(property, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            return string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }
}
