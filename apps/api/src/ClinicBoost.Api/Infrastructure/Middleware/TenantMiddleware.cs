namespace ClinicBoost.Api.Infrastructure.Middleware;

/// <summary>
/// Extrae el tenant_id del JWT claim "tenant_id" y lo almacena en HttpContext.Items.
/// Se coloca después de UseAuthentication/UseAuthorization.
/// </summary>
public sealed class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var tenantId = ctx.User.FindFirst("tenant_id")?.Value
                        ?? ctx.User.FindFirst("app_metadata.tenant_id")?.Value;

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                _logger.LogWarning("JWT autenticado pero sin claim tenant_id. Path={Path}", ctx.Request.Path);
            }
            else
            {
                ctx.Items["TenantId"] = tenantId;
            }
        }

        await _next(ctx);
    }
}

// Alias para registrar en Program.cs
public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<TenantMiddleware>();
}
