using System.Text.Json;
using Csms.Application;

namespace Csms.Api;

/// <summary>
/// Справочники НСИ: ОКПД2, КТРУ, адреса и любые другие официальные таблицы.
/// Импорт — отдельный POST, чтение не обращается к ЕИС и работает из памяти/БД.
/// </summary>
public static class ReferenceDataEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapReferenceDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/references").WithTags("Справочники НСИ");

        group.MapGet("/datasets", async (IReferenceDataStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetDatasetsAsync(cancellationToken)))
            .Produces<IReadOnlyList<ReferenceDatasetInfo>>()
            .WithSummary("Версии загруженных справочников и их хеши");

        group.MapGet("/{dataset}/items", async (
            string dataset,
            string? search,
            string? parentCode,
            int? take,
            IReferenceDataStore store,
            CancellationToken cancellationToken) =>
        {
            var items = await store.QueryAsync(
                dataset,
                search,
                parentCode,
                take ?? 50,
                cancellationToken);

            return Results.Ok(items);
        })
        .Produces<IReadOnlyList<ReferenceItemInfo>>()
        .WithSummary("Поиск по активной версии справочника");

        group.MapPost("/import", async (
            HttpContext context,
            IReferenceDataStore store,
            ReferenceDataEventBroker broker,
            CancellationToken cancellationToken) =>
        {
            var request = await context.Request.ReadFromJsonAsync<ReferenceImportRequest>(
                JsonOptions, cancellationToken);

            if (request is null)
            {
                return Results.BadRequest(new { error = "Пустое тело запроса." });
            }

            try
            {
                var change = await store.PublishAsync(request, cancellationToken);
                broker.Publish(change);
                return Results.Ok(change);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
        .Produces<ReferenceDataUpdateEvent>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithSummary("Установить новую версию справочника из официальной выгрузки");

        group.MapGet("/events", async (
            HttpContext context,
            IReferenceDataStore store,
            ReferenceDataEventBroker broker,
            CancellationToken cancellationToken) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await using var subscription = broker.Subscribe();

            await WriteSseAsync(context.Response, "ready", new
            {
                datasets = await store.GetDatasetsAsync(cancellationToken),
            }, cancellationToken);

            await foreach (var change in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteSseAsync(context.Response, "references-updated", change, cancellationToken);
            }
        })
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .WithSummary("SSE: уведомление об импорте новой версии справочника");

        return app;
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        object data,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
