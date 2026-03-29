namespace ClinicBoost.Api.Infrastructure.Middleware;

// ════════════════════════════════════════════════════════════════
// ITenantContext — contrato para acceder al contexto del tenant
// activo en la petición HTTP actual.
//
// Se registra como Scoped en DI (una instancia por request).
// TenantMiddleware lo rellena; el interceptor EF Core lo consume.
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Contexto del tenant activo en la petición HTTP actual.
/// Poblado por TenantMiddleware a partir del JWT.
/// Consumido por TenantDbContextInterceptor al inicio de cada
/// transacción EF Core para llamar a claim_tenant_context().
/// </summary>
public interface ITenantContext
{
    /// <summary>Tenant activo. Null si la petición no está autenticada.</summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Rol del usuario activo: owner | admin | therapist | receptionist | service.
    /// Null si no autenticado.
    /// </summary>
    string? UserRole { get; }

    /// <summary>ID del usuario autenticado (sub del JWT). Null si no autenticado.</summary>
    Guid? UserId { get; }

    /// <summary>True si el contexto está completamente inicializado.</summary>
    bool IsInitialized { get; }
}

/// <summary>
/// Implementación mutable de ITenantContext.
/// Solo TenantMiddleware puede escribir en ella.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid?   TenantId       { get; private set; }
    public string? UserRole       { get; private set; }
    public Guid?   UserId         { get; private set; }
    public bool    IsInitialized  => TenantId.HasValue;

    internal void Initialize(Guid tenantId, string? userRole, Guid? userId)
    {
        TenantId = tenantId;
        UserRole = userRole;
        UserId   = userId;
    }
}


// ════════════════════════════════════════════════════════════════
// TenantMiddleware
//
// Extrae tenant_id, user_role y user_id (sub) del JWT y
// popula ITenantContext (Scoped).
//
// Posición en el pipeline: DESPUÉS de UseAuthentication().
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Middleware que extrae el contexto de tenant del JWT autenticado.
///
/// Fuentes consultadas (en orden):
///   1. Claim directo "tenant_id"
///   2. Claim "app_metadata.tenant_id" (Supabase app_metadata)
///
/// El contexto queda disponible en ITenantContext (Scoped DI)
/// para el resto de la petición, incluyendo el interceptor EF Core.
/// </summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate             _next;
    private readonly ILogger<TenantMiddleware>   _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            // ── Extraer tenant_id ──────────────────────────────────
            var rawTenantId = ctx.User.FindFirst("tenant_id")?.Value
                           ?? ctx.User.FindFirst("app_metadata.tenant_id")?.Value;

            if (string.IsNullOrWhiteSpace(rawTenantId) ||
                !Guid.TryParse(rawTenantId, out var tenantId))
            {
                _logger.LogWarning(
                    "JWT autenticado sin claim tenant_id válido. Path={Path} Sub={Sub}",
                    ctx.Request.Path,
                    ctx.User.FindFirst("sub")?.Value ?? "unknown");

                // Continuar sin inicializar el contexto;
                // los endpoints que requieran tenant usarán [Authorize] + el interceptor
                // lanzará assert_tenant_context si se intenta una query sin contexto.
                await _next(ctx);
                return;
            }

            // ── Extraer user_role ──────────────────────────────────
            var userRole = ctx.User.FindFirst("user_role")?.Value
                        ?? ctx.User.FindFirst("app_metadata.user_role")?.Value;

            // ── Extraer user_id (sub) ──────────────────────────────
            var rawSub = ctx.User.FindFirst("sub")?.Value;
            Guid.TryParse(rawSub, out var userId);

            // ── Inicializar ITenantContext ─────────────────────────
            var tenantCtx = ctx.RequestServices.GetRequiredService<TenantContext>();
            tenantCtx.Initialize(
                tenantId,
                userRole,
                userId == Guid.Empty ? null : userId);

            _logger.LogDebug(
                "TenantContext inicializado. TenantId={TenantId} Role={Role} UserId={UserId}",
                tenantId, userRole, userId);
        }

        await _next(ctx);
    }
}

// ── Extensión para Program.cs ──────────────────────────────────

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(
        this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();

    /// <summary>
    /// Registra ITenantContext y TenantContext como Scoped.
    /// Llamar en AddFeatureServices() o directamente en Program.cs.
    /// </summary>
    public static IServiceCollection AddTenantContext(
        this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return services;
    }
}
