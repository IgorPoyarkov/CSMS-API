using Csms.Infrastructure;

namespace Csms.Api;

/// <summary>Выгрузка аннулирования контракта в ЕИС (часть Б10).</summary>
public static class ContractCancelEisPackageEndpoints
{
    public static IEndpointRouteBuilder MapContractCancelEisPackageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contract-changes").WithTags("Выгрузка расторжения в ЕИС");

        group.MapPost("/{id:guid}/eis-package", async (
            Guid id,
            ContractCancelPackageInput request,
            ContractCancelEisPackageBuilder builder,
            CancellationToken cancellationToken) =>
        {
            var package = await builder.CreateAsync(id, request, cancellationToken);
            return Results.Created($"/api/eis/packages/{package.Id}", EisPackageResponse.From(package));
        })
        .Produces<EisPackageResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Собрать и проверить пакет contractCancel по расторжению контракта");

        return app;
    }
}
