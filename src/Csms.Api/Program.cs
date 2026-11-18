using System.Text.Json.Serialization;
using Csms.Api;
using Csms.Application;
using Csms.Infrastructure;

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
builder.Services.AddSingleton<IEisPackageRepository, InMemoryEisPackageRepository>();
builder.Services.AddSingleton<IEisDocumentXmlBuilder, PlaceholderXmlBuilder>();
builder.Services.AddSingleton<IEisTransport, MockEisTransport>();
builder.Services.AddSingleton<EisPackageService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors(FrontendPolicy);

// Контракт для фронта: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapEisPackageEndpoints();

app.Run();
