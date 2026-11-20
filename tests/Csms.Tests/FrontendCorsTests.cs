using Csms.Api;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Csms.Tests;

/// <summary>
/// Проверки CORS для Vite: фронт на 5174 не должен получать блокировку
/// браузера, а политика не должна смешивать wildcard с credentials.
/// </summary>
public class FrontendCorsTests
{
    private static IConfiguration BuildConfiguration(
        Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? [])
            .Build();

    [Fact]
    public void DefaultOrigins_IncludeVitePorts()
    {
        var origins = FrontendCors.ResolveOrigins(BuildConfiguration());

        Assert.Contains("http://localhost:5173", origins);
        Assert.Contains("http://localhost:5174", origins);
        Assert.Contains("http://127.0.0.1:5174", origins);
    }

    [Fact]
    public void Configuration_ExtendsOriginsAndNormalizesTrailingSlash()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "http://localhost:6000",
            ["CORS_ALLOWED_ORIGINS"] = "http://localhost:6001, http://localhost:6002/",
        });

        var origins = FrontendCors.ResolveOrigins(configuration);

        Assert.Contains("http://localhost:6000", origins);
        Assert.Contains("http://localhost:6001", origins);
        Assert.Contains("http://localhost:6002", origins);
        Assert.DoesNotContain(origins, origin => origin.Contains('*'));
    }

    [Fact]
    public void Policy_DoesNotAllowWildcardWithCredentials()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrontendCors(BuildConfiguration());

        using var provider = services.BuildServiceProvider();
        var policy = provider
            .GetRequiredService<IOptions<CorsOptions>>()
            .Value
            .GetPolicy(FrontendCors.PolicyName);

        Assert.NotNull(policy);
        Assert.False(policy!.SupportsCredentials);
        Assert.Contains("http://localhost:5174", policy.Origins);
        Assert.DoesNotContain("*", policy.Origins);
    }
}
