using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>Параметры оболочки пакета аннулирования, приходящие из API.</summary>
public sealed record ContractCancelPackageInput(
    string Sender = "CSMS",
    string Receiver = "EIS",
    Guid? PacketId = null,
    DateTimeOffset? CreatedAt = null,
    string Mode = "TEST",
    string? SignatureBase64 = null,
    string SignatureType = "CAdES-BES");

/// <summary>
/// Сборка пакета <c>contractCancel</c> по зарегистрированному расторжению
/// контракта (часть Б10). Проверка XSD обязательна, как и для остальных пакетов.
/// </summary>
public sealed class ContractCancelEisPackageBuilder(
    IContractChangeRepository changes,
    ContractCancelXmlMapper mapper,
    EisPackageService packages,
    IEisSchemaCatalog schemaCatalog,
    TimeProvider clock)
{
    public async Task<EisPackage> CreateAsync(
        Guid changeId,
        ContractCancelPackageInput input,
        CancellationToken cancellationToken)
    {
        var change = await changes.FindAsync(changeId, cancellationToken)
            ?? throw new DomainException($"Документ об изменении контракта {changeId} не найден.");

        if (change.Status is not (ContractChangeStatus.Signed or ContractChangeStatus.RegisteredInEis))
        {
            throw new DomainException(
                $"В ЕИС выгружается подписанное расторжение контракта, сейчас «{change.Status.Ru()}».");
        }

        if (change.Input.Kind != ContractChangeKind.Termination)
        {
            throw new DomainException(
                "Пакет contractCancel собирается только по расторжению контракта.");
        }

        var built = mapper.Build(change, new ContractCancelXmlContext(
            Sender: input.Sender,
            Receiver: input.Receiver,
            CreatedAt: input.CreatedAt ?? clock.GetUtcNow(),
            PacketId: input.PacketId ?? Guid.NewGuid(),
            Mode: input.Mode,
            SignatureBase64: input.SignatureBase64,
            SignatureType: input.SignatureType));

        if (!schemaCatalog.HasSchema(built.SchemaVersion))
        {
            throw new DomainException(
                $"XSD версии {built.SchemaVersion} не загружены: выгрузка в ЕИС без проверки схемы запрещена.");
        }

        var errors = schemaCatalog.Validate(built.Xml, built.SchemaVersion);
        if (errors.Count > 0)
        {
            throw new DomainException(
                "XML аннулирования не прошёл XSD 17.0: " + string.Join("; ", errors.Take(3)) + ".");
        }

        return await packages.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.ContractExecution,
                change.Input.EisRegistryNumber,
                change.Input.Ikz,
                built.Xml,
                Source: new EisPackageSource(
                    EisSourceDocumentKind.Contract,
                    change.Id.ToString("D")),
                Customer: new EisCustomerSnapshot(FullName: change.Input.CustomerName)),
            cancellationToken);
    }
}
