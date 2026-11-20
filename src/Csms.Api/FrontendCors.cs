namespace Csms.Api;

/// <summary>
/// CORS для фронтенда в разработке.
///
/// Vite поднимается на 5173, а если порт занят — на 5174 и далее. Список
/// origin-ов точечный: wildcard вместе с credentials браузер запрещает, а
/// разрешать «любой» origin для API заказчика небезопасно. Дополнительные
/// origin-ы можно передать через Cors:AllowedOrigins или переменную
/// окружения CORS_ALLOWED_ORIGINS (через запятую).
/// </summary>
public static class FrontendCors
{
    public const string PolicyName = "frontend";

    private static readonly string[] DefaultOrigins =
    [
        "http://localhost:5173",
        "http://localhost:5174",
        "http://127.0.0.1:5173",
        "http://127.0.0.1:5174",
        "http://localhost:4173",
        "http://localhost:4174",
    ];

    public static string[] ResolveOrigins(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var fromEnvironment = (configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return configured
            .Concat(fromEnvironment)
            .Concat(DefaultOrigins)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IServiceCollection AddFrontendCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var origins = ResolveOrigins(configuration);

        services.AddCors(options => options.AddPolicy(PolicyName, policy => policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            // Фронт не использует cookie/HTTP-аутентификацию: токен ходит
            // в заголовке Authorization. Credentials не включаем, чтобы не
            // смешивать их с wildcard и не открывать cookie-сценарии.
            .DisallowCredentials()));

        return services;
    }
}
