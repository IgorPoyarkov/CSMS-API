using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Хранилище доступа по умолчанию (EIS_STORE=memory). Поведение поиска и
/// импорта должно совпадать с EfCoreAccessStore: расхождение здесь сразу
/// проявится как «в тесте права есть, на стенде нет».
/// </summary>
public sealed class InMemoryAccessStore(TimeProvider clock) : IAccessStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccessOrganizationRow> _organizations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessUserRow> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessEmployeeRow> _employees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessContractServiceRow> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessFunctionRow> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessRequestRow> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _userRoles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessSessionRow> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _rolePermissions =
        AccessCatalog.Roles.ToDictionary(
            role => role.Code,
            role => role.PermissionCodes.ToList(),
            StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roleNames =
        AccessCatalog.Roles.ToDictionary(role => role.Code, role => role.Name, StringComparer.Ordinal);

    public Task<AccessSource?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var user = _users.Values.FirstOrDefault(row =>
                string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase) && row.IsActive);
            return Task.FromResult(ToSource(user));
        }
    }

    public Task<AccessSource?> FindByUserKeyAsync(string userKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.TryGetValue(userKey, out var user) ? ToSource(user) : null);
        }
    }

    public Task<AccessSource?> FindBySessionAsync(string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(token, out var session) ||
                session.ExpiresAt <= clock.GetUtcNow() ||
                !_users.TryGetValue(session.UserKey, out var user) ||
                !user.IsActive)
                return Task.FromResult<AccessSource?>(null);

            return Task.FromResult(ToSource(user));
        }
    }

    public Task<string> CreateSessionAsync(string userKey, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_users.ContainsKey(userKey))
                throw new DomainException($"Пользователь {userKey} не найден.");

            var token = Guid.NewGuid().ToString("N");
            _sessions[token] = new AccessSessionRow
            {
                Token = token,
                UserKey = userKey,
                CreatedAt = clock.GetUtcNow(),
                ExpiresAt = expiresAt,
            };
            return Task.FromResult(token);
        }
    }

    public Task RevokeSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sessions.Remove(token);
            return Task.CompletedTask;
        }
    }

    public Task ImportAsync(AccessImportRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var organization in request.Organizations)
            {
                _organizations[organization.Id] = new AccessOrganizationRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = organization.Id,
                    Inn = ReadString(organization.Data, "inn"),
                    Data = AccessImportValidator.EnsureJson(organization.Data, $"organization {organization.Id}"),
                };
            }

            foreach (var employee in request.Employees)
            {
                _employees[employee.Id] = new AccessEmployeeRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = employee.Id,
                    OrganizationKey = employee.OrganizationId,
                    UserKey = string.IsNullOrWhiteSpace(employee.UserId) ? null : employee.UserId,
                    Data = AccessImportValidator.EnsureJson(employee.Data, $"employee {employee.Id}"),
                };
            }

            foreach (var user in request.Users)
            {
                if (string.IsNullOrWhiteSpace(user.Email))
                    throw new DomainException($"У пользователя {user.Id} не задан email.");

                var employeeKey = string.IsNullOrWhiteSpace(user.EmployeeId)
                    ? _employees.Values.FirstOrDefault(row => row.UserKey == user.Id)?.ExternalKey
                    : user.EmployeeId;

                _users[user.Id] = new AccessUserRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = user.Id,
                    Email = AccessService.NormalizeEmail(user.Email),
                    PasswordHash = PasswordHasher.Hash(user.Password),
                    OrganizationKey = user.OrganizationId,
                    EmployeeKey = employeeKey,
                    Data = AccessImportValidator.EnsureJson(user.Data, $"user {user.Id}"),
                };

                if (user.RoleCodes is { Count: > 0 })
                {
                    AccessImportValidator.EnsureRoles(user.RoleCodes);
                    _userRoles[user.Id] = user.RoleCodes.Distinct(StringComparer.Ordinal).ToList();
                }
            }

            foreach (var service in request.ContractServices)
            {
                _services[service.Id] = new AccessContractServiceRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = service.Id,
                    OrganizationKey = service.OrganizationId,
                    Data = AccessImportValidator.EnsureJson(service.Data, $"contractService {service.Id}"),
                };
            }

            foreach (var function in request.Functions)
            {
                _functions[function.Code] = new AccessFunctionRow
                {
                    Id = Guid.NewGuid(),
                    Code = function.Code,
                    Data = AccessImportValidator.EnsureJson(function.Data, $"function {function.Id}"),
                };
            }

            foreach (var accessRequest in request.Requests)
            {
                _requests[accessRequest.Id] = new AccessRequestRow
                {
                    Id = Guid.NewGuid(),
                    ExternalKey = accessRequest.Id,
                    OrganizationKey = accessRequest.OrganizationId,
                    EmployeeKey = accessRequest.EmployeeId,
                    Status = accessRequest.Status,
                    Data = AccessImportValidator.EnsureJson(accessRequest.Data, $"accessRequest {accessRequest.Id}"),
                };
            }

            SeedCatalog();
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AccessRoleInfo> result = _roleNames
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new AccessRoleInfo(
                    pair.Key,
                    pair.Value,
                    _rolePermissions.TryGetValue(pair.Key, out var permissions) ? permissions : []))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<AccessPermissionInfo>> GetPermissionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AccessPermissionInfo> result = AccessCatalog.Permissions
                .Select(permission => new AccessPermissionInfo(permission.Code, permission.Name))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task SetUserRolesAsync(string userKey, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_users.ContainsKey(userKey))
                throw new DomainException($"Пользователь {userKey} не найден.");
            AccessImportValidator.EnsureRoles(roleCodes);
            _userRoles[userKey] = roleCodes.Distinct(StringComparer.Ordinal).ToList();
            return Task.CompletedTask;
        }
    }

    private void SeedCatalog()
    {
        foreach (var role in AccessCatalog.Roles)
        {
            _roleNames[role.Code] = role.Name;
            _rolePermissions[role.Code] = role.PermissionCodes.ToList();
        }
    }

    private AccessSource? ToSource(AccessUserRow? user)
    {
        if (user is null) return null;

        _organizations.TryGetValue(user.OrganizationKey, out var organization);

        AccessEmployeeRow? employee = null;
        if (!string.IsNullOrEmpty(user.EmployeeKey))
            _employees.TryGetValue(user.EmployeeKey, out employee);
        employee ??= _employees.Values.FirstOrDefault(row => row.UserKey == user.ExternalKey);

        var service = _services.Values.FirstOrDefault(row =>
            row.OrganizationKey == user.OrganizationKey);

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
            _userRoles.TryGetValue(user.ExternalKey, out var roles) ? roles : []);
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
