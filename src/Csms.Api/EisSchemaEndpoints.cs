using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>
/// Наблюдение за автообновлением XSD. Статус читается без обращения к ЕИС,
/// принудительная проверка — отдельным POST, чтобы случайный GET не вызывал
/// скачивание архива.
/// </summary>
public static class EisSchemaEndpoints
{
    public static IEndpointRouteBuilder MapEisSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/eis/schemas").WithTags("XSD ЕИС");

        group.MapGet("/status", (EisSchemaUpdateStatus status) => Results.Ok(status.Snapshot()))
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

        return app;
    }
}
