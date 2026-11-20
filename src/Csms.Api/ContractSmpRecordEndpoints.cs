using System.Globalization;
using Csms.Application;
using Csms.Domain;

namespace Csms.Api;

public static class ContractSmpRecordEndpoints
{
    /// <summary>
    /// Маршруты сведений о привлечении СМП/СОНКО. Как и у изменений контракта,
    /// отдельного «поставить статус» нет: статус меняют только сценарии домена,
    /// поэтому правила ч. 30 44-ФЗ и ПП РФ № 1466 нельзя обойти одним PUT.
    /// </summary>
    public static IEndpointRouteBuilder MapContractSmpRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contract-smp-records")
            .WithTags("Привлечение СМП/СОНКО в реестре контрактов");

        group.MapGet("/", async (
            string? organizationId,
            string? contractId,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var records = await service.ListAsync(organizationId, contractId, cancellationToken);

            return Results.Ok(records.Select(ContractSmpRecordResponse.From));
        })
        .Produces<IEnumerable<ContractSmpRecordResponse>>()
        .WithSummary("Список сведений, свежие сверху");

        group.MapGet("/meta", () =>
        {
            var statuses = Enum.GetValues<ContractSmpStatus>()
                .Select(ContractSmpResponseText.Option)
                .ToList();

            var documentTypes = Enum.GetValues<ContractSmpDocumentType>()
                .Select(ContractSmpResponseText.Option)
                .ToList();

            var priceTypes = Enum.GetValues<ContractSmpPriceType>()
                .Select(ContractSmpResponseText.Option)
                .ToList();

            return Results.Ok(new ContractSmpMetaResponse(
                statuses,
                documentTypes,
                priceTypes,
                ContractSmpDeadlineCatalog.Rules,
                ContractSmpDeadlineCatalog.CustomerEisRule,
                ContractSmpDeadlineCatalog.MinimumRequiredPercent,
                ContractSmpDeadlineCatalog.EarlyPaymentWorkingDays));
        })
        .Produces<ContractSmpMetaResponse>()
        .WithSummary("Статусы, типы документов и сроки ПП РФ № 1466");

        group.MapGet("/volume", async (
            string contractId,
            string contractPrice,
            string? requiredPercent,
            string? overdueDays,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var price = ParseDecimal(contractPrice, "contractPrice");
            var percent = ParseDecimal(requiredPercent, "requiredPercent");
            var volume = await service.CalculateVolumeAsync(
                contractId, price, percent, cancellationToken);
            var days = string.IsNullOrWhiteSpace(overdueDays)
                ? 0
                : (int)ParseDecimal(overdueDays, "overdueDays");

            return Results.Ok(new ContractSmpVolumeResponse(
                volume,
                ContractSmpVolume.CalculatePenalty(volume, days)));
        })
        .Produces<ContractSmpVolumeResponse>()
        .WithSummary("Контроль объёма attracting и неустойка по контракту");

        group.MapPost("/validate", (
            ContractSmpRecordInputRequest request,
            ContractSmpRecordService service) =>
        {
            return Results.Ok(service.Validate(request.ToDomain()));
        })
        .Produces<IEnumerable<ContractSmpIssue>>()
        .WithSummary("Проверить сведения без сохранения");

        group.MapGet("/{id:guid}", async (
            Guid id,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.GetAsync(id, cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>();

        group.MapPost("/", async (
            ContractSmpRecordInputRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.CreateAsync(
                new CreateContractSmpRecordCommand(request.ToDomain()),
                cancellationToken);

            return Results.Created(
                $"/api/contract-smp-records/{record.Id}",
                ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>(StatusCodes.Status201Created)
        .WithSummary("Подать сведения о договоре с субподрядчиком СМП/СОНКО");

        async Task<IResult> UpdateAsync(
            Guid id,
            string? actor,
            ContractSmpRecordInputRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken)
        {
            var record = await service.UpdateAsync(
                new UpdateContractSmpRecordCommand(id, request.ToDomain(), actor ?? string.Empty),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        }

        group.MapPut("/{id:guid}", UpdateAsync)
            .Produces<ContractSmpRecordResponse>()
            .WithSummary("Изменить поданные или отклонённые сведения");
        group.MapPatch("/{id:guid}", UpdateAsync)
            .Produces<ContractSmpRecordResponse>()
            .WithSummary("Изменить поданные или отклонённые сведения");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            string? actor,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(id, cancellationToken);

            return Results.NoContent();
        })
        .WithSummary("Удалить неподтверждённые сведения");

        group.MapPost("/{id:guid}/documents", async (
            Guid id,
            AddContractSmpDocumentRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.AddDocumentAsync(
                new AddContractSmpDocumentCommand(
                    id,
                    request.Type,
                    request.Title ?? string.Empty,
                    request.Content ?? string.Empty,
                    request.Actor ?? string.Empty),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Приложить документ-основание");

        group.MapDelete("/{id:guid}/documents/{documentId:guid}", async (
            Guid id,
            Guid documentId,
            string? actor,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.RemoveDocumentAsync(
                new RemoveContractSmpDocumentCommand(id, documentId, actor ?? string.Empty),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Удалить документ-основание из черновика");

        group.MapPost("/{id:guid}/accept", async (
            Guid id,
            ContractSmpActionRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.AcceptAsync(
                new ContractSmpRecordActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Принять сведения заказчиком");

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            ContractSmpActionRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.RejectAsync(
                new ContractSmpRecordActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Отклонить сведения с указанием причины");

        group.MapPost("/{id:guid}/resubmit", async (
            Guid id,
            ContractSmpActionRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var record = await service.ResubmitAsync(
                new ContractSmpRecordActionCommand(id, request.Actor ?? string.Empty, request.Comment),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Вернуть отклонённые сведения в работу");

        group.MapPost("/{id:guid}/push-to-eis", async (
            Guid id,
            PushContractSmpRecordRequest request,
            ContractSmpRecordService service,
            CancellationToken cancellationToken) =>
        {
            var publishDate = ParseDate(request.PublishDate);

            var record = await service.PushToEisAsync(
                new PushContractSmpRecordCommand(
                    id,
                    request.Actor ?? string.Empty,
                    publishDate,
                    request.RecordNumber ?? string.Empty),
                cancellationToken);

            return Results.Ok(ContractSmpRecordResponse.From(record));
        })
        .Produces<ContractSmpRecordResponse>()
        .WithSummary("Зафиксировать выгрузку в карточку контракта ЕИС");

        return app;
    }

    private static decimal ParseDecimal(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0m;
        }

        if (decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new DomainException($"Параметр {field}: ожидается число.");
    }

    private static DateOnly ParseDate(string? value)
    {
        if (DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new DomainException("Укажите дату выгрузки в формате ГГГГ-ММ-ДД.");
    }
}

/// <summary>Приложить копию договора, декларацию, акт или платёжное поручение.</summary>
public sealed record AddContractSmpDocumentRequest(
    ContractSmpDocumentType Type,
    string? Title,
    string? Content,
    string? Actor);
