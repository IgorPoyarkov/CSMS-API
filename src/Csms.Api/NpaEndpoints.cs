using System.Text.Json;
using Csms.Application;
using Csms.Infrastructure;
using Csms.Infrastructure.Npa;

namespace Csms.Api;

/// <summary>
/// Правила по официальным актам (471-р, 1875, 2604 и другие), разобранные
/// агентом из текста публикации. Чтение — из БД, обновление — отдельным POST:
/// агент сам скачивает официальный текст и публикует новую версию правил.
/// </summary>
public static class NpaEndpoints
{
    public static IEndpointRouteBuilder MapNpaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/npa").WithTags("Правила НПА по ОКПД2");

        // Какие акты агент ведёт: фронт показывает список ещё до первой загрузки.
        group.MapGet("/config", (NpaOptions options) => Results.Ok(new
        {
            enabled = options.Enabled,
            checkTime = options.CheckTime,
            checkDelayMinutes = options.CheckDelayMinutes,
            timeZone = "UTC+3 (Москва)",
            autoPromote = options.AutoPromote,
            minAutoConfidence = options.MinAutoConfidence,
            llm = new
            {
                enabled = options.Llm.Enabled,
                endpoint = options.Llm.Endpoint,
                model = options.Llm.Model,
            },
            documents = options.Documents.Select(document => new
            {
                code = document.Code,
                title = document.Title,
                number = document.Number,
                eoNumber = document.EoNumber,
                source = document.File is null ? "publication.pravo.gov.ru" : document.File,
            }),
        }))
        .WithSummary("Акты, которые размечает агент");

        group.MapGet("/documents", async (INpaRuleStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetDocumentsAsync(cancellationToken)))
            .Produces<IReadOnlyList<NpaDocumentInfo>>()
            .WithSummary("Версии правил по всем актам");

        group.MapGet("/documents/{code}", async (
            string code,
            INpaRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var active = await store.GetActiveAsync(code, cancellationToken);
            return active is null ? Results.NotFound() : Results.Ok(active);
        })
        .Produces<NpaDocumentInfo>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Активная версия правил по акту");

        group.MapGet("/documents/{code}/rules", async (
            string code,
            string? search,
            string? status,
            int? skip,
            int? take,
            bool? directOnly,
            NpaOptions options,
            INpaRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var rules = await store.GetRulesAsync(
                code,
                search,
                status,
                skip ?? 0,
                take ?? options.DefaultPageSize,
                cancellationToken,
                directOnly ?? false);

            return Results.Ok(rules);
        })
        .Produces<IReadOnlyList<NpaRuleInfo>>()
        .WithSummary("Правила активной версии: поиск и фильтр по статусу");

        // Главный запрос фронта при проверке закупки: действует ли ограничение
        // для конкретного кода ОКПД2.
        group.MapGet("/documents/{code}/effective", async (
            string code,
            string okpd2Code,
            bool? includePending,
            INpaRuleStore store,
            CancellationToken cancellationToken) =>
        {
            var rule = await store.ResolveAsync(
                code,
                okpd2Code,
                includePending ?? false,
                cancellationToken);

            return rule is null ? Results.NotFound() : Results.Ok(rule);
        })
        .Produces<NpaEffectiveRule>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Действующее правило для кода ОКПД2 (ближайший предок)");

        // Второй агент: разбор спорных строк локальной моделью. Ничего не
        // публикует — предложения возвращаются человеку и попадают в правила
        // только после /verify или при следующей синхронизации с Npa:Llm:Enabled.
        group.MapPost("/documents/{code}/analyze", async (
            string code,
            NpaOptions options,
            INpaRuleStore store,
            IReferenceDataStore references,
            NpaLlmAnalyzer analyzer,
            CancellationToken cancellationToken) =>
        {
            if (!options.Llm.Enabled)
            {
                return Results.BadRequest(new
                {
                    error = "LLM-аналитик выключен (Npa:Llm:Enabled=false).",
                });
            }

            var active = await store.GetActiveAsync(code, cancellationToken);
            if (active is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(active.UnresolvedJson))
            {
                return Results.Ok(new NpaLlmAnalysisResult(code, options.Llm.Model, 0, 0, 0, [], null));
            }

            var rows = JsonSerializer.Deserialize<List<NpaUnresolvedRow>>(
                           active.UnresolvedJson,
                           new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var items = await references.GetAllAsync("nsiOKPD2", cancellationToken);
            var catalog = Okpd2Catalog.FromItems(items);
            var request = NpaLlmRuleAdapter.BuildRequest(
                code,
                active.Title,
                active.Number,
                rows,
                catalog,
                options.Llm.MaxCandidates);

            return Results.Ok(await analyzer.AnalyzeAsync(request, cancellationToken));
        })
        .Produces<NpaLlmAnalysisResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Разобрать спорные строки акта локальной моделью (без публикации)");

        group.MapPost("/documents/{code}/sync", async (
            string code,
            bool? force,
            bool? dryRun,
            NpaSyncService sync,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await sync.SyncAsync(
                    code,
                    force ?? false,
                    dryRun ?? false,
                    cancellationToken);

                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(
                    title: "Не удалось обновить правила",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
        })
        .Produces<NpaSyncResult>()
        .WithSummary("Скачать официальный текст, разобрать и опубликовать правила");

        // Своя редакция в Word/PDF: когда у заказчика есть консолидированный текст.
        group.MapPost("/documents/{code}/upload", async (
            string code,
            HttpContext context,
            bool? force,
            bool? dryRun,
            NpaSyncService sync,
            CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Ожидается multipart/form-data с файлом." });
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Файл не передан (поле file)." });
            }

            if (file.Length > 32 * 1024 * 1024)
            {
                return Results.BadRequest(new { error = "Файл больше 32 МБ." });
            }

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            try
            {
                var result = await sync.ImportFileAsync(
                    code,
                    buffer.ToArray(),
                    file.FileName,
                    force ?? false,
                    dryRun ?? false,
                    cancellationToken);

                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
        .DisableAntiforgery()
        .Produces<NpaSyncResult>()
        .WithSummary("Загрузить свою редакцию акта (.pdf/.docx) и разметить её");

        group.MapPost("/documents/{code}/rules/{ruleId:guid}/verify", async (
            string code,
            Guid ruleId,
            NpaVerification body,
            INpaRuleStore store,
            CancellationToken cancellationToken) =>
        {
            if (body.Status is not (NpaRuleStatuses.Approved or NpaRuleStatuses.Rejected or NpaRuleStatuses.NeedsReview))
            {
                return Results.BadRequest(new
                {
                    error = "status должен быть approved, rejected или needs_review.",
                });
            }

            var rule = await store.VerifyAsync(code, ruleId, body.Status, body.Note, cancellationToken);
            return rule is null ? Results.NotFound() : Results.Ok(rule);
        })
        .Produces<NpaRuleInfo>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithSummary("Подтвердить или отклонить спорное правило");

        return app;
    }

    /// <summary>Тело подтверждения правила человеком.</summary>
    public sealed record NpaVerification(string Status, string? Note);
}
