using System.Security.Claims;
using ClinicBoost.Api.Infrastructure.Middleware;
using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Infrastructure.Middleware;

/// <summary>
/// Tests unitarios de TenantMiddleware.
/// Verifica que el middleware extrae correctamente los claims y
/// no inicializa el contexto cuando los claims son inválidos.
/// </summary>
public sealed class TenantMiddlewareTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (TenantMiddleware middleware, TenantContext ctx, HttpContext httpCtx)
        Build(ClaimsPrincipal? principal = null)
    {
        // DI scoped simulado
        var services = new ServiceCollection();
        services.AddTenantContext();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();

        var ctx    = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var iface  = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        ctx.Should().BeSameAs(iface);   // mismo objeto, DI correcto

        // HttpContext
        var httpCtx = new DefaultHttpContext();
        httpCtx.RequestServices = scope.ServiceProvider;

        if (principal is not null)
            httpCtx.User = principal;

        var middleware = new TenantMiddleware(
            _ => Task.CompletedTask,
            NullLogger<TenantMiddleware>.Instance,
            new ClaimsExtractor());

        return (middleware, ctx, httpCtx);
    }

    private static ClaimsPrincipal AuthenticatedWith(params (string type, string value)[] claims)
    {
        var allClaims = new List<Claim>(claims.Select(c => new Claim(c.type, c.value)));
        var identity  = new ClaimsIdentity(allClaims, "TestAuth"); // authType != null → IsAuthenticated = true
        return new ClaimsPrincipal(identity);
    }

    // ── No autenticado → contexto no inicializado ─────────────────────────────

    [Fact]
    public async Task Invoke_AnonymousRequest_DoesNotInitializeContext()
    {
        var (mw, ctx, httpCtx) = Build(); // sin usuario

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeFalse();
    }

    // ── Autenticado sin tenant_id → contexto no inicializado ─────────────────

    [Fact]
    public async Task Invoke_AuthenticatedWithoutTenantId_DoesNotInitializeContext()
    {
        var principal = AuthenticatedWith(("sub", Guid.NewGuid().ToString()));
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeFalse();
    }

    // ── Autenticado con tenant_id inválido → contexto no inicializado ─────────

    [Fact]
    public async Task Invoke_AuthenticatedWithInvalidTenantIdGuid_DoesNotInitializeContext()
    {
        var principal = AuthenticatedWith(
            ("tenant_id", "not-a-guid"),
            ("sub",       Guid.NewGuid().ToString()));
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeFalse();
    }

    // ── Happy path: claims mínimos ────────────────────────────────────────────

    [Fact]
    public async Task Invoke_ValidClaims_InitializesContext()
    {
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();

        var principal = AuthenticatedWith(
            ("tenant_id", tenantId.ToString()),
            ("user_role", "admin"),
            ("sub",       userId.ToString()));
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeTrue();
        ctx.TenantId.Should().Be(tenantId);
        ctx.UserRole.Should().Be("admin");
        ctx.UserId.Should().Be(userId);
    }

    // ── Sin sub: userId = null ────────────────────────────────────────────────

    [Fact]
    public async Task Invoke_ValidClaimsNoSub_InitializesContextWithNullUserId()
    {
        var tenantId  = Guid.NewGuid();
        var principal = AuthenticatedWith(
            ("tenant_id", tenantId.ToString()),
            ("user_role", "therapist"));
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeTrue();
        ctx.UserId.Should().BeNull();
    }

    // ── Rol inválido en claim: se descarta el rol ─────────────────────────────

    [Fact]
    public async Task Invoke_InvalidRoleClaim_InitializesContextWithNullRole()
    {
        var tenantId  = Guid.NewGuid();
        var principal = AuthenticatedWith(
            ("tenant_id", tenantId.ToString()),
            ("user_role", "superadmin"));   // inválido
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeTrue("el contexto se inicializa incluso con rol inválido");
        ctx.UserRole.Should().BeNull("el rol inválido se descarta");
    }

    // ── Lee tenant_id desde app_metadata si no hay claim directo ──────────────

    [Fact]
    public async Task Invoke_TenantIdInAppMetadata_InitializesCorrectly()
    {
        var tenantId  = Guid.NewGuid();
        var principal = AuthenticatedWith(
            ("app_metadata.tenant_id", tenantId.ToString()),
            ("user_role",              "owner"));
        var (mw, ctx, httpCtx) = Build(principal);

        await mw.InvokeAsync(httpCtx);

        ctx.IsInitialized.Should().BeTrue();
        ctx.TenantId.Should().Be(tenantId);
    }

    // ── Pipeline continúa siempre ─────────────────────────────────────────────

    [Fact]
    public async Task Invoke_AlwaysCallsNext_RegardlessOfClaims()
    {
        var nextCalled = false;
        var services   = new ServiceCollection();
        services.AddTenantContext();
        var sp      = services.BuildServiceProvider().CreateScope().ServiceProvider;
        var httpCtx = new DefaultHttpContext { RequestServices = sp };

        var mw = new TenantMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<TenantMiddleware>.Instance,
            new ClaimsExtractor());

        await mw.InvokeAsync(httpCtx);

        nextCalled.Should().BeTrue();
    }
}
