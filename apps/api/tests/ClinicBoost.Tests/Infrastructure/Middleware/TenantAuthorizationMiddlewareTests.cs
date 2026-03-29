using System.Text.Json;
using ClinicBoost.Api.Infrastructure.Middleware;
using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Infrastructure.Middleware;

/// <summary>
/// Tests del TenantAuthorizationMiddleware.
/// Verifica que los endpoints con [RequireRole] devuelven 401/403
/// según el contexto del tenant.
/// </summary>
public sealed class TenantAuthorizationMiddlewareTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static HttpContext BuildContext(
        ITenantContext tenantCtx,
        RequireRoleAttribute? requireRole = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tenantCtx);
        services.AddScoped<ITenantContext>(_ => tenantCtx);

        var httpCtx      = new DefaultHttpContext();
        httpCtx.RequestServices = services.BuildServiceProvider();
        httpCtx.Response.Body   = new MemoryStream();

        if (requireRole is not null)
        {
            // Simular endpoint con metadata [RequireRole]
            var endpointMetadata = new EndpointMetadataCollection(requireRole);
            var endpoint         = new Endpoint(_ => Task.CompletedTask, endpointMetadata, "Test");
            httpCtx.Features.Set<IEndpointFeature>(new EndpointFeature(endpoint));
        }

        return httpCtx;
    }

    private static TenantContext InitializedContext(
        string role, Guid? tenantId = null, Guid? userId = null)
    {
        var ctx = new TenantContext();
        ctx.Initialize(tenantId ?? Guid.NewGuid(), role, userId);
        return ctx;
    }

    private static TenantContext UninitializedContext() => new TenantContext();

    private static TenantAuthorizationMiddleware BuildMiddleware(bool nextCalled = false)
    {
        var called = false;
        return new TenantAuthorizationMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            NullLogger<TenantAuthorizationMiddleware>.Instance);
    }

    private static async Task<(int status, string body)> InvokeAsync(
        TenantAuthorizationMiddleware mw,
        HttpContext ctx)
    {
        await mw.InvokeAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    // ── Sin [RequireRole]: siempre pasa ────────────────────────────────────────

    [Fact]
    public async Task Invoke_NoRequireRoleMetadata_CallsNext()
    {
        var nextCalled = false;
        var mw         = new TenantAuthorizationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantAuthorizationMiddleware>.Instance);

        var httpCtx = BuildContext(UninitializedContext(), requireRole: null);

        await mw.InvokeAsync(httpCtx);

        nextCalled.Should().BeTrue();
        httpCtx.Response.StatusCode.Should().Be(200);
    }

    // ── Contexto no inicializado + [RequireRole] → 401 ───────────────────────

    [Fact]
    public async Task Invoke_UninitializedContext_WithRequireRole_Returns401()
    {
        var mw      = BuildMiddleware();
        var httpCtx = BuildContext(UninitializedContext(), new RequireRoleAttribute("admin"));

        var (status, body) = await InvokeAsync(mw, httpCtx);

        status.Should().Be(401);
        body.Should().Contain("SEC-401");
        body.Should().Contain("authenticated");
    }

    // ── Rol insuficiente → 403 ────────────────────────────────────────────────

    [Theory]
    [InlineData("therapist",    "admin")]
    [InlineData("receptionist", "admin")]
    [InlineData("service",      "therapist")]
    [InlineData("admin",        "owner")]
    public async Task Invoke_InsufficientRole_Returns403(string userRole, string requiredRole)
    {
        var mw      = BuildMiddleware();
        var httpCtx = BuildContext(
            InitializedContext(userRole),
            new RequireRoleAttribute(requiredRole));

        var (status, body) = await InvokeAsync(mw, httpCtx);

        status.Should().Be(403);
        body.Should().Contain("SEC-403");
        body.Should().Contain(requiredRole);
        body.Should().Contain(userRole);
    }

    // ── Rol suficiente → pasa al siguiente ────────────────────────────────────

    [Theory]
    [InlineData("owner",  "owner")]
    [InlineData("owner",  "admin")]
    [InlineData("admin",  "admin")]
    [InlineData("admin",  "therapist")]
    [InlineData("owner",  "service")]
    public async Task Invoke_SufficientRole_CallsNext(string userRole, string requiredRole)
    {
        var nextCalled = false;
        var mw         = new TenantAuthorizationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantAuthorizationMiddleware>.Instance);

        var httpCtx = BuildContext(
            InitializedContext(userRole),
            new RequireRoleAttribute(requiredRole));

        await mw.InvokeAsync(httpCtx);

        nextCalled.Should().BeTrue();
        httpCtx.Response.StatusCode.Should().Be(200);
    }

    // ── La respuesta de error es JSON válido con Problem Details ─────────────

    [Fact]
    public async Task Invoke_Error_ResponseIsValidProblemJson()
    {
        var mw      = BuildMiddleware();
        var httpCtx = BuildContext(
            InitializedContext("therapist"),
            new RequireRoleAttribute("owner"));

        var (_, body) = await InvokeAsync(mw, httpCtx);

        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(403);
        doc.RootElement.GetProperty("code").GetString().Should().Be("SEC-403");
        doc.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        httpCtx.Response.ContentType.Should().Contain("application/problem+json");
    }

    // ── Helper interno: IEndpointFeature ─────────────────────────────────────

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
        public EndpointFeature(Endpoint ep) => Endpoint = ep;
    }
}
