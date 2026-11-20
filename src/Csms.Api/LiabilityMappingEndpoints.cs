using Csms.Application;

namespace Csms.Api;

/// <summary>
/// Сопоставление структурированной обязанности с нормами ответственности.
/// POST запускает конвейер (идемпотентно по run_id), GET возвращает последний результат.
/// </summary>
public static class LiabilityMappingEndpoints
{
    public static IEndpointRouteBuilder MapLiabilityMappingEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/liability-mapping")
            .WithTags("Compliance & Liability Mapping");

        group.MapPost("/map", async (
            DutyInput duty,
            IDutyLiabilityMappingService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(duty.Identifiers?.DutyId))
            {
                return Results.BadRequest(new { error = "identifiers.dutyId обязателен." });
            }

            var result = await service.MapAsync(duty, cancellationToken);
            return Results.Ok(result);
        })
        .Produces<LiabilityMappingResult>()
        .Produces(StatusCodes.Status400BadRequest)
        .WithSummary("Сопоставить обязанность с нормами ответственности");

        group.MapGet("/{dutyId}", async (
            string dutyId,
            IDutyLiabilityMappingService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetAsync(dutyId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .Produces<LiabilityMappingResult>()
        .Produces(StatusCodes.Status404NotFound)
        .WithSummary("Вернуть последний результат сопоставления обязанности");

        return app;
    }
}
