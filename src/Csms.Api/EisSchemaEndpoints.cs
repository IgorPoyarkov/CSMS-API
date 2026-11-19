using System.Text.Json;
using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>
/// Наблюдение за автообновлением XSD и каталог документации для фронтенда.
/// Статус читается без обращения к ЕИС, принудительная проверка — отдельным
/// POST, чтобы случайный GET не вызывал скачивание архива.
/// </summary>
public static class EisSchemaEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapEisSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/eis/schemas").WithTags("XSD ЕИС");

        group.MapGet("/status", (
            EisSchemaUpdateStatus status,
            EisSchemaDocCatalog docs) =>
        {
            var snapshot = status.Snapshot() with
            {
                CatalogHash = docs.Hash,
                CatalogVersion = docs.Version,
                CatalogGeneratedAt = docs.GeneratedAt,
            };

            return Results.Ok(snapshot);
        })
        .Produces<EisSchemaStatusSnapshot>()
        .WithSummary("Состояние последней проверки обновлений XSD ЕИС");

        group.MapPost("/update", async (
            EisSchemaDownloader downloader,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await downloader.CheckAndInstallAsync(cancellationToken);

            return snapshot.State == "Error"
                ? Results.Problem(snapshot.LastError, statusCode: StatusCodes.Status502BadGateway)
                : Results.Ok(snapshot);
        })
        .Produces<EisSchemaStatusSnapshot>()
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithSummary("Проверить ЕИС и применить новую версию XSD");

        group.MapGet("/catalog", (HttpContext context, EisSchemaDocCatalog docs) =>
        {
            context.Response.Headers.ETag = $"\"{docs.Hash}\"";
            context.Response.Headers.CacheControl = "no-cache";

            return Results.Bytes(docs.Json, "application/json");
        })
        .Produces<byte[]>()
        .WithSummary("Текущий каталог определений XSD ЕИС");

        group.MapGet("/changes", (string? from, EisSchemaDocCatalog docs) =>
            Results.Ok(docs.Changes(from)))
        .Produces<EisSchemaUpdateEvent>()
        .WithSummary("Что изменилось в каталоге относительно переданного хеша");

        group.MapGet("/events", async (
            HttpContext context,
            EisSchemaDocCatalog docs,
            EisSchemaEventBroker broker,
            CancellationToken cancellationToken) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await using var subscription = broker.Subscribe();

            // Сразу отдаём текущий хеш: фронт синхронизируется даже если
            // обновление случилось до подключения.
            await WriteSseAsync(context.Response, "ready", new
            {
                catalogHash = docs.Hash,
                version = docs.Version,
                generatedAt = docs.GeneratedAt,
            }, cancellationToken);

            await foreach (var change in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteSseAsync(context.Response, "schemas-updated", change, cancellationToken);
            }
        })
        .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
        .WithSummary("SSE: уведомление об обновлении каталога XSD ЕИС");

        return app;
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        object data,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data, SseJsonOptions);

        await response.WriteAsync(
            $"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
