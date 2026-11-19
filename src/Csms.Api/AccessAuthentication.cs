using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Csms.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Csms.Api;

/// <summary>
/// Схема авторизации через токен сессии, который сервер выдаёт при логине.
/// Использует стандартный Bearer-заголовок, поэтому фронту достаточно обычного
/// Authorization: Bearer … без кастомных заголовков.
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "SessionBearer";

    private readonly IAccessStore _store;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccessStore store)
        : base(options, logger, encoder)
    {
        _store = store;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) || header.Count == 0)
            return AuthenticateResult.NoResult();

        if (!AuthenticationHeaderValue.TryParse(header.ToString(), out var parsed) ||
            !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parsed.Parameter))
            return AuthenticateResult.NoResult();

        var source = await _store.FindBySessionAsync(parsed.Parameter, Context.RequestAborted);
        if (source is null)
            return AuthenticateResult.Fail("Сессия не найдена или истекла.");

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, source.UserKey),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, source.Email),
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, SchemeName);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
