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

        // Список обязанностей 158н, уже приведённых к форме Сущности А.
        group.MapGet("/duties", async (
            IOrder158nPoolCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await catalog.GetAsync(cancellationToken);
            var duties = snapshot.Items
                .OrderBy(item => item.SortIndex)
                .Select(item => new
                {
                    duty = LiabilityDutyFactory.FromOrder158n(item),
                    clause = item.Clause,
                    sectionTitle = item.SectionTitle,
                    taskTitle = item.DefaultTaskTitle,
                })
                .ToArray();
            return Results.Ok(duties);
        })
        .WithSummary("Список обязанностей контрактной службы по 158н");

        // Сводка по всем пунктам 158н: что уже сопоставлено, что требует проверки.
        group.MapGet("/overview", async (
            IOrder158nPoolCatalog catalog,
            ILiabilityMappingStore store,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await catalog.GetAsync(cancellationToken);
            var overview = new List<LiabilityMappingOverviewItem>();
            foreach (var item in snapshot.Items.OrderBy(entry => entry.SortIndex))
            {
                var result = await store.GetLatestByDutyAsync(item.Id, cancellationToken);
                var top = result?.MatchedNorms
                    .OrderByDescending(match => match.ProbabilityScore)
                    .FirstOrDefault();
                overview.Add(new LiabilityMappingOverviewItem(
                    item.Id,
                    item.Clause,
                    item.SectionTitle,
                    item.OfficialFormulation,
                    result is not null,
                    result?.RunId,
                    result?.CandidateCount ?? 0,
                    result?.MatchedNorms.Count ?? 0,
                    result?.MatchedNorms.Count(match => match.RequiresHumanReview) ?? 0,
                    top?.NormReference,
                    top?.MatchDegree.ToString().ToLowerInvariant(),
                    top?.ProbabilityScore,
                    result?.CalculatedAt));
            }

            return Results.Ok(overview);
        })
        .WithSummary("Сводка сопоставлений по всем пунктам 158н");

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
