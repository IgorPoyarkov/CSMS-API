namespace Csms.Domain;

/// <summary>Что именно происходит с контрактом: изменение условий или расторжение.</summary>
public enum ContractChangeKind
{
    /// <summary>Изменение существенных условий контракта (ст. 95 44-ФЗ).</summary>
    Modification = 0,

    /// <summary>Расторжение контракта (ст. 95 44-ФЗ).</summary>
    Termination = 1,
}

/// <summary>
/// Статус документа об изменении/расторжении.
///
/// Переходы линейные и проверяются доменом: черновик → на согласовании →
/// согласован → подписан → зарегистрирован в ЕИС. Отклонить можно только
/// документ на согласовании, отменить — любой незарегистрированный.
/// </summary>
public enum ContractChangeStatus
{
    Draft = 0,
    OnApproval = 1,
    Approved = 2,
    Signed = 3,
    RegisteredInEis = 4,
    Rejected = 5,
    Cancelled = 6,
}

/// <summary>Вид документа, который формируется по изменению или расторжению.</summary>
public enum ContractChangeDocumentType
{
    /// <summary>Соглашение об изменении или расторжении контракта.</summary>
    Agreement = 0,

    /// <summary>Уведомление другой стороны (в том числе об одностороннем отказе).</summary>
    Notice = 1,

    /// <summary>Решение заказчика или поставщика.</summary>
    Decision = 2,

    /// <summary>Судебный акт.</summary>
    CourtDecision = 3,

    /// <summary>Приложение и подтверждающий документ.</summary>
    Attachment = 4,
}

public static class ContractChangeStatusText
{
    public static string Ru(this ContractChangeStatus status) => status switch
    {
        ContractChangeStatus.Draft => "Черновик",
        ContractChangeStatus.OnApproval => "На согласовании",
        ContractChangeStatus.Approved => "Согласован",
        ContractChangeStatus.Signed => "Подписан",
        ContractChangeStatus.RegisteredInEis => "Зарегистрирован в ЕИС",
        ContractChangeStatus.Rejected => "Отклонён",
        ContractChangeStatus.Cancelled => "Отменён",
        _ => status.ToString(),
    };
}

public static class ContractChangeKindText
{
    public static string Ru(this ContractChangeKind kind) => kind switch
    {
        ContractChangeKind.Modification => "Изменение контракта",
        ContractChangeKind.Termination => "Расторжение контракта",
        _ => kind.ToString(),
    };
}

public static class ContractChangeDocumentTypeText
{
    public static string Ru(this ContractChangeDocumentType type) => type switch
    {
        ContractChangeDocumentType.Agreement => "Соглашение",
        ContractChangeDocumentType.Notice => "Уведомление",
        ContractChangeDocumentType.Decision => "Решение",
        ContractChangeDocumentType.CourtDecision => "Судебный акт",
        ContractChangeDocumentType.Attachment => "Приложение",
        _ => type.ToString(),
    };
}
