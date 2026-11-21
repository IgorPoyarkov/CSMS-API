using Csms.Application;
using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>
/// Наблюдение и ручная проверка публичной выгрузки ст. 19.28 КоАП. Статус
/// читается без обращения к ЕИС, принудительная проверка — отдельным POST.
/// Результат поиска содержит реквизиты источника (id, SHA-256 и дату
/// публикации), чтобы ответ можно было предъявить как снимок на дату.
/// </summary>
public static class EisCounterpartyEndpoints
{
    public static IEndpointRouteBuilder MapEisCounterpartyEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/eis/counterparties")
            .WithTags("Проверка контрагентов ЕИС");

        group.MapGet("/status", (
            EisCounterpartyUpdateStatus status,
            ICounterpartyScreeningCatalog catalog) =>
        {
            return Results.Ok(WithSource(status.Snapshot(), catalog));
        })
        .Produces<EisCounterpartyStatusSnapshot>()
        .WithSummary("Состояние последней проверки выгрузки ст. 19.28 КоАП");

        group.MapPost("/update", async (
            EisCounterpartyDownloader downloader,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await downloader.CheckAndInstallAsync(cancellationToken);

            return snapshot.State == "Error"
                ? Results.Problem(snapshot.LastError, statusCode: StatusCodes.Status502BadGateway)
                : Results.Ok(snapshot);
        })
        .Produces<EisCounterpartyStatusSnapshot>()
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .WithSummary("Проверить ЕИС и установить новую выгрузку ст. 19.28 КоАП");

        group.MapGet("/check", (
            string? inn,
            string? ogrn,
            string? kpp,
            ICounterpartyScreeningCatalog catalog) => Check(inn, ogrn, kpp, catalog))
        .Produces<CounterpartyScreeningResult>()
        .WithSummary("Найти контрагента в выгрузке ст. 19.28 КоАП");

        group.MapPost("/check", (
            EisCounterpartyCheckRequest request,
            ICounterpartyScreeningCatalog catalog) =>
        {
            return Check(request.Inn, request.Ogrn, request.Kpp, catalog);
        })
        .Produces<CounterpartyScreeningResult>()
        .WithSummary("Найти контрагента в выгрузке ст. 19.28 КоАП (POST)");

        return app;
    }

    private static IResult Check(
        string? inn,
        string? ogrn,
        string? kpp,
        ICounterpartyScreeningCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(inn) &&
            string.IsNullOrWhiteSpace(ogrn) &&
            string.IsNullOrWhiteSpace(kpp))
        {
            return Results.BadRequest(new
            {
                error = "Укажите ИНН, ОГРН или КПП контрагента для проверки.",
            });
        }

        var result = catalog.Check(inn, ogrn, kpp);

        // Плоские поля источника удобно читать и логировать: ответ остаётся
        // осмысленным и без вложенного объекта source.
        return Results.Ok(new
        {
            result.IsListed,
            result.Matches,
            SourceId = result.Source?.SourceId,
            Sha256 = result.Source?.Sha256,
            PublishedDate = result.Source?.PublishedDate,
            RowCount = result.Source?.RowCount ?? 0,
            InstalledAt = result.Source?.InstalledAt,
            result.CheckedAt,
        });
    }

    private static EisCounterpartyStatusSnapshot WithSource(
        EisCounterpartyStatusSnapshot snapshot,
        ICounterpartyScreeningCatalog catalog)
    {
        // После перезапуска статус может быть ещё Starting, а снимок уже
        // загружен с диска — показываем его реквизиты, а не пустые поля.
        var source = catalog.Source;
        if (source is null)
        {
            return snapshot;
        }

        return snapshot with
        {
            SourceId = snapshot.SourceId ?? source.SourceId,
            PublishedDate = snapshot.PublishedDate ?? source.PublishedDate,
            Sha256 = snapshot.Sha256 ?? source.Sha256,
            RowCount = snapshot.RowCount != 0 ? snapshot.RowCount : source.RowCount,
        };
    }

    /// <summary>Тело POST /check; поля необязательны, но хотя бы одно нужно.</summary>
    public sealed class EisCounterpartyCheckRequest
    {
        public string? Inn { get; set; }

        public string? Ogrn { get; set; }

        public string? Kpp { get; set; }
    }
}
