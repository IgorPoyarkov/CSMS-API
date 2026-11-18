using System.Text.Json.Serialization;
using Csms.Api;
using Csms.Application;
using Csms.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Статусы и типы документов отдаём строками ("Signed", "SmpAttraction"):
// так фронт читает их глазами и не зависит от порядка значений в enum
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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

// Контракт для фронта: /openapi/v1.json
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapEisPackageEndpoints();

app.Run();
