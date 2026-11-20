using Csms.Domain;

namespace Csms.Application;

/// <summary>Создать документ об изменении или расторжении.</summary>
public sealed record CreateContractChangeCommand(ContractChangeInput Input);

/// <summary>Изменить черновик или отклонённый документ.</summary>
public sealed record UpdateContractChangeCommand(Guid Id, ContractChangeInput Input, string Actor);

/// <summary>Добавить соглашение, уведомление, решение или приложение.</summary>
public sealed record AddContractChangeDocumentCommand(
    Guid Id,
    ContractChangeDocumentType Type,
    string Title,
    string Content,
    string Actor);

public sealed record RemoveContractChangeDocumentCommand(Guid Id, Guid DocumentId, string Actor);

/// <summary>Действие по статусу: отправить, согласовать, отклонить, подписать, отменить.</summary>
public sealed record ContractChangeActionCommand(Guid Id, string Actor, string? Comment = null);

/// <summary>Зафиксировать регистрацию документа в ЕИС.</summary>
public sealed record RegisterContractChangeCommand(
    Guid Id,
    string Actor,
    string RegistrationNumber,
    DateOnly RegistrationDate);
