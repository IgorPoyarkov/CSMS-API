using Csms.Application;

namespace Csms.Api;

public static class EisPackageEndpoints
{
    /// <summary>
    /// Маршруты пакетов. Пути повторяют жизненный цикл: создать → получить
    /// содержимое для подписи → приложить подпись → отправить → спросить
    /// результат. Отдельного «поставить статус» нет: статус меняет только
    /// сценарий, иначе его можно было бы «нарисовать» в обход проверок.
    ///
    /// <c>Produces*</c> здесь не для красоты: без них OpenAPI не знает типов
    /// ответов и контракт для фронта получается пустым.
    /// </summary>
    public static IEndpointRouteBuilder MapEisPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/eis/packages").WithTags("Пакеты ЕИС");

        group.MapGet("/", async (EisPackageService service, CancellationToken cancellationToken) =>
        {
            var packages = await service.ListAsync(cancellationToken);

            return Results.Ok(packages.Select(EisPackageResponse.From));
        })
        .Produces<IEnumerable<EisPackageResponse>>()
        .WithSummary("Список пакетов, свежие сверху");

        group.MapGet("/{id:guid}", async (Guid id, EisPackageService service, CancellationToken cancellationToken) =>
        {
            var package = await service.GetAsync(id, cancellationToken);

            return Results.Ok(EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/", async (
            CreatePackageRequest request,
            EisPackageService service,
            CancellationToken cancellationToken) =>
        {
            var command = new CreatePackageCommand(
                request.DocumentType,
                request.ContractEisNumber,
                request.Ikz,
                request.PayloadXml);

            var package = await service.CreateAsync(command, cancellationToken);

            return Results.Created($"/api/eis/packages/{package.Id}", EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Создать пакет сведений");

        group.MapPost("/{id:guid}/signing", async (
            Guid id,
            EisPackageService service,
            CancellationToken cancellationToken) =>
        {
            var task = await service.PrepareSignatureAsync(id, cancellationToken);

            return Results.Ok(SignatureTaskResponse.From(task));
        })
        .Produces<SignatureTaskResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Получить содержимое и хеш для подписания");

        group.MapPut("/{id:guid}/signature", async (
            Guid id,
            AttachSignatureRequest request,
            EisPackageService service,
            CancellationToken cancellationToken) =>
        {
            var command = new AttachSignatureCommand(
                request.SignatureBase64,
                request.SignerThumbprint,
                request.SignedContentSha256);

            var package = await service.AttachSignatureAsync(id, command, cancellationToken);

            return Results.Ok(EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Приложить подпись, полученную на рабочем месте");

        group.MapPost("/{id:guid}/send", async (
            Guid id,
            EisPackageService service,
            CancellationToken cancellationToken) =>
        {
            var package = await service.SendAsync(id, cancellationToken);

            return Results.Ok(EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Отправить пакет в ЕИС");

        group.MapPost("/{id:guid}/refresh", async (
            Guid id,
            EisPackageService service,
            CancellationToken cancellationToken) =>
        {
            var package = await service.RefreshStatusAsync(id, cancellationToken);

            return Results.Ok(EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Запросить результат проверки ФЛК");

        group.MapPost("/{id:guid}/revise", async (
            Guid id,
            EisPackageService service,
            CancellationToken cancellationToken,
            RevisePackageRequest? request = null) =>
        {
            var package = await service.ReviseAsync(id, cancellationToken, request?.PayloadXml);

            return Results.Ok(EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Вернуть отклонённый пакет в работу, сохранив содержимое");

        return app;
    }
}
