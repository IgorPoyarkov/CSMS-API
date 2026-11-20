using System.Text.Json.Serialization;
using Csms.Api;
using Microsoft.AspNetCore.Authentication;
using Csms.Application;
using Csms.Infrastructure;
using Csms.Infrastructure.Documents;
using Csms.Infrastructure.Liability;
using Csms.Infrastructure.Npa;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Статусы и типы документов отдаём строками ("Signed", "SmpAttraction"):
// так фронт читает их глазами и не зависит от порядка значений в enum
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// В разработке фронт живёт на другом порту Vite (5173, а если он занят —
// 5174 и далее), поэтому браузеру нужно разрешение. Список origin-ов
// точечный, а не «любой»: иначе первый же открытый чужой сайт смог бы
// дёргать API пакетов от имени заказчика. Origins можно расширить через
// Cors:AllowedOrigins или CORS_ALLOWED_ORIGINS (через запятую).
builder.Services.AddFrontendCors(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

// Авторизация пока не включает RequireAuthorization: контур доступа вводится
// параллельно текущему json-server-режиму. standalone-эндпоинты /api/access
// сами проверяют токен и возвращают 401 в формате JSON.
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddSingleton(TimeProvider.System);

// Хранилище выбирается переключателем: по умолчанию память, чтобы тесты и
// локальный запуск работали без поднятой БД. С ЕИС_STORE=postgres сервис
// честно падает, если строка подключения не задана, — молча уходить в память
// нельзя: пакеты тогда терялись бы при каждом перезапуске незаметно для всех
var store = (builder.Configuration["EIS_STORE"] ?? "memory").Trim().ToLowerInvariant();

if (store == "postgres")
{
    var connectionString = builder.Configuration.GetConnectionString("EisDb");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "EIS_STORE=postgres, но строка подключения ConnectionStrings:EisDb не задана. " +
            "Задайте её: dotnet user-secrets set \"ConnectionStrings:EisDb\" \"Host=...;Database=...;Username=...\" --project src/Csms.Api");
    }

    builder.Services.AddDbContextFactory<EisDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddSingleton<IEisPackageRepository, EfCoreEisPackageRepository>();
    builder.Services.AddSingleton<IReferenceDataStore, EfCoreReferenceDataStore>();
    builder.Services.AddSingleton<IAccessStore, EfCoreAccessStore>();
    builder.Services.AddSingleton<INpaRuleStore, EfCoreNpaRuleStore>();
    builder.Services.AddSingleton<ILiabilityMappingStore, EfCoreLiabilityMappingStore>();
    builder.Services.AddSingleton<ILiabilityRetriever, EfCoreLiabilityRetriever>();
}
else
{
    builder.Services.AddSingleton<IEisPackageRepository, InMemoryEisPackageRepository>();
    builder.Services.AddSingleton<IReferenceDataStore, InMemoryReferenceDataStore>();
    builder.Services.AddSingleton<IAccessStore, InMemoryAccessStore>();
    builder.Services.AddSingleton<INpaRuleStore, InMemoryNpaRuleStore>();
    // В memory-режиме нет pgvector: пул пуст, но конвейер и эндпоинт живут.
    builder.Services.AddSingleton<ILiabilityMappingStore, InMemoryLiabilityMappingStore>();
    builder.Services.AddSingleton<ILiabilityRetriever, EmptyLiabilityRetriever>();
}

// Импорт справочников рассылается фронту отдельным SSE-потоком
var referenceEvents = new ReferenceDataEventBroker();
builder.Services.AddSingleton(referenceEvents);

builder.Services.AddSingleton<IEisDocumentXmlBuilder, PlaceholderXmlBuilder>();

// Папку схем можно переопределить переменной EIS_SCHEMAS_ROOT. По умолчанию
// схемы ищутся рядом с выполненной сборкой: schemas/<версия>/*.xsd.
// Один и тот же экземпляр каталога нужен обновлятору, чтобы сбросить кеш
// сразу после подмены файлов версии.
var schemaRoot = builder.Configuration["EIS_SCHEMAS_ROOT"];
var schemaCatalog = new EisSchemaCatalog(schemaRoot);
builder.Services.AddSingleton(schemaCatalog);
builder.Services.AddSingleton<IEisSchemaCatalog>(schemaCatalog);

// Каталог документации строится из тех же XSD и живёт в API-процессе:
// фронт читает его через API и получает SSE при смене хеша.
var docCatalog = new EisSchemaDocCatalog(schemaRoot);
docCatalog.Initialize();
var schemaEvents = new EisSchemaEventBroker();
builder.Services.AddSingleton(docCatalog);
builder.Services.AddSingleton(schemaEvents);

// Автообновление Альбома форматов: страница документов → ZIP → проверка XSD →
// атомарная подмена папки версии. Настройки читаются из EisSchemas:Update.
var schemaOptions = new EisSchemaSourceOptions();
builder.Configuration.GetSection(EisSchemaSourceOptions.SectionName).Bind(schemaOptions);
schemaOptions.Validate();
builder.Services.AddSingleton(schemaOptions);
builder.Services.AddSingleton<EisSchemaUpdateStatus>();

builder.Services.AddHttpClient("eis-schemas", client =>
{
    client.Timeout = TimeSpan.FromSeconds(schemaOptions.HttpTimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Csms.EisSchemaUpdater/1.0");
});

builder.Services.AddSingleton<EisSchemaDownloader>(provider => new EisSchemaDownloader(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("eis-schemas"),
    schemaOptions,
    provider.GetRequiredService<EisSchemaUpdateStatus>(),
    provider.GetRequiredService<ILogger<EisSchemaDownloader>>(),
    schemaRoot,
    (version, _) =>
    {
        schemaCatalog.Invalidate(version);

        var change = docCatalog.Regenerate(version);
        if (change is not null)
        {
            schemaEvents.Publish(change);
        }

        return Task.CompletedTask;
    }));
builder.Services.AddHostedService<EisSchemaUpdateWorker>();

// Импорт справочников НСИ ходит на официальный сайт ЕИС только по запросу.
// Таймаут увеличен: дерево ОКПД2 обходится сотнями мелких вызовов children.html.
builder.Services.AddHttpClient("eis-nsi", client =>
{
    client.BaseAddress = new Uri("https://zakupki.gov.ru");
    client.Timeout = TimeSpan.FromSeconds(180);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.DefaultRequestHeaders.Add("User-Agent", "Csms.ReferenceDataImporter/1.0");
});
builder.Services.AddSingleton<EisNsiTreeImporter>(provider => new EisNsiTreeImporter(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("eis-nsi"),
    provider.GetRequiredService<ILogger<EisNsiTreeImporter>>()));

builder.Services.AddSingleton<IEisTransport, MockEisTransport>();
builder.Services.AddSingleton<EisPackageService>();
builder.Services.AddSingleton<AccessService>();

// Агент разметки НПА: официальный текст акта → правила по кодам ОКПД2.
// Настройки в разделе Npa; Tesseract ищется при первом запуске, чтобы сервис
// поднимался и без него.
var npaOptions = new NpaOptions();
builder.Configuration.GetSection(NpaOptions.SectionName).Bind(npaOptions);
npaOptions.Validate();
builder.Services.AddSingleton(npaOptions);
builder.Services.AddSingleton(npaOptions.Ocr);

builder.Services.AddHttpClient("pravo-api", client =>
{
    client.BaseAddress = new Uri("http://publication.pravo.gov.ru");
    client.Timeout = TimeSpan.FromSeconds(180);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Csms.NpaAgent/1.0");
    // Портал отдаёт 500.30, если в запросе есть Accept-Encoding: gzip/br.
    // Curl без этого заголовка получает 200, поэтому явно просим identity.
    client.DefaultRequestHeaders.AcceptEncoding.Clear();
    client.DefaultRequestHeaders.AcceptEncoding.Add(
        new System.Net.Http.Headers.StringWithQualityHeaderValue("identity"));
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.None,
});

builder.Services.AddSingleton(provider => new PravoDocumentClient(
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("pravo-api")));
builder.Services.AddSingleton<INpaOcrEngine, TesseractOcrEngine>();
builder.Services.AddSingleton<NpaLayoutParser>();
builder.Services.AddSingleton<NpaPdfParser>();

// Второй агент (локальная модель) подключается к OpenAI-совместимому эндпоинту
// Ollama. Адрес и модель задаются в Npa:Llm, ключи не нужны.
builder.Services.AddSingleton<NpaLlmAnalyzer>();
builder.Services.AddSingleton<NpaSyncService>();
builder.Services.AddHostedService<NpaSyncWorker>();

// Агент извлечения функций: настройки отдельные, чтобы не зависить от
// Npa:Llm:Enabled. Модель та же, локальная, но вызывается только по запросу.
var documentFunctionsOptions = new DocumentFunctionsOptions();
builder.Configuration.GetSection(DocumentFunctionsOptions.SectionName)
    .Bind(documentFunctionsOptions);
documentFunctionsOptions.Validate();
builder.Services.AddSingleton(documentFunctionsOptions);
builder.Services.AddSingleton<IDocumentFunctionStore, InMemoryDocumentFunctionStore>();
builder.Services.AddSingleton<DocumentSegmenter>();
builder.Services.AddSingleton<DocumentTextExtractor>();
builder.Services.AddSingleton<DocumentFunctionExtractor>();
builder.Services.AddSingleton<DocumentFunctionValidator>();
builder.Services.AddSingleton<DocumentFunctionReducer>();
builder.Services.AddSingleton<DocumentExtractionService>();

// Compliance & Liability Mapping: арбитр и эмбеддинги — та же локальная
// Ollama; пул кандидатов живёт в PostgreSQL (схема legal) при EIS_STORE=postgres.
var liabilityOptions = new LiabilityMappingOptions();
builder.Configuration.GetSection(LiabilityMappingOptions.SectionName)
    .Bind(liabilityOptions);
liabilityOptions.Validate();
builder.Services.AddSingleton(liabilityOptions);
builder.Services.AddSingleton<ILlmCompletionClient, OllamaCompletionClient>();
builder.Services.AddSingleton<ILiabilityEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddSingleton<IViolationHypothesisGenerator, ViolationHypothesisGenerator>();
builder.Services.AddSingleton<IMappingValidator, MappingValidator>();
builder.Services.AddSingleton<ILlmArbiter, LlmArbiter>();
builder.Services.AddSingleton<IDutyLiabilityMappingService, DutyLiabilityMappingService>();

// Пул 158н — версионируемый справочник в хранилище НСИ (ref.datasets),
// поэтому регистрация одна для memory и postgres: каталог сам наполнит БД
// эталоном, если активной версии ещё нет.
builder.Services.AddSingleton<IOrder158nPoolCatalog, Order158nPoolCatalog>();

var app = builder.Build();

app.UseExceptionHandler();
// UseCors обязан стоять до авторизации и эндпоинтов: иначе preflight OPTIONS
// уйдёт в авторизацию и вернётся без Access-Control-Allow-Origin
app.UseCors(FrontendCors.PolicyName);
app.UseAuthentication();
app.UseAuthorization();

// Контракт для фронта: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapEisPackageEndpoints();
app.MapEisSchemaEndpoints();
app.MapReferenceDataEndpoints();
app.MapNpaEndpoints();
app.MapDocumentFunctionEndpoints();
app.MapOrder158nPoolEndpoints();
app.MapLiabilityMappingEndpoints();
app.MapAccessEndpoints();

app.Run();
