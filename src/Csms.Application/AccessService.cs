using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Csms.Domain;

namespace Csms.Application;

/// <summary>Импортируемая из db.json запись. Data — исходный JSON документа фронта.</summary>
public sealed record ImportedOrganization(string Id, string Data);
public sealed record ImportedUser(
    string Id,
    string Email,
    string Password,
    string OrganizationId,
    string? EmployeeId,
    string Data,
    IReadOnlyList<string>? RoleCodes = null);
public sealed record ImportedEmployee(
    string Id,
    string OrganizationId,
    string? UserId,
    string Data);
public sealed record ImportedContractService(string Id, string OrganizationId, string Data);
public sealed record ImportedFunction(string Id, string Code, string Data);
public sealed record ImportedAccessRequest(
    string Id,
    string OrganizationId,
    string EmployeeId,
    string Status,
    string Data);

public sealed record AccessImportRequest(
    IReadOnlyList<ImportedOrganization> Organizations,
    IReadOnlyList<ImportedUser> Users,
    IReadOnlyList<ImportedEmployee> Employees,
    IReadOnlyList<ImportedContractService> ContractServices,
    IReadOnlyList<ImportedFunction> Functions,
    IReadOnlyList<ImportedAccessRequest> Requests);

/// <summary>
/// Всё, что нужно сервису для вычисления прав, без EF-зависимостей. Хранилища
/// (memory/postgres) отвечают только за поиск исходных документов по ключу.
/// </summary>
public sealed record AccessSource(
    string UserKey,
    string Email,
    string PasswordHash,
    string OrganizationKey,
    string? EmployeeKey,
    string UserData,
    string OrganizationData,
    string? EmployeeData,
    string? ContractServiceData,
    IReadOnlyList<string> RoleCodes);

/// <summary>Ответ /api/access/me — зеркало возвращаемых значений хука useMyContractRole.</summary>
public sealed record AccessProfile(
    JsonElement? User,
    JsonElement? Organization,
    JsonElement? Employee,
    JsonElement? ContractService,
    string? MyRole,
    bool IsHead,
    bool IsInContractService,
    IReadOnlyList<string> MyFunctions,
    bool CanEditPurchaseNote,
    bool CanDisagreeKbk,
    bool CanEditCustomerRequisites,
    string? PurchaseNoteDeniedReason,
    bool IsContractServiceMember,
    bool IsPurchaseInitiator,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public sealed record AccessRoleInfo(string Code, string Name, IReadOnlyList<string> PermissionCodes);
public sealed record AccessPermissionInfo(string Code, string Name);

public interface IAccessStore
{
    Task<AccessSource?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<AccessSource?> FindByUserKeyAsync(string userKey, CancellationToken cancellationToken = default);
    Task<AccessSource?> FindBySessionAsync(string token, CancellationToken cancellationToken = default);
    Task<string> CreateSessionAsync(string userKey, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task RevokeSessionAsync(string token, CancellationToken cancellationToken = default);
    Task ImportAsync(AccessImportRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccessPermissionInfo>> GetPermissionsAsync(CancellationToken cancellationToken = default);
    Task SetUserRolesAsync(string userKey, IReadOnlyList<string> roleCodes, CancellationToken cancellationToken = default);
}

/// <summary>
/// PBKDF2 без внешних пакетов: соль и итерации хранятся вместе с хешем, чтобы
/// можно было поднять число итераций, не ломая ранее выданные пароли.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new DomainException("Пароль не может быть пустым.");

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"$pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "pbkdf2" ||
            !int.TryParse(parts[1], out var iterations) || iterations <= 0)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class AccessService(IAccessStore store, TimeProvider clock)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly Regex UnitManagerPosition =
        new("начальник|руководитель|заместитель", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] PurchaseNoteFunctions =
    [
        "planning", "purchase_description", "supplier_determination",
    ];

    public Task<IReadOnlyList<AccessRoleInfo>> GetRolesAsync(CancellationToken ct) =>
        store.GetRolesAsync(ct);

    public Task<IReadOnlyList<AccessPermissionInfo>> GetPermissionsAsync(CancellationToken ct) =>
        store.GetPermissionsAsync(ct);

    public Task ImportAsync(AccessImportRequest request, CancellationToken ct) =>
        store.ImportAsync(request, ct);

    public Task SetUserRolesAsync(string userKey, IReadOnlyList<string> roleCodes, CancellationToken ct) =>
        store.SetUserRolesAsync(userKey, roleCodes, ct);

    public async Task<AccessSource?> GetUserAsync(string userKey, CancellationToken ct) =>
        await store.FindByUserKeyAsync(userKey, ct).ConfigureAwait(false);

    public async Task<(string Token, AccessProfile Profile)> LoginAsync(
        string email, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new DomainException("Укажите email и пароль.");

        var source = await store.FindByEmailAsync(NormalizeEmail(email), ct).ConfigureAwait(false);
        if (source is null || !PasswordHasher.Verify(password, source.PasswordHash))
            throw new DomainException("Неверный email или пароль.");

        var token = await store
            .CreateSessionAsync(source.UserKey, clock.GetUtcNow() + SessionLifetime, ct)
            .ConfigureAwait(false);

        return (token, BuildProfile(source));
    }

    public async Task<AccessProfile?> GetByTokenAsync(string token, CancellationToken ct)
    {
        var source = await store.FindBySessionAsync(token, ct).ConfigureAwait(false);
        return source is null ? null : BuildProfile(source);
    }

    public Task LogoutAsync(string token, CancellationToken ct) =>
        store.RevokeSessionAsync(token, ct);

    /// <summary>
    /// Те же правила, что и в useMyContractRole: роль выводится из утверждённого
    /// состава контрактной службы (приказ Минфина России № 158н), а не из записи
    /// в справочнике ролей. Явные назначения из access.user_roles добавляются
    /// сверху — они нужны администратору и кадровику, которых в КС может не быть.
    /// </summary>
    public static AccessProfile BuildProfile(AccessSource source)
    {
        var user = ParseObject(source.UserData);
        var organization = ParseObject(source.OrganizationData);
        var employee = ParseObject(source.EmployeeData);
        var service = ParseObject(source.ContractServiceData);

        var employeeKey = source.EmployeeKey;
        var creationMethod = StringOf(service, "creationMethod");
        var myRole = (string?)null;
        var myFunctions = new List<string>();

        JsonElement? MatchEmployee(JsonElement candidate) =>
            EqualId(StringOf(candidate, "employeeId"), employeeKey) ? candidate : null;

        if (creationMethod == "contract_manager")
        {
            var manager = MatchEmployee(ObjectOf(service, "contractManager"));
            if (manager is not null)
            {
                myRole = "Контрактный управляющий (ч. 21 ст. 38 44-ФЗ)";
                AddFunctions(myFunctions, manager.Value);
            }
        }
        else
        {
            var head = MatchEmployee(ObjectOf(service, "head"));
            if (head is not null)
            {
                myRole = "Руководитель службы (п. 2.3)";
                AddFunctions(myFunctions, head.Value);
            }

            if (myRole is null && creationMethod == "employees")
            {
                foreach (var member in ArrayOf(service, "members").EnumerateArray())
                {
                    var matched = MatchEmployee(member);
                    if (matched is null) continue;

                    myRole = UnitManagerPosition.IsMatch(StringOf(matched.Value, "position"))
                        ? "Начальник структурного подразделения КС"
                        : "Работник службы";
                    AddFunctions(myFunctions, matched.Value);
                    break;
                }
            }
        }

        // При способе «структурное подразделение» все работники подразделения
        // ведут закупки в рамках службы (п. 2.1 приказа № 158н).
        var employeeUnit = StringOf(employee, "structuralUnitId");
        var serviceUnit = StringOf(service, "structuralUnitId");
        var inStructuralUnitCs = creationMethod == "structural_unit" &&
            !string.IsNullOrEmpty(employeeUnit) && !string.IsNullOrEmpty(serviceUnit) &&
            string.Equals(employeeUnit, serviceUnit, StringComparison.Ordinal);

        var isHead = myRole is not null &&
            (myRole.Contains("Руководитель", StringComparison.Ordinal) ||
             myRole.Contains("Контрактный управляющий", StringComparison.Ordinal));
        var isInContractService = myRole is not null || inStructuralUnitCs;
        var hasAnyData = user.ValueKind != JsonValueKind.Undefined &&
            employee.ValueKind != JsonValueKind.Undefined &&
            service.ValueKind != JsonValueKind.Undefined;

        var canEditPurchaseNote = hasAnyData &&
            (isHead || inStructuralUnitCs || myFunctions.Any(PurchaseNoteFunctions.Contains));
        var canDisagreeKbk = hasAnyData &&
            (isHead || inStructuralUnitCs ||
             myFunctions.Contains("planning") ||
             myFunctions.Contains("contract_conclusion") ||
             myFunctions.Contains("contract_execution"));
        var canEditCustomerRequisites = hasAnyData &&
            (isHead || inStructuralUnitCs || myFunctions.Count > 0);

        string? deniedReason = null;
        if (!canEditPurchaseNote)
        {
            if (employee.ValueKind == JsonValueKind.Undefined)
                deniedReason = "Для изменения полей потребности выполните вход в учётную запись сотрудника организации.";
            else if (service.ValueKind == JsonValueKind.Undefined)
                deniedReason = "В организации не утверждена контрактная служба или контрактный управляющий (раздел «Контрактная служба»).";
            else if (!isInContractService)
                deniedReason = "Вы не входите в состав контрактной службы организации (приказ Минфина России № 158н). Поля СЗ доступны только для просмотра.";
            else
                deniedReason = "В составе контрактной службы вам не назначены полномочия п. 3.1 (планирование), п. 3.2 (описание объекта и НМЦК) или п. 3.3 (определение поставщика). Поля СЗ доступны только для просмотра.";
        }

        var roles = new List<string>(source.RoleCodes);
        if (isHead && !roles.Contains(AccessRoleCodes.ContractServiceHead))
            roles.Add(AccessRoleCodes.ContractServiceHead);
        if (creationMethod == "contract_manager" && myRole is not null &&
            !roles.Contains(AccessRoleCodes.ContractManager))
            roles.Add(AccessRoleCodes.ContractManager);
        if (isInContractService && !isHead && !roles.Contains(AccessRoleCodes.ContractServiceMember))
            roles.Add(AccessRoleCodes.ContractServiceMember);
        if (employee.ValueKind != JsonValueKind.Undefined && !isInContractService &&
            !roles.Contains(AccessRoleCodes.PurchaseInitiator))
            roles.Add(AccessRoleCodes.PurchaseInitiator);
        if (roles.Count == 0) roles.Add(AccessRoleCodes.Viewer);

        var permissions = roles
            .SelectMany(AccessCatalog.PermissionsOf)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        void Ensure(string code)
        {
            if (!permissions.Contains(code)) permissions.Add(code);
        }
        if (canEditPurchaseNote) Ensure(AccessPermissionCodes.PurchaseNoteEdit);
        if (canDisagreeKbk) Ensure(AccessPermissionCodes.KbkDisagree);
        if (canEditCustomerRequisites) Ensure(AccessPermissionCodes.CustomerRequisitesEdit);

        return new AccessProfile(
            NullableObject(user),
            NullableObject(organization),
            NullableObject(employee),
            NullableObject(service),
            myRole,
            isHead,
            isInContractService,
            myFunctions,
            canEditPurchaseNote,
            canDisagreeKbk,
            canEditCustomerRequisites,
            deniedReason,
            isInContractService,
            employee.ValueKind != JsonValueKind.Undefined && !isInContractService,
            roles,
            permissions);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static JsonElement ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new DomainException($"Некорректный JSON документа доступа: {error.Message}");
        }
    }

    private static JsonElement? NullableObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? null : element;

    private static JsonElement ObjectOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static JsonElement ArrayOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value
            : default;

    private static string StringOf(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            return string.Empty;

        // В db.json id и structuralUnitId — числа, а ключи хранилища — строки.
        // Без приведения к строке связь «сотрудник → состав КС» не находится.
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static bool EqualId(string left, string? right) =>
        !string.IsNullOrEmpty(right) &&
        string.Equals(left, right, StringComparison.Ordinal);

    private static void AddFunctions(List<string> target, JsonElement row)
    {
        foreach (var function in ArrayOf(row, "functions").EnumerateArray())
        {
            var code = function.ValueKind == JsonValueKind.String ? function.GetString() : null;
            if (!string.IsNullOrEmpty(code) && !target.Contains(code))
                target.Add(code);
        }
    }
}
