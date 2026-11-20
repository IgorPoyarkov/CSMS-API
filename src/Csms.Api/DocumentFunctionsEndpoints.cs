using Csms.Application;
using Csms.Infrastructure.Documents;

namespace Csms.Api;

/// <summary>
/// Агент извлечения функций и атомарных подзадач из произвольного документа.
/// Загрузка файла — по запросу, результат по умолчанию уходит в needs_review.
/// </summary>
public static class DocumentFunctionsEndpoints
{
    public static IEndpointRouteBuilder MapDocumentFunctionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents")
            .WithTags("Функции из документов");

        group.MapGet("/config", (DocumentFunctionsOptions options) => Results.Ok(new
        {
            endpoint = options.Endpoint,
            model = options.Model,
            batchSize = options.BatchSize,
            timeoutSeconds = options.TimeoutSeconds,
            maxOutputTokens = options.MaxOutputTokens,
            taxonomyVersion = DocumentFunctionTaxonomy.Version,
            promptVersion = DocumentFunctionPromptVersions.Map,
            taxonomy = DocumentFunctionTaxonomy.Codes,
        }))
        .WithSummary("Настройки агента и версия таксономии");

        group.MapPost("/extract", async (
            HttpContext context,
            bool? dryRun,
            DocumentExtractionService service,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Ожидается multipart/form-data с файлом." });

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Файл не передан (поле file)." });
            if (file.Length > 32 * 1024 * 1024)
                return Results.BadRequest(new { error = "Файл больше 32 МБ." });

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            var title = form["title"].Count > 0 ? form["title"][0] : null;
            var number = form["number"].Count > 0 ? form["number"][0] : null;
            var result = await service.ExtractAsync(
                buffer.ToArray(),
                file.FileName,
                dryRun ?? false,
                title,
                number,
                cancellationToken);
            return Results.Ok(result);
        })
        .DisableAntiforgery()
        .Produces<DocumentExtractionResponse>()
        .WithSummary("Разобрать файл: .docx/.pdf/.txt/.md");

        group.MapGet("/{sha256}/functions", async (
            string sha256,
            IDocumentFunctionStore store,
            CancellationToken cancellationToken) =>
        {
            var set = await store.GetActiveAsync(sha256, cancellationToken);
            return set is null
                ? Results.NotFound()
                : Results.Ok(set.Functions);
        })
        .Produces<IReadOnlyList<DocumentFunctionItem>>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Активный набор функций по документу");

        group.MapGet("/{sha256}/report", async (
            string sha256,
            IDocumentFunctionStore store,
            CancellationToken cancellationToken) =>
        {
            var versions = await store.GetVersionsAsync(sha256, cancellationToken);
            return versions.Count == 0
                ? Results.NotFound()
                : Results.Ok(new
                {
                    versions = versions.Count,
                    active = versions[^1].Version,
                    report = versions[^1].Report,
                });
        })
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Отчёт о покрытии сегментов и расхождениях");

        group.MapPost("/functions/{id:guid}/verify", async (
            Guid id,
            DocumentVerification body,
            IDocumentFunctionStore store,
            CancellationToken cancellationToken) =>
        {
            if (body.Status is not (
                DocumentFunctionReviewStatuses.Approved or
                DocumentFunctionReviewStatuses.Rejected))
            {
                return Results.BadRequest(new
                {
                    error = "status должен быть approved или rejected.",
                });
            }

            var updated = await store.VerifyAsync(
                id, body.Status, body.Note, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .Produces<DocumentFunctionItem>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Подтвердить или отклонить конкретную функцию");

        return group;
    }

    public sealed record DocumentVerification(string Status, string? Note);
}
