using System.Text.Json;
using Csms.Domain;

namespace Csms.Infrastructure;

// Строки БД для доступа отделены от прикладных DTO по тому же правилу, что и
// EisPackageRow: в Data лежит исходный документ фронта, чтобы переход с
// json-server не терял поля, которые пока не участвуют в проверке прав.

public sealed class AccessOrganizationRow
{
    public Guid Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string Inn { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessUserRow
{
    public Guid Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string OrganizationKey { get; set; } = string.Empty;
    public string? EmployeeKey { get; set; }
    public bool IsActive { get; set; } = true;
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessEmployeeRow
{
    public Guid Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string OrganizationKey { get; set; } = string.Empty;
    public string? UserKey { get; set; }
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessContractServiceRow
{
    public Guid Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string OrganizationKey { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessFunctionRow
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessRequestRow
{
    public Guid Id { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public string OrganizationKey { get; set; } = string.Empty;
    public string EmployeeKey { get; set; } = string.Empty;
    public string Status { get; set; } = "new";
    public string Data { get; set; } = string.Empty;
}

public sealed class AccessRoleRow
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
}

public sealed class AccessPermissionRow
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class AccessRolePermissionRow
{
    public string RoleCode { get; set; } = string.Empty;
    public string PermissionCode { get; set; } = string.Empty;
}

public sealed class AccessUserRoleRow
{
    public string UserKey { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
}

public sealed class AccessSessionRow
{
    public string Token { get; set; } = string.Empty;
    public string UserKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Общие проверки импорта — нужны и memory-, и postgres-реализации.</summary>
public static class AccessImportValidator
{
    public static string EnsureJson(string data, string label)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new DomainException($"Пустой документ при импорте доступа: {label}.");

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new DomainException($"Документ {label} должен быть объектом JSON.");
            return data;
        }
        catch (JsonException error)
        {
            throw new DomainException($"Некорректный JSON документа {label}: {error.Message}");
        }
    }

    public static void EnsureRoles(IReadOnlyList<string> roleCodes)
    {
        foreach (var code in roleCodes)
        {
            if (!AccessRoleCodes.All.Contains(code, StringComparer.Ordinal))
                throw new DomainException($"Неизвестный код роли доступа: «{code}».");
        }
    }
}
