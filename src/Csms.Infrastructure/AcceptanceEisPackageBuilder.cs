using Csms.Application;
using Csms.Domain;

namespace Csms.Infrastructure;

/// <summary>
/// Параметры выгрузки документа о приёмке, которых нет в домене актирования.
///
/// Коды справочников и реквизиты обмена передаёт вызывающий код: маппер не
/// подбирает их по названию, а ЕИС всё равно отклонит выдуманное значение.
/// </summary>
public sealed record AcceptancePackageInput(
    string DocumentTypeCode,
    string? Sender = null,
    string? Receiver = null,
    string? Mode = null,
    Guid? PacketId = null,
    DateTimeOffset? CreatedAt = null,
    string? CustomerSpzRegNum = null,
    string? SignatureBase64 = null,
    string? SignatureType = null,
    DateOnly? DeliveryAcceptDate = null,
    IReadOnlyDictionary<decimal, string>? VatCodes = null);

/// <summary>
/// Сборка пакета ЕИС по принятому документу о приёмке (Б7).
///
/// Оркестрация вынесена из endpoint-а в отдельный сервис: так проверки статуса,
/// обязательных справочников и XSD можно покрыть тестами без поднятия веб-хоста.
/// Endpoint остаётся тонким и только переводит HTTP-DTO в этот вызов.
/// </summary>
public sealed class AcceptanceEisPackageBuilder(
    IAcceptanceRepository acceptances,
    AcceptanceXmlMapper mapper,
    EisPackageService packages,
    IEisSchemaCatalog schemaCatalog,
    TimeProvider clock)
{
    /// <summary>Собрать, проверить по XSD и сохранить пакет.</summary>
    public async Task<EisPackage> CreateAsync(
        Guid acceptanceId,
        AcceptancePackageInput input,
        CancellationToken cancellationToken)
    {
        var acceptance = await acceptances.FindAsync(acceptanceId, cancellationToken)
            ?? throw new DomainException($"Документ о приёмке {acceptanceId} не найден.");

        if (acceptance.Status is not (
            AcceptanceStatus.Accepted or
            AcceptanceStatus.AcceptedWithDiscrepancies or
            AcceptanceStatus.RegisteredInEis))
        {
            throw new DomainException(
                $"Выгружать в ЕИС можно принятый документ о приёмке, сейчас «{acceptance.Status.Ru()}».");
        }

        var context = new AcceptanceXmlContext(
            DocumentTypeCode: input.DocumentTypeCode,
            Sender: input.Sender ?? "CSMS",
            Receiver: input.Receiver ?? "EIS",
            CreatedAt: input.CreatedAt ?? clock.GetUtcNow(),
            PacketId: input.PacketId ?? Guid.NewGuid(),
            Mode: input.Mode ?? "TEST",
            CustomerSpzRegNum: input.CustomerSpzRegNum,
            SignatureBase64: input.SignatureBase64,
            SignatureType: input.SignatureType ?? "CAdES-BES",
            VatCodes: input.VatCodes,
            DeliveryAcceptDateOverride: input.DeliveryAcceptDate);

        var built = mapper.Build(acceptance, context);

        if (!schemaCatalog.HasSchema(built.SchemaVersion))
        {
            throw new DomainException(
                $"XSD версии {built.SchemaVersion} не загружены: выгрузка в ЕИС без проверки схемы запрещена.");
        }

        var errors = schemaCatalog.Validate(built.Xml, built.SchemaVersion);
        if (errors.Count > 0)
        {
            throw new DomainException(
                "XML приёмки не прошёл XSD 17.0: " + string.Join("; ", errors.Take(3)) + ".");
        }

        return await packages.CreateAsync(
            new CreatePackageCommand(
                EisDocumentType.ContractExecution,
                acceptance.Input.EisRegistryNumber,
                acceptance.Input.Ikz,
                built.Xml,
                Source: new EisPackageSource(
                    EisSourceDocumentKind.ContractExecution,
                    acceptance.Id.ToString("D")),
                Customer: new EisCustomerSnapshot(
                    FullName: acceptance.Input.CustomerName,
                    RegNumber: input.CustomerSpzRegNum)),
            cancellationToken);
    }
}
