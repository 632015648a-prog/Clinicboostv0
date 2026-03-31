using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ClinicBoost.Api.Features.Audit;

public static class AuthEndpoints
{
    public const string CookieName = "cb_rt";
    public const string CookiePath = "/auth/refresh";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /auth/refresh — rota el refresh token y emite nuevo HttpOnly cookie
        app.MapPost("/auth/refresh", async (
            HttpContext ctx,
            IRefreshTokenService tokenSvc,
            ISecurityAuditService audit) =>
        {
            var plain = ctx.Request.Cookies[CookieName];
            if (string.IsNullOrEmpty(plain))
                return Results.Unauthorized();

            var ip = GetClientIp(ctx);
            var ua = ctx.Request.Headers.UserAgent.ToString();

            var result = await tokenSvc.RotateAsync(plain, ip, ua);

            if (!result.Success)
            {
                ClearRefreshCookie(ctx);
                return result.IsBreach
                    ? Results.Problem(
                        title: "Brecha de seguridad detectada",
                        detail: "Todas las sesiones han sido revocadas.",
                        statusCode: 401)
                    : Results.Unauthorized();
            }

            SetRefreshCookie(ctx, result.PlainToken!, result.ExpiresAt!.Value);
            return Results.Ok(new { tokenId = result.TokenId, expiresAt = result.ExpiresAt });
        }).AllowAnonymous();

        // POST /auth/revoke — cierra sesión revocando token + JTI
        app.MapPost("/auth/revoke", async (
            HttpContext ctx,
            IRefreshTokenService tokenSvc,
            ISessionInvalidationService sessionSvc,
            ITenantContext tenantCtx,
            ISecurityAuditService audit) =>
        {
            var plain = ctx.Request.Cookies[CookieName];
            if (!string.IsNullOrEmpty(plain))
                await tokenSvc.RevokeAsync(plain, "logout");

            ClearRefreshCookie(ctx);

            if (tenantCtx.IsInitialized && tenantCtx.UserId.HasValue)
                await audit.RecordAuthAsync(
                    tenantCtx.TenantId!.Value, "auth.logout",
                    actorId: tenantCtx.UserId.Value,
                    ipAddress: GetClientIp(ctx));

            return Results.Ok(new { revoked = true });
        });

        // POST /auth/revoke-all — revoca todas las sesiones del usuario
        app.MapPost("/auth/revoke-all", async (
            HttpContext ctx,
            IRefreshTokenService tokenSvc,
            ITenantContext tenantCtx,
            ISecurityAuditService audit) =>
        {
            if (!tenantCtx.IsInitialized)
                return Results.Unauthorized();

            var tenantId = tenantCtx.RequireTenantId();
            var userId   = tenantCtx.UserId!.Value;

            var count = await tokenSvc.RevokeAllForUserAsync(userId, tenantId, "revoke-all");
            ClearRefreshCookie(ctx);

            await audit.RecordAuthAsync(
                tenantId, "auth.revoke_all_sessions",
                actorId: userId,
                ipAddress: GetClientIp(ctx),
                metadata: $"{{\"revokedCount\":{count}}}");

            return Results.Ok(new { revokedCount = count });
        });

        // GET /auth/sessions — lista sesiones activas del usuario
        app.MapGet("/auth/sessions", async (
            ITenantContext tenantCtx,
            IRefreshTokenService tokenSvc) =>
        {
            if (!tenantCtx.IsInitialized)
                return Results.Unauthorized();

            var sessions = await tokenSvc.GetActiveSessionsAsync(
                tenantCtx.UserId!.Value, tenantCtx.RequireTenantId());

            return Results.Ok(sessions);
        });

        // POST /auth/csp-report — recibe informes de violación CSP
        app.MapPost("/auth/csp-report", async (
            HttpContext ctx,
            ISecurityAuditService audit) =>
        {
            try
            {
                // P2: limitar cuerpo a 4 KB para prevenir DoS/abuso del endpoint público
                const int MaxBodyBytes = 4096;
                using var reader    = new System.IO.StreamReader(ctx.Request.Body);
                var buffer          = new char[MaxBodyBytes + 1];
                int charsRead       = await reader.ReadAsync(buffer, 0, buffer.Length);
                var body            = new string(buffer, 0, Math.Min(charsRead, MaxBodyBytes));

                await audit.RecordSecurityAsync(
                    Guid.Empty, "csp.violation",
                    ipAddress: GetClientIp(ctx),
                    metadata: body,
                    riskScore: 3);
            }
            catch { /* best-effort */ }
            return Results.NoContent();
        }).AllowAnonymous();

        return app;
    }

    // ── Cookie helpers ─────────────────────────────────────────────────────────

    private static void SetRefreshCookie(HttpContext ctx, string token, DateTimeOffset expires)
    {
        ctx.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Strict,
            Path     = CookiePath,
            Expires  = expires,
        });
    }

    private static void ClearRefreshCookie(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(CookieName,
            new CookieOptions { Path = CookiePath, Secure = true, HttpOnly = true });
    }

    private static string? GetClientIp(HttpContext ctx)
    {
        // N-P0-02 fix: UseForwardedHeaders() ya procesó X-Forwarded-For y actualizó
        // Connection.RemoteIpAddress con la IP real del cliente.
        // Leer directamente el header aquí bypasearía el middleware y permitiría
        // que un cliente malicioso falsifique su IP en los logs de auditoría.
        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
