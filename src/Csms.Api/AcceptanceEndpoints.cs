using Csms.Application;
using Csms.Domain;

namespace Csms.Api;

public static class AcceptanceEndpoints
{
    /// <summary>
    /// Маршруты электронного актирования (ст. 94 44-ФЗ).
    ///
    /// Отдельного «поставить статус» нет: статус меняет только сценарий
    /// домена, иначе правила приёмки можно было бы обойти одним PUT. Проверка
    /// условий доступна отдельным <c>POST /validate</c> — это тот же код
    /// правил, что и при сохранении, поэтому форма и сервер не расходятся.
    /// </summary>
    public static IEndpointRouteBuilder MapAcceptanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/acceptances").WithTags("Электронное актирование");

        group.MapGet("/", async (
            string? organizationId,
            string? contractId,
            AcceptanceStatus? status,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var items = await service.ListAsync(organizationId, contractId, status, cancellationToken);

            return Results.Ok(items.Select(AcceptanceResponse.From));
        })
        .Produces<IEnumerable<AcceptanceResponse>>()
        .WithSummary("Список документов о приёмке, свежие сверху");

        group.MapGet("/meta", () =>
        {
            var statuses = Enum.GetValues<AcceptanceStatus>()
                .Select(value => new AcceptanceStatusOption(value, value.Ru()))
                .ToList();

            var kinds = Enum.GetValues<AcceptanceKind>()
                .Select(value => new AcceptanceKindOption(value, value.Ru()))
                .ToList();

            var results = Enum.GetValues<AcceptanceResult>()
                .Select(value => new AcceptanceResultOption(value, (int)value, value.Ru()))
                .ToList();

            var requirements = Enum.GetValues<AcceptanceDefectRequirement>()
                .Select(value => new AcceptanceDefectRequirementOption(value, value.Ru()))
                .ToList();

            return Results.Ok(new AcceptanceMetaResponse(
                statuses,
                kinds,
                results,
                requirements,
                AcceptanceVatRates.Allowed,
                AcceptanceRules.MaxReviewTermDays));
        })
        .Produces<AcceptanceMetaResponse>()
        .WithSummary("Справочные значения модуля");

        group.MapPost("/validate", (
            ValidateAcceptanceRequest request,
            AcceptanceService service) =>
        {
            var issues = service.Validate(request.ToDomain());

            return Results.Ok(issues);
        })
        .Produces<IEnumerable<AcceptanceIssue>>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Проверить условия без сохранения");

        group.MapGet("/{id:guid}", async (
            Guid id,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.GetAsync(id, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/", async (
            CreateAcceptanceRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new CreateAcceptanceCommand(request.ToDomain());
            var acceptance = await service.CreateAsync(command, cancellationToken);

            return Results.Created(
                $"/api/acceptances/{acceptance.Id}",
                AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Создать черновик документа о приёмке");

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateAcceptanceRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new UpdateAcceptanceCommand(id, request.ToDomain(), request.Actor ?? string.Empty);
            var acceptance = await service.UpdateAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Изменить черновик или возвращённый документ");

        group.MapPost("/{id:guid}/positions", async (
            Guid id,
            AddAcceptancePositionRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new AddAcceptancePositionCommand(
                id,
                request.Name ?? string.Empty,
                request.Okpd2,
                request.Unit ?? string.Empty,
                request.Quantity,
                request.UnitPrice,
                request.VatRate,
                request.CountryOrigin,
                request.RegistryNumber,
                request.Actor ?? string.Empty);

            var acceptance = await service.AddPositionAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Добавить позицию принятого");

        group.MapDelete("/{id:guid}/positions/{positionId:guid}", async (
            Guid id,
            Guid positionId,
            string? actor,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new RemoveAcceptancePositionCommand(id, positionId, actor ?? string.Empty);
            var acceptance = await service.RemovePositionAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Удалить позицию из черновика");

        group.MapPost("/{id:guid}/defects", async (
            Guid id,
            AddAcceptanceDefectRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new AddAcceptanceDefectCommand(
                id,
                request.PositionId,
                request.Description ?? string.Empty,
                request.Quantity,
                request.Requirement,
                request.Actor ?? string.Empty);

            var acceptance = await service.AddDefectAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Зафиксировать недостаток, выявленный при приёмке");

        group.MapDelete("/{id:guid}/defects/{defectId:guid}", async (
            Guid id,
            Guid defectId,
            string? actor,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new RemoveAcceptanceDefectCommand(id, defectId, actor ?? string.Empty);
            var acceptance = await service.RemoveDefectAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Снять недостаток");

        group.MapPost("/{id:guid}/submit", async (
            Guid id,
            AcceptanceActionRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.SubmitAsync(
                new AcceptanceActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Направить документ заказчику");

        group.MapPost("/{id:guid}/accept", async (
            Guid id,
            AcceptAcceptanceRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.AcceptAsync(
                new AcceptAcceptanceCommand(
                    id,
                    request.Actor ?? string.Empty,
                    request.Result,
                    request.Comment),
                cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Подписать итог приёмки");

        group.MapPost("/{id:guid}/refuse", async (
            Guid id,
            AcceptanceActionRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.RefuseAsync(
                new AcceptanceActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Оформить мотивированный отказ");

        group.MapPost("/{id:guid}/return", async (
            Guid id,
            AcceptanceActionRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.ReturnAsync(
                new AcceptanceActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Вернуть документ на уточнение");

        group.MapPost("/{id:guid}/register", async (
            Guid id,
            RegisterAcceptanceRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var command = new RegisterAcceptanceCommand(
                id,
                request.Actor ?? string.Empty,
                request.RegistrationNumber ?? string.Empty,
                request.RegistrationDate);

            var acceptance = await service.RegisterAsync(command, cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Зафиксировать регистрацию в ЕИС");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            AcceptanceActionRequest request,
            AcceptanceService service,
            CancellationToken cancellationToken) =>
        {
            var acceptance = await service.CancelAsync(
                new AcceptanceActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(AcceptanceResponse.From(acceptance));
        })
        .Produces<AcceptanceResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Отменить незарегистрированный документ");

        return app;
    }
}
