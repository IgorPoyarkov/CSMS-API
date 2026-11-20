using Csms.Application;
using Csms.Domain;

namespace Csms.Api;

public static class ContractChangeEndpoints
{
    /// <summary>
    /// Маршруты изменения и расторжения контрактов.
    ///
    /// Отдельного «поставить статус» нет: статус меняет только сценарий домена,
    /// иначе проверки ст. 95 можно было бы обойти одним PUT. Проверка условий
    /// доступна отдельным <c>POST /validate</c> — это тот же код правил, что и
    /// при сохранении, поэтому форма и сервер не расходятся.
    /// </summary>
    public static IEndpointRouteBuilder MapContractChangeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contract-changes").WithTags("Изменение и расторжение контрактов");

        group.MapGet("/", async (
            string? organizationId,
            string? contractId,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var changes = await service.ListAsync(organizationId, contractId, cancellationToken);

            return Results.Ok(changes.Select(ContractChangeResponse.From));
        })
        .Produces<IEnumerable<ContractChangeResponse>>()
        .WithSummary("Список документов, свежие сверху");

        group.MapGet("/bases", () =>
            Results.Ok(ContractChangeBasisCatalog.All.Select(ContractChangeBasisResponse.From)))
        .Produces<IEnumerable<ContractChangeBasisResponse>>()
        .WithSummary("Каталог оснований изменения и расторжения");

        group.MapGet("/meta", () =>
        {
            var statuses = Enum.GetValues<ContractChangeStatus>()
                .Select(value => new ContractChangeStatusOption(value, value.Ru()))
                .ToList();

            var kinds = Enum.GetValues<ContractChangeKind>()
                .Select(value => new ContractChangeKindOption(value, value.Ru()))
                .ToList();

            var documentTypes = Enum.GetValues<ContractChangeDocumentType>()
                .Select(value => new ContractChangeDocumentTypeOption(value, value.Ru()))
                .ToList();

            return Results.Ok(new ContractChangeMetaResponse(
                statuses,
                kinds,
                documentTypes,
                ContractChangeRules.UnilateralNoticeDays));
        })
        .Produces<ContractChangeMetaResponse>()
        .WithSummary("Справочные значения модуля");

        group.MapPost("/validate", (
            ValidateContractChangeRequest request,
            ContractChangeService service) =>
        {
            var issues = service.Validate(request.ToDomain());

            return Results.Ok(issues);
        })
        .Produces<IEnumerable<ContractChangeIssue>>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Проверить условия без сохранения");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.GetAsync(id, cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/", async (
            CreateContractChangeRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var command = new CreateContractChangeCommand(request.ToDomain());
            var change = await service.CreateAsync(command, cancellationToken);

            return Results.Created(
                $"/api/contract-changes/{change.Id}",
                ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Создать черновик изменения или расторжения");

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateContractChangeRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var command = new UpdateContractChangeCommand(id, request.ToDomain(), request.Actor ?? string.Empty);
            var change = await service.UpdateAsync(command, cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Изменить черновик или отклонённый документ");

        group.MapPost("/{id:guid}/documents", async (
            Guid id,
            AddContractChangeDocumentRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var command = new AddContractChangeDocumentCommand(
                id,
                request.Type,
                request.Title ?? string.Empty,
                request.Content ?? string.Empty,
                request.Actor ?? string.Empty);

            var change = await service.AddDocumentAsync(command, cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Добавить документ: соглашение, уведомление, решение, приложение");

        group.MapDelete("/{id:guid}/documents/{documentId:guid}", async (
            Guid id,
            Guid documentId,
            string? actor,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var command = new RemoveContractChangeDocumentCommand(id, documentId, actor ?? string.Empty);
            var change = await service.RemoveDocumentAsync(command, cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Удалить документ из черновика");

        group.MapPost("/{id:guid}/submit", async (
            Guid id,
            ContractChangeActionRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.SubmitAsync(
                new ContractChangeActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Отправить на согласование");

        group.MapPost("/{id:guid}/approve", async (
            Guid id,
            ContractChangeActionRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.ApproveAsync(
                new ContractChangeActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Согласовать документ");

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            ContractChangeActionRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.RejectAsync(
                new ContractChangeActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Отклонить документ с указанием причины");

        group.MapPost("/{id:guid}/sign", async (
            Guid id,
            ContractChangeActionRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.SignAsync(
                new ContractChangeActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Подписать документ");

        group.MapPost("/{id:guid}/register", async (
            Guid id,
            RegisterContractChangeRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var command = new RegisterContractChangeCommand(
                id,
                request.Actor ?? string.Empty,
                request.RegistrationNumber ?? string.Empty,
                request.RegistrationDate);

            var change = await service.RegisterAsync(command, cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Зафиксировать регистрацию в ЕИС");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            ContractChangeActionRequest request,
            ContractChangeService service,
            CancellationToken cancellationToken) =>
        {
            var change = await service.CancelAsync(
                new ContractChangeActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractChangeResponse.From(change));
        })
        .Produces<ContractChangeResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Отменить незарегистрированный документ");

        return app;
    }
}
