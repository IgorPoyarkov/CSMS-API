using System.Text.Json.Serialization;
using Csms.Api;
using Csms.Application;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore;

const string FrontendPolicy = "frontend";

var builder = WebApplication.CreateBuilder(args);

// Статусы и типы документов отдаём строками ("Signed", "SmpAttraction"):
// так фронт читает их глазами и не зависит от порядка значений в enum
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// В разработке фронт живёт на другом порту Vite, поэтому браузеру нужно
// разрешение. Список origin-ов точечный, а не «любой»: иначе первый же
// открытый чужой сайт смог бы дёргать API пакетов от имени заказчика
builder.Services.AddCors(options => options.AddPolicy(FrontendPolicy, policy => policy
    .WithOrigins("http://localhost:5173", "http://localhost:4173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddOpenApi();

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
}
else
{
    builder.Services.AddSingleton<IEisPackageRepository, InMemoryEisPackageRepository>();
}

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

builder.Services.AddSingleton<IEisTransport, MockEisTransport>();
builder.Services.AddSingleton<EisPackageService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors(FrontendPolicy);

// Контракт для фронта: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapEisPackageEndpoints();
app.MapEisSchemaEndpoints();

app.Run();
