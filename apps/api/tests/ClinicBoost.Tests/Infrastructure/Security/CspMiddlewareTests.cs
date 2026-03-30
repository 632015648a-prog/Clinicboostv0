using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Tests.Infrastructure.Security;

/// <summary>
/// Tests unitarios para CspMiddleware.
/// Verifica que los headers de seguridad se añaden correctamente
/// para rutas de frontend (con nonce) y API (CSP mínima).
/// </summary>
public sealed class CspMiddlewareTests
{
    private static async Task<HttpContext> RunAsync(
        string path = "/dashboard",
        CspOptions? options = null,
        bool isHttps = false)
    {
        var opts = Options.Create(options ?? new CspOptions());

        var services = new ServiceCollection();
        services.AddScoped<CspNonceAccessor>();
        services.AddScoped<ICspNonceAccessor>(sp => sp.GetRequiredService<CspNonceAccessor>());
        var sp = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Request.Path    = path;
        ctx.Request.IsHttps = isHttps;
        if (isHttps) ctx.Request.Scheme = "https";

        var middleware = new CspMiddleware(_ => Task.CompletedTask, opts);
        await middleware.InvokeAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task Invoke_AddsCspHeader()
    {
        var ctx = await RunAsync("/api/test");
        ctx.Response.Headers.ContainsKey("Content-Security-Policy")
            .Should().BeTrue("las rutas API deben recibir cabecera CSP");
    }

    [Fact]
    public async Task Invoke_FrontendPaths_CspContainsNonce()
    {
        var ctx = await RunAsync("/dashboard");
        var csp = ctx.Response.Headers["Content-Security-Policy"].ToString();
        csp.Should().Contain("nonce-", "las páginas frontend deben incluir nonce en CSP");
    }

    [Fact]
    public async Task Invoke_ApiPaths_CspIsMinimal()
    {
        var ctx = await RunAsync("/api/patients");
        ctx.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Be("default-src 'none'");
    }

    [Fact]
    public async Task Invoke_AuthPaths_CspIsMinimal()
    {
        var ctx = await RunAsync("/auth/refresh");
        ctx.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Be("default-src 'none'");
    }

    [Fact]
    public async Task Invoke_NonceIsUniquePerRequest()
    {
        var ctx1 = await RunAsync("/app");
        var ctx2 = await RunAsync("/app");

        var csp1 = ctx1.Response.Headers["Content-Security-Policy"].ToString();
        var csp2 = ctx2.Response.Headers["Content-Security-Policy"].ToString();
        csp1.Should().NotBe(csp2, "cada petición debe recibir un nonce único");
    }

    [Fact]
    public async Task Invoke_Https_AddsHstsHeader()
    {
        var ctx = await RunAsync("/dashboard", isHttps: true);
        ctx.Response.Headers.ContainsKey("Strict-Transport-Security")
            .Should().BeTrue("HTTPS debe incluir HSTS");
    }

    [Fact]
    public async Task Invoke_Http_NoHstsHeader()
    {
        var ctx = await RunAsync("/dashboard", isHttps: false);
        ctx.Response.Headers.ContainsKey("Strict-Transport-Security")
            .Should().BeFalse("HTTP no debe incluir HSTS");
    }

    [Fact]
    public async Task Invoke_AddsXContentTypeOptions()
    {
        var ctx = await RunAsync();
        ctx.Response.Headers["X-Content-Type-Options"].ToString()
            .Should().Be("nosniff");
    }

    [Fact]
    public async Task Invoke_AddsXFrameOptions()
    {
        var ctx = await RunAsync();
        ctx.Response.Headers["X-Frame-Options"].ToString()
            .Should().Be("DENY");
    }

    [Fact]
    public async Task Invoke_AddsPermissionsPolicy()
    {
        var ctx = await RunAsync();
        var pp = ctx.Response.Headers["Permissions-Policy"].ToString();
        pp.Should().Contain("camera=()");
        pp.Should().Contain("microphone=()");
        pp.Should().Contain("geolocation=()");
    }

    [Fact]
    public async Task Invoke_AddsReferrerPolicy()
    {
        var ctx = await RunAsync();
        ctx.Response.Headers["Referrer-Policy"].ToString()
            .Should().Be("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task Invoke_AddsCrossOriginPolicies()
    {
        var ctx = await RunAsync("/dashboard");
        ctx.Response.Headers["Cross-Origin-Opener-Policy"].ToString()
            .Should().Be("same-origin");
        ctx.Response.Headers["Cross-Origin-Embedder-Policy"].ToString()
            .Should().Be("require-corp");
    }

    [Fact]
    public async Task Invoke_ReportOnlyMode_UsesReportOnlyHeader()
    {
        var ctx = await RunAsync("/app", new CspOptions { ReportOnly = true });
        ctx.Response.Headers.ContainsKey("Content-Security-Policy-Report-Only")
            .Should().BeTrue();
        ctx.Response.Headers.ContainsKey("Content-Security-Policy")
            .Should().BeFalse();
    }

    [Fact]
    public async Task Invoke_EnforceMode_UsesCspHeader()
    {
        var ctx = await RunAsync("/app", new CspOptions { ReportOnly = false });
        ctx.Response.Headers.ContainsKey("Content-Security-Policy")
            .Should().BeTrue();
        ctx.Response.Headers.ContainsKey("Content-Security-Policy-Report-Only")
            .Should().BeFalse();
    }

    [Fact]
    public async Task Invoke_NonceExposedViaCspNonceAccessor()
    {
        var ctx = await RunAsync("/dashboard");
        var accessor = ctx.RequestServices.GetRequiredService<ICspNonceAccessor>();
        accessor.Nonce.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Invoke_CspContainsUpgradeInsecureRequests()
    {
        var ctx = await RunAsync("/app");
        ctx.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Contain("upgrade-insecure-requests");
    }

    [Fact]
    public async Task Invoke_ConfiguredReportUriAppearsInCsp()
    {
        var opts = new CspOptions { ReportUri = "https://report.example.com/csp" };
        var ctx  = await RunAsync("/app", opts);
        ctx.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Contain("https://report.example.com/csp");
    }

    [Fact]
    public async Task Invoke_AdditionalConnectSrcAppearsInCsp()
    {
        var opts = new CspOptions
            { AdditionalConnectSrc = "https://api.example.com wss://realtime.example.com" };
        var ctx = await RunAsync("/app", opts);
        var csp = ctx.Response.Headers["Content-Security-Policy"].ToString();
        csp.Should().Contain("https://api.example.com");
        csp.Should().Contain("wss://realtime.example.com");
    }
}
