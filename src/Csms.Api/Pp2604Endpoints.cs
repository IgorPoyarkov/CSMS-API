using Csms.Application;

namespace Csms.Api;

/// <summary>
/// Каталог приложения N 2 ПП РФ N 2604 и связей ОКПД2 с позициями.
/// Источник — официальный портал опубликования; импорт выполняется
/// инструментом tools/Pp2604Importer, API только отдаёт активную версию.
/// </summary>
public static class Pp2604Endpoints
{
    public static IEndpointRouteBuilder MapPp2604Endpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pp2604")
            .WithTags("ПП РФ N 2604 / ОКПД2");

        group.MapGet("/mapping", async (
            IPp2604Catalog catalog,
            CancellationToken cancellationToken) =>
        {
            var mapping = await catalog.GetActiveAsync(cancellationToken);
            return mapping is null
                ? Results.NotFound(new
                {
                    error = "Каталог ПП РФ N 2604 не импортирован. Запустите tools/Pp2604Importer с EIS_STORE=postgres.",
                })
                : Results.Ok(mapping);
        })
        .Produces<Pp2604Mapping>()
        .Produces(StatusCodes.Status404NotFound)
        .WithSummary("Активная версия приложения N 2 и связей ОКПД2");

        group.MapGet("/resolve", async (
            string code,
            IPp2604Catalog catalog,
            CancellationToken cancellationToken,
            string? purpose = null) =>
        {
            var mapping = await catalog.GetActiveAsync(cancellationToken);
            if (mapping is null)
            {
                return Results.NotFound(new { error = "Каталог ПП РФ N 2604 не импортирован." });
            }

            return Results.Ok(Pp2604CategoryResolver.Resolve(mapping, code, purpose));
        })
        .Produces<Pp2604Resolution>()
        .Produces(StatusCodes.Status404NotFound)
        .WithSummary("Подобрать позицию приложения N 2 по коду ОКПД2");

        return app;
    }
}
