using System.Text.Json;
using Csms.Application;
using Microsoft.AspNetCore.Authorization;

namespace Csms.Api;

/// <summary>
/// Контракт для фронта. Login отдельно от /me: при первом рендере приложение
/// восстанавливает профиль по токену из localStorage, как и сейчас с email.
/// </summary>
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, AccessProfile Profile);
public sealed record SetRolesRequest(IReadOnlyList<string> RoleCodes);

public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/access").WithTags("Доступ и роли");

        group.MapPost("/login", async (
            LoginRequest request,
            AccessService service,
            CancellationToken cancellationToken) =>
        {
            var (token, profile) = await service.LoginAsync(
                request.Email, request.Password, cancellationToken);
            return Results.Ok(new LoginResponse(token, profile));
        })
        .Produces<LoginResponse>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .AllowAnonymous()
        .WithSummary("Войти по email и паролю, получить токен сессии и профиль прав");

        group.MapGet("/me", async (
            HttpContext context,
            AccessService service,
            CancellationToken cancellationToken) =>
        {
            var token = BearerToken(context);
            if (token is null) return Results.Unauthorized();

            var profile = await service.GetByTokenAsync(token, cancellationToken);
            return profile is null ? Results.Unauthorized() : Results.Ok(profile);
        })
        .Produces<AccessProfile>()
        .Produces(StatusCodes.Status401Unauthorized)
        .AllowAnonymous()
        .WithSummary("Профиль текущего пользователя и эффективные права");

        group.MapPost("/logout", async (
            HttpContext context,
            AccessService service,
            CancellationToken cancellationToken) =>
        {
            var token = BearerToken(context);
            if (token is not null)
                await service.LogoutAsync(token, cancellationToken);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .AllowAnonymous()
        .WithSummary("Завершить сессию");

        group.MapGet("/roles", async (
            AccessService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetRolesAsync(cancellationToken)))
        .Produces<IEnumerable<AccessRoleInfo>>()
        .WithSummary("Справочник ролей доступа и прав роли");

        group.MapGet("/permissions", async (
            AccessService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPermissionsAsync(cancellationToken)))
        .Produces<IEnumerable<AccessPermissionInfo>>()
        .WithSummary("Справочник прав доступа");

        group.MapPut("/users/{userKey}/roles", async (
            string userKey,
            SetRolesRequest request,
            AccessService service,
            CancellationToken cancellationToken) =>
        {
            await service.SetUserRolesAsync(userKey, request.RoleCodes, cancellationToken);
            var source = await service.GetUserAsync(userKey, cancellationToken);
            return source is null
                ? Results.NotFound()
                : Results.Ok(AccessService.BuildProfile(source));
        })
        .Produces<AccessProfile>()
        .ProducesProblem(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status404NotFound)
        .WithSummary("Назначить роли пользователю (явные роли поверх прав КС)");

        group.MapPost("/import", async (
            AccessImportRequest request,
            AccessService service,
            CancellationToken cancellationToken) =>
        {
            await service.ImportAsync(request, cancellationToken);
            return Results.Ok(new { imported = true });
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithSummary("Импортировать пользователей, организации и КС из db.json");

        return app;
    }

    /// <summary>
    /// /me и /logout читают токен напрямую: эти методы уже доступны анонимно,
    /// чтобы вернуть 401 JSON вместо редиректа на вызов схемы авторизации.
    /// </summary>
    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            header.Length > prefix.Length
                ? header[prefix.Length..].Trim()
                : null;
    }
}
