using Csms.Domain;

namespace Csms.Application;

/// <summary>Подать сведения о привлечении СМП по договору с субподрядчиком.</summary>
public sealed record CreateContractSmpRecordCommand(ContractSmpRecordInput Input);

/// <summary>Изменить поданные или отклонённые сведения.</summary>
public sealed record UpdateContractSmpRecordCommand(
    Guid Id,
    ContractSmpRecordInput Input,
    string Actor);

/// <summary>Приложить документ-основание: договор, декларацию, акт или платёжку.</summary>
public sealed record AddContractSmpDocumentCommand(
    Guid Id,
    ContractSmpDocumentType Type,
    string Title,
    string Content,
    string Actor);

public sealed record RemoveContractSmpDocumentCommand(Guid Id, Guid DocumentId, string Actor);

/// <summary>Действие по статусу: принять, отклонить, вернуть в работу.</summary>
public sealed record ContractSmpRecordActionCommand(Guid Id, string Actor, string? Comment = null);

/// <summary>Зафиксировать выгрузку сведений в карточку контракта ЕИС.</summary>
public sealed record PushContractSmpRecordCommand(
    Guid Id,
    string Actor,
    DateOnly PublishDate,
    string RecordNumber);
