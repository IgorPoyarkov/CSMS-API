using Csms.Application;

namespace Csms.Api;

/// <summary>
/// Пул функций и задач по Приказу № 158н. Отдаётся из активной версии
/// справочника в БД; клиентский бандл больше не содержит эти 41 пункт.
/// </summary>
public static class Order158nPoolEndpoints
{
    public static IEndpointRouteBuilder MapOrder158nPoolEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/order158n").WithTags("Пул задач 158н");

        group.MapGet("/pool", async (
            IOrder158nPoolCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await catalog.GetAsync(cancellationToken);
            return Results.Ok(new
            {
                snapshot.Version,
                snapshot.SourceDocument,
                snapshot.SourceUrl,
                snapshot.SourceSha256,
                snapshot.ImportedAt,
                count = snapshot.Items.Count,
                items = snapshot.Items,
            });
        })
        .Produces(StatusCodes.Status200OK)
        .WithSummary("Активная версия пула 158н из БД");

        group.MapGet("/pool/{id}", async (
            string id,
            IOrder158nPoolCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await catalog.GetAsync(cancellationToken);
            var item = snapshot.Items.FirstOrDefault(poolItem =>
                string.Equals(poolItem.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(poolItem.Clause, id, StringComparison.OrdinalIgnoreCase));

            return item is null
                ? Results.NotFound(new { error = $"Пункт {id} не найден в пуле 158н." })
                : Results.Ok(item);
        })
        .Produces<Order158nPoolItemInfo>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Один пункт пула по ID или номеру");

        return group;
    }
}
