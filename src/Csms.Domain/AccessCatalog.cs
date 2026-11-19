namespace Csms.Domain;

/// <summary>
/// Коды ролей нашей системы. Это не роли ЕИС из responsibleRole* (они описывают
/// роль заказчика в XML) и не роли членов комиссии: здесь фиксируются права
/// доступа к интерфейсу и действиям системы.
/// </summary>
public static class AccessRoleCodes
{
    public const string Administrator = "administrator";
    public const string ContractServiceHead = "contract_service_head";
    public const string ContractManager = "contract_manager";
    public const string ContractServiceMember = "contract_service_member";
    public const string PurchaseInitiator = "purchase_initiator";
    public const string HumanResources = "human_resources";
    public const string Viewer = "viewer";

    public static readonly IReadOnlyList<string> All =
    [
        Administrator,
        ContractServiceHead,
        ContractManager,
        ContractServiceMember,
        PurchaseInitiator,
        HumanResources,
        Viewer,
    ];
}

/// <summary>
/// Коды прав на действия. Названия привязаны к проверкам интерфейса, чтобы
/// сервер и фронт говорили на одном языке; вычислениеeffective-прав живёт в
/// AccessService, а catalog задаёт понятийный минимум.
/// </summary>
public static class AccessPermissionCodes
{
    public const string AccessManage = "access.manage";
    public const string EmployeeManage = "employee.manage";
    public const string OrganizationEdit = "organization.edit";
    public const string TasksAssign = "tasks.assign";
    public const string PurchaseNoteEdit = "purchase.note.edit";
    public const string KbkDisagree = "kbk.disagree";
    public const string CustomerRequisitesEdit = "customer.requisites.edit";

    public static readonly IReadOnlyList<string> All =
    [
        AccessManage,
        EmployeeManage,
        OrganizationEdit,
        TasksAssign,
        PurchaseNoteEdit,
        KbkDisagree,
        CustomerRequisitesEdit,
    ];
}

/// <summary>
/// Статический справочник «роль → права». При импорте он попадает в PostgreSQL,
/// после чего права можно менять записями в access.role_permissions без релиза.
/// </summary>
public static class AccessCatalog
{
    private static readonly IReadOnlyDictionary<string, string[]> RolePermissions =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [AccessRoleCodes.Administrator] =
            [
                AccessPermissionCodes.AccessManage,
                AccessPermissionCodes.EmployeeManage,
                AccessPermissionCodes.OrganizationEdit,
                AccessPermissionCodes.TasksAssign,
                AccessPermissionCodes.PurchaseNoteEdit,
                AccessPermissionCodes.KbkDisagree,
                AccessPermissionCodes.CustomerRequisitesEdit,
            ],
            [AccessRoleCodes.ContractServiceHead] =
            [
                AccessPermissionCodes.TasksAssign,
                AccessPermissionCodes.PurchaseNoteEdit,
                AccessPermissionCodes.KbkDisagree,
                AccessPermissionCodes.CustomerRequisitesEdit,
            ],
            [AccessRoleCodes.ContractManager] =
            [
                AccessPermissionCodes.TasksAssign,
                AccessPermissionCodes.PurchaseNoteEdit,
                AccessPermissionCodes.KbkDisagree,
                AccessPermissionCodes.CustomerRequisitesEdit,
            ],
            [AccessRoleCodes.ContractServiceMember] =
            [
                AccessPermissionCodes.PurchaseNoteEdit,
                AccessPermissionCodes.CustomerRequisitesEdit,
            ],
            [AccessRoleCodes.PurchaseInitiator] = [],
            [AccessRoleCodes.HumanResources] = [AccessPermissionCodes.EmployeeManage],
            [AccessRoleCodes.Viewer] = [],
        };

    private static readonly IReadOnlyDictionary<string, string> RoleNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccessRoleCodes.Administrator] = "Администратор системы",
            [AccessRoleCodes.ContractServiceHead] = "Руководитель контрактной службы",
            [AccessRoleCodes.ContractManager] = "Контрактный управляющий",
            [AccessRoleCodes.ContractServiceMember] = "Работник контрактной службы",
            [AccessRoleCodes.PurchaseInitiator] = "Инициатор закупки",
            [AccessRoleCodes.HumanResources] = "Сотрудник кадровой службы",
            [AccessRoleCodes.Viewer] = "Наблюдатель",
        };

    public static IReadOnlyList<RoleSeed> Roles =>
        AccessRoleCodes.All
            .Select(code => new RoleSeed(code, RoleNames[code], RolePermissions[code]))
            .ToArray();

    public static IReadOnlyList<PermissionSeed> Permissions =>
        AccessPermissionCodes.All
            .Select(code => new PermissionSeed(code, code))
            .ToArray();

    /// <summary>Права роли. Возвращает пустой список для неизвестной роли.</summary>
    public static IReadOnlyList<string> PermissionsOf(string roleCode) =>
        RolePermissions.TryGetValue(roleCode, out var permissions) ? permissions : [];
}

public sealed record RoleSeed(string Code, string Name, IReadOnlyList<string> PermissionCodes);

public sealed record PermissionSeed(string Code, string Name);
