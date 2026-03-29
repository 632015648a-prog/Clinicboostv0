using ClinicBoost.Api.Infrastructure.Tenants;

namespace ClinicBoost.Api.Infrastructure.Middleware;

// ════════════════════════════════════════════════════════════════
// TenantMiddleware
//
// POSICIÓN en el pipeline (Program.cs):
//   UseAuthentication() → UseAuthorization() → UseTenantMiddleware()
//
// RESPONSABILIDAD:
//   Extrae los claims de tenant del JWT autenticado y popula
//   ITenantContext (Scoped) para el resto de la petición.
//
// FLUJO:
//   1. Si el usuario no está autenticado → pasa al siguiente middleware
//      sin tocar el contexto (endpoints públicos funcionan normalmente).
//   2. Si autenticado pero tenant_id ausente o inválido → log Warning +
//      pasa al siguiente. El endpoint protegido fallará con 403/401.
//   3. Si autenticado y claims válidos → inicializa TenantContext + enriquece
//      LogContext de Serilog con TenantId, UserId y Role.
//
// SEGURIDAD:
//   · NO lanza excepciones que aborten la petición; deja que los endpoints
//     y TenantDbContextInterceptor manejen el contexto incompleto.
//   · El ClaimsExtractor valida los tipos de datos de los claims.
//   · La doble inicialización queda protegida en TenantContext.Initialize().
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Middleware que extrae el contexto de tenant (tenant_id, user_role, user_id)
/// del JWT autenticado y lo popula en <see cref="ITenantContext"/> (Scoped DI).
/// </summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate           _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private readonly ClaimsExtractor           _extractor;

    public TenantMiddleware(
        RequestDelegate           next,
        ILogger<TenantMiddleware> logger,
        ClaimsExtractor           extractor)
    {
        _next      = next;
        _logger    = logger;
        _extractor = extractor;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            // Petición sin autenticar: pasa al siguiente sin inicializar el contexto.
            // Los endpoints protegidos con [Authorize] devolverán 401 antes de llegar aquí.
            await _next(ctx);
            return;
        }

        // ── 1. Extraer tenant_id (obligatorio para usuarios autenticados) ──────
        var tenantResult = _extractor.ExtractTenantId(ctx.User);
        if (!tenantResult.Success)
        {
            _logger.LogWarning(
                "JWT válido sin tenant_id extraíble. " +
                "Code={Code} Path={Path} Sub={Sub} Detail={Detail}",
                tenantResult.ErrorCode,
                ctx.Request.Path,
                ctx.User.FindFirst("sub")?.Value ?? "unknown",
                tenantResult.ErrorMsg);

            // No abortamos: el endpoint puede ser público o de administración
            await _next(ctx);
            return;
        }

        // ── 2. Extraer user_role (opcional; si inválido logueamos y seguimos) ──
        var roleResult = _extractor.ExtractUserRole(ctx.User);
        if (!roleResult.Success)
        {
            _logger.LogWarning(
                "JWT contiene user_role inválido; se ignorará. " +
                "Code={Code} TenantId={TenantId} Detail={Detail}",
                roleResult.ErrorCode,
                tenantResult.Value,
                roleResult.ErrorMsg);
        }

        // ── 3. Extraer user_id (sub) ───────────────────────────────────────────
        var userIdResult = _extractor.ExtractUserId(ctx.User);
        if (!userIdResult.Success)
        {
            _logger.LogWarning(
                "Claim sub inválido en JWT; se usará null. " +
                "Code={Code} TenantId={TenantId} Detail={Detail}",
                userIdResult.ErrorCode,
                tenantResult.Value,
                userIdResult.ErrorMsg);
        }

        // ── 4. Inicializar TenantContext ───────────────────────────────────────
        var tenantCtx = ctx.RequestServices.GetRequiredService<TenantContext>();
        tenantCtx.Initialize(
            tenantResult.Value,
            roleResult.Success ? roleResult.Value : null,
            userIdResult.Success ? userIdResult.Value : null);

        // ── 5. Enriquecer LogContext de Serilog con campos de tenant ───────────
        // Cualquier log emitido DESPUÉS de este punto llevará estos campos
        using (Serilog.Context.LogContext.PushProperty("TenantId", tenantResult.Value))
        using (Serilog.Context.LogContext.PushProperty("UserId",   userIdResult.Value?.ToString() ?? "null"))
        using (Serilog.Context.LogContext.PushProperty("UserRole", roleResult.Value  ?? "unknown"))
        {
            _logger.LogDebug(
                "TenantContext inicializado. TenantId={TenantId} Role={Role} UserId={UserId}",
                tenantResult.Value,
                roleResult.Value,
                userIdResult.Value);

            await _next(ctx);
        }
    }
}

// ── Extensiones de registro y pipeline ───────────────────────────────────────

public static class TenantMiddlewareExtensions
{
    /// <summary>
    /// Agrega TenantMiddleware al pipeline HTTP.
    /// Debe colocarse DESPUÉS de UseAuthentication() y UseAuthorization().
    /// </summary>
    public static IApplicationBuilder UseTenantMiddleware(
        this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();

    /// <summary>
    /// Registra ITenantContext, TenantContext, ClaimsExtractor como servicios DI.
    /// Llamar en AddClinicBoostDatabase() o Program.cs.
    /// </summary>
    public static IServiceCollection AddTenantContext(
        this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ClaimsExtractor>();   // Sin estado → Singleton
        return services;
    }
}
