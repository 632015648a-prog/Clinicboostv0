using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ClinicBoost.Api.Infrastructure.Middleware;

// ── Nonce accessor ─────────────────────────────────────────────────────────────

public interface ICspNonceAccessor
{
    string Nonce { get; }
}

public sealed class CspNonceAccessor : ICspNonceAccessor
{
    public string Nonce { get; set; } = "";
}

// ── Options ────────────────────────────────────────────────────────────────────

public sealed class CspOptions
{
    public const string SectionName = "Csp";

    public string? AdditionalConnectSrc { get; set; }
    public string? AdditionalImgSrc     { get; set; }
    public bool    ReportOnly           { get; set; }
    public string  ReportUri            { get; set; } = "/auth/csp-report";
}

// ── Middleware ─────────────────────────────────────────────────────────────────

/// <summary>
/// Añade cabeceras de seguridad HTTP a todas las respuestas.
///
/// ORDEN en el pipeline (ADR-005 addendum):
///   Debe colocarse después de UseSerilogRequestLogging y antes de UseAuthentication.
///
/// DISEÑO: los headers se escriben directamente en InvokeAsync (no en OnStarting)
/// para garantizar compatibilidad con DefaultHttpContext en tests unitarios.
/// </summary>
public sealed class CspMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<CspOptions> _options;

    public CspMiddleware(RequestDelegate next, IOptions<CspOptions> options)
    {
        _next    = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var nonce = GenerateNonce();

        // Exponer el nonce al código de la aplicación
        var accessor = ctx.RequestServices.GetService<CspNonceAccessor>();
        if (accessor is not null)
            accessor.Nonce = nonce;

        // Escribir headers ANTES de delegar al siguiente middleware
        AddSecurityHeaders(ctx, nonce, _options.Value);

        await _next(ctx);
    }

    // ── Header construction ────────────────────────────────────────────────────

    private static void AddSecurityHeaders(HttpContext ctx, string nonce, CspOptions opts)
    {
        var headers = ctx.Response.Headers;
        var path    = ctx.Request.Path.Value ?? "";

        bool isApi = path.StartsWith("/api",      StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith("/auth",     StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith("/webhooks", StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith("/health",   StringComparison.OrdinalIgnoreCase);

        // ── CSP ────────────────────────────────────────────────────────────────
        string csp;
        if (isApi)
        {
            csp = "default-src 'none'";
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append($"default-src 'self'; ");
            sb.Append($"script-src 'self' 'nonce-{nonce}' 'strict-dynamic'; ");
            sb.Append($"style-src 'self' 'nonce-{nonce}'; ");
            sb.Append("img-src 'self' data: https:; ");
            sb.Append("font-src 'self'; ");
            sb.Append("form-action 'self'; ");
            sb.Append("frame-ancestors 'none'; ");

            var connectSrc = "connect-src 'self'";
            if (!string.IsNullOrWhiteSpace(opts.AdditionalConnectSrc))
                connectSrc += $" {opts.AdditionalConnectSrc}";
            sb.Append(connectSrc + "; ");

            sb.Append("worker-src 'self' blob:; ");
            sb.Append("manifest-src 'self'; ");
            sb.Append("media-src 'none'; ");
            sb.Append("object-src 'none'; ");
            sb.Append("base-uri 'self'; ");
            sb.Append("upgrade-insecure-requests; ");

            if (!string.IsNullOrWhiteSpace(opts.ReportUri))
                sb.Append($"report-uri {opts.ReportUri}");

            csp = sb.ToString().TrimEnd();
        }

        var cspHeaderName = opts.ReportOnly
            ? "Content-Security-Policy-Report-Only"
            : "Content-Security-Policy";

        headers[cspHeaderName] = csp;

        // ── HSTS (solo HTTPS) ──────────────────────────────────────────────────
        if (ctx.Request.IsHttps)
            headers["Strict-Transport-Security"] =
                "max-age=31536000; includeSubDomains; preload";

        // ── Otras cabeceras de seguridad ───────────────────────────────────────
        headers["X-Frame-Options"]             = "DENY";
        headers["X-Content-Type-Options"]      = "nosniff";
        headers["Referrer-Policy"]             = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"]          =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"]  = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = isApi ? "cross-origin" : "same-origin";
        headers["Cross-Origin-Embedder-Policy"] = "require-corp";

        // Eliminar cabeceras de identificación del servidor
        ctx.Response.Headers.Remove("Server");
        ctx.Response.Headers.Remove("X-Powered-By");
    }

    private static string GenerateNonce()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

// ── Extensions ────────────────────────────────────────────────────────────────

public static class CspMiddlewareExtensions
{
    public static IServiceCollection AddCspMiddleware(
        this IServiceCollection services,
        Action<CspOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<CspOptions>();

        services.AddScoped<CspNonceAccessor>();
        services.AddScoped<ICspNonceAccessor>(sp => sp.GetRequiredService<CspNonceAccessor>());
        return services;
    }

    /// <summary>
    /// Añade el CspMiddleware al pipeline.
    /// Colocar después de UseSerilogRequestLogging y antes de UseAuthentication.
    /// </summary>
    public static IApplicationBuilder UseCspMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<CspMiddleware>();
}
