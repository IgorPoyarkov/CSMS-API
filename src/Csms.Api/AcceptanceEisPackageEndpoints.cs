using Csms.Application;
using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>Код ставки НДС из справочника nsiVAT, привязанный к ставке домена.</summary>
public sealed record AcceptanceVatCodeRequest(decimal Rate, string Code);

/// <summary>
/// Параметры выгрузки документа о приёмке, которых нет в домене актирования.
/// Передаются в <see cref="AcceptancePackageInput"/> без догадок о кодах.
/// </summary>
public sealed record CreateAcceptancePackageRequest(
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
    IReadOnlyList<AcceptanceVatCodeRequest>? VatCodes = null);

/// <summary>
/// Выгрузка документа о приёмке в пакет ЕИС (Б7).
///
/// Отдельный файл и отдельная группа маршрутов: электронное актирование —
/// чужая зона, поэтому его собственные маршруты не меняются. Здесь только
/// читается готовый документ и собирается пакет по XSD 17.0.
/// </summary>
public static class AcceptanceEisPackageEndpoints
{
    public static IEndpointRouteBuilder MapAcceptanceEisPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acceptances").WithTags("Выгрузка приёмки в ЕИС");

        group.MapPost("/{id:guid}/eis-package", async (
            Guid id,
            CreateAcceptancePackageRequest request,
            AcceptanceEisPackageBuilder builder,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyDictionary<decimal, string>? vatCodes = request.VatCodes is null
                ? null
                : request.VatCodes.ToDictionary(item => item.Rate, item => item.Code);

            var package = await builder.CreateAsync(
                id,
                new AcceptancePackageInput(
                    DocumentTypeCode: request.DocumentTypeCode,
                    Sender: request.Sender,
                    Receiver: request.Receiver,
                    Mode: request.Mode,
                    PacketId: request.PacketId,
                    CreatedAt: request.CreatedAt,
                    CustomerSpzRegNum: request.CustomerSpzRegNum,
                    SignatureBase64: request.SignatureBase64,
                    SignatureType: request.SignatureType,
                    DeliveryAcceptDate: request.DeliveryAcceptDate,
                    VatCodes: vatCodes),
                cancellationToken);

            return Results.Created($"/api/eis/packages/{package.Id}", EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Собрать и проверить пакет ЕИС по принятому документу о приёмке");

        return app;
    }
}
