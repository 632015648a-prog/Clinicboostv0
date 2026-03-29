using System.Text.Json;
using ClinicBoost.Api.Infrastructure.Tenants;

namespace ClinicBoost.Api.Infrastructure.Middleware;

// ════════════════════════════════════════════════════════════════
// TenantAuthorizationMiddleware
//
// PROPÓSITO
// ─────────
// Valida que el usuario tiene el rol mínimo requerido para acceder
// a una ruta. Complementa a [Authorize] de ASP.NET Core con
// semántica de roles específica de ClinicBoost.
//
// USO
// ───
// Decorar el endpoint con el atributo [RequireRole("admin")]:
//
//   app.MapGet("/api/settings", handler)
//      .WithMetadata(new RequireRoleAttribute("admin"))
//      .RequireAuthorization();
//
// FLUJO
// ─────
// 1. Si la ruta no tiene [RequireRole] → pasa al siguiente.
// 2. Si el contexto no está inicializado → 401 JSON.
// 3. Si el rol del usuario es insuficiente → 403 JSON estructurado.
// 4. Si el rol es suficiente → pasa al siguiente.
//
// RESPUESTA DE ERROR
// ───────────────────
// Siempre JSON con:
//   { "type": "...", "title": "...", "status": 4xx,
//     "detail": "...", "traceId": "...", "code": "SEC-xxx" }
//
// POSICIÓN en el pipeline:
//   UseTenantMiddleware() → UseTenantAuthorizationMiddleware()
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Atributo para marcar un endpoint con el rol mínimo requerido.
/// Usar con <see cref="TenantAuthorizationMiddleware"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireRoleAttribute : Attribute
{
    /// <summary>Rol mínimo necesario (usa jerarquía de TenantRole).</summary>
    public string MinimumRole { get; }

    public RequireRoleAttribute(string minimumRole) => MinimumRole = minimumRole;
}

/// <summary>
/// Middleware que valida el rol mínimo requerido en cada endpoint
/// usando los metadatos del endpoint y <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantAuthorizationMiddleware
{
    private readonly RequestDelegate                       _next;
    private readonly ILogger<TenantAuthorizationMiddleware> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TenantAuthorizationMiddleware(
        RequestDelegate                        next,
        ILogger<TenantAuthorizationMiddleware> logger)
    {
        _next   = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var requireRole = ctx.GetEndpoint()
            ?.Metadata
            .GetMetadata<RequireRoleAttribute>();

        // Sin atributo: pasa al siguiente sin comprobación adicional
        if (requireRole is null)
        {
            await _next(ctx);
            return;
        }

        var tenantCtx = ctx.RequestServices.GetRequiredService<ITenantContext>();

        // ── Caso 1: contexto no inicializado → 401 ─────────────────────────────
        if (!tenantCtx.IsInitialized)
        {
            _logger.LogWarning(
                "[TenantAuth] Contexto no inicializado para ruta protegida. " +
                "Path={Path} RequiredRole={Role}",
                ctx.Request.Path, requireRole.MinimumRole);

            await WriteErrorAsync(ctx, 401,
                "unauthorized",
                "Autenticación requerida",
                "Must be authenticated with a valid JWT containing tenant_id to access this resource.",
                "SEC-401");
            return;
        }

        // ── Caso 2: rol insuficiente → 403 ────────────────────────────────────
        if (!tenantCtx.HasAtLeastRole(requireRole.MinimumRole))
        {
            _logger.LogWarning(
                "[TenantAuth] Acceso denegado por rol insuficiente. " +
                "TenantId={TenantId} UserRole={UserRole} RequiredRole={RequiredRole} Path={Path}",
                tenantCtx.TenantId, tenantCtx.UserRole,
                requireRole.MinimumRole, ctx.Request.Path);

            await WriteErrorAsync(ctx, 403,
                "forbidden",
                "Acceso denegado",
                $"Esta acción requiere el rol '{requireRole.MinimumRole}' o superior. " +
                $"Rol actual: '{tenantCtx.UserRole ?? "none"}'.",
                "SEC-403");
            return;
        }

        await _next(ctx);
    }

    private static async Task WriteErrorAsync(
        HttpContext ctx,
        int         statusCode,
        string      type,
        string      title,
        string      detail,
        string      code)
    {
        ctx.Response.StatusCode  = statusCode;
        ctx.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type    = $"https://clinicboost.io/errors/{type}",
            title,
            status  = statusCode,
            detail,
            traceId = ctx.TraceIdentifier,
            code
        };

        var json = JsonSerializer.Serialize(problem, _jsonOpts);
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }
}

// ── Extensión de pipeline ─────────────────────────────────────────────────────

public static class TenantAuthorizationMiddlewareExtensions
{
    /// <summary>
    /// Agrega TenantAuthorizationMiddleware al pipeline.
    /// Debe colocarse DESPUÉS de UseTenantMiddleware().
    /// </summary>
    public static IApplicationBuilder UseTenantAuthorizationMiddleware(
        this IApplicationBuilder app)
        => app.UseMiddleware<TenantAuthorizationMiddleware>();
}
