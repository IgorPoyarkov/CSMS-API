using Csms.Domain;

namespace Csms.Application;

/// <summary>Создать документ о приёмке или корректировочный документ.</summary>
public sealed record CreateAcceptanceCommand(AcceptanceInput Input);

/// <summary>Изменить черновик или возвращённый для уточнения документ.</summary>
public sealed record UpdateAcceptanceCommand(Guid Id, AcceptanceInput Input, string Actor);

/// <summary>Добавить позицию принятого.</summary>
public sealed record AddAcceptancePositionCommand(
    Guid Id,
    string Name,
    string? Okpd2,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal VatRate,
    string? CountryOrigin,
    string? RegistryNumber,
    string Actor);

public sealed record RemoveAcceptancePositionCommand(Guid Id, Guid PositionId, string Actor);

/// <summary>Зафиксировать недостаток, выявленный при приёмке.</summary>
public sealed record AddAcceptanceDefectCommand(
    Guid Id,
    Guid? PositionId,
    string Description,
    decimal Quantity,
    AcceptanceDefectRequirement Requirement,
    string Actor);

public sealed record RemoveAcceptanceDefectCommand(Guid Id, Guid DefectId, string Actor);

/// <summary>Действие по статусу: направить, отменить.</summary>
public sealed record AcceptanceActionCommand(Guid Id, string Actor, string? Comment = null);

/// <summary>Итог приёмки: принято / принято с расхождениями.</summary>
public sealed record AcceptAcceptanceCommand(
    Guid Id,
    string Actor,
    AcceptanceResult Result,
    string? Comment = null);

/// <summary>Зафиксировать регистрацию документа в ЕИС.</summary>
public sealed record RegisterAcceptanceCommand(
    Guid Id,
    string Actor,
    string RegistrationNumber,
    DateOnly RegistrationDate);
