using ClinicBoost.Api.Infrastructure.Extensions;
using ClinicBoost.Api.Features.Health;
using ClinicBoost.Api.Infrastructure.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;
using Serilog;

// ─── Bootstrap Serilog early (antes de construir el host) ────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Secrets locales (gitignored) ────────────────────────────────────────
    // appsettings.Development.Local.json sobreescribe Development para Twilio / AI keys.
    // Ver appsettings.Development.Local.json.example para la plantilla.
    if (builder.Environment.IsDevelopment())
    {
        builder.Configuration.AddJsonFile(
            "appsettings.Development.Local.json", optional: true, reloadOnChange: false);
    }

    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "ClinicBoost.Api")
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] " +
            "[T:{TenantId}] [U:{UserId}] [R:{UserRole}] " +
            "{Message:lj}{NewLine}{Exception}"));

    // ─── ForwardedHeaders (P0: IP real detrás de proxy/load balancer) ─────────
    // Configurar KnownProxies/KnownNetworks en producción vía appsettings.
    // En desarrollo, ForwardLimit=1 acepta el primer proxy de confianza (Docker/Nginx).
    builder.Services.Configure<ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Limpiar redes/proxies por defecto (solo loopback) para evitar spoofing.
        opts.KnownIPNetworks.Clear(); // KnownNetworks obsoleto en .NET 10; usar KnownIPNetworks
        opts.KnownProxies.Clear();
        // Añadir loopback explícito
        opts.KnownProxies.Add(System.Net.IPAddress.Loopback);
        opts.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
        opts.ForwardLimit = 1; // Confiamos en 1 capa de proxy (Nginx/Cloudflare)
    });

    // ─── Infrastructure ───────────────────────────────────────────────────────
    // Orden importante: Database registra TenantContext + interceptor antes que Auth
    builder.Services.AddClinicBoostDatabase(builder.Configuration);
    builder.Services.AddClinicBoostAuth(builder.Configuration);
    builder.Services.AddClinicBoostCors(builder.Configuration);
    builder.Services.AddClinicBoostHealthChecks(builder.Configuration);
    builder.Services.AddClinicBoostResilience();
    builder.Services.AddAiResilience();

    // ─── OpenAPI / Scalar ────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── Features (Vertical Slice) ───────────────────────────────────────────
    builder.Services.AddFeatureServices(builder.Configuration);

    var app = builder.Build();

    // ─── Middleware pipeline ──────────────────────────────────────────────────
    // ORDEN CRÍTICO — no reordenar sin revisar ADR-005:
    //
    //  0. ForwardedHeaders       (PRIMERO: resuelve IP real del cliente)
    //  1. Serilog request logging  (antes de todo para capturar toda la petición)
    //  2. CORS                     (preflight OPTIONS antes de auth)
    //  3. UseAuthentication        (valida JWT y popula ctx.User)
    //  4. UseAuthorization         (aplica políticas [Authorize])
    //  5. UseTenantMiddleware      (extrae claims → ITenantContext)
    //  6. TenantAuthorization      (valida rol mínimo por endpoint)
    //  7. Endpoints

    // P0: resolver IP real antes de cualquier log o validación de seguridad
    app.UseForwardedHeaders();

    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            var tenantCtx = httpCtx.RequestServices
                .GetService<ClinicBoost.Api.Infrastructure.Tenants.ITenantContext>();
            diagCtx.Set("TenantId", tenantCtx?.TenantId?.ToString() ?? "-");
            diagCtx.Set("UserId",   tenantCtx?.UserId?.ToString()   ?? "-");
            diagCtx.Set("UserRole", tenantCtx?.UserRole             ?? "-");
        };
    });

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(opts =>
        {
            opts.Title = "ClinicBoost API";
            opts.Theme = Scalar.AspNetCore.ScalarTheme.Purple;
        });
    }

    // Scalar en Staging: solo accesible desde red local (sin auth pública)
    if (app.Environment.IsEnvironment("Staging"))
    {
        app.MapOpenApi();
        // EndpointPathPrefix fue eliminado en Scalar.AspNetCore v2.x.
        // El prefijo de ruta se pasa como parámetro endpointPrefix directamente.
        app.MapScalarApiReference(endpointPrefix: "/scalar", configureOptions: opts =>
        {
            opts.Title = "ClinicBoost API [STAGING]";
            opts.Theme = Scalar.AspNetCore.ScalarTheme.Purple;
        });
    }

    // Cabeceras de seguridad (CSP, HSTS, X-Frame-Options…)
    // Colocar antes de UseAuthentication
    app.UseCspMiddleware();

    app.UseClinicBoostCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // Tenant middleware: popula ITenantContext desde JWT y enriquece LogContext
    app.UseTenantMiddleware();

    // Rol mínimo por endpoint vía [RequireRole("admin")] metadata
    app.UseTenantAuthorizationMiddleware();

    // ─── Endpoints (Vertical Slice) ───────────────────────────────────────────
    app.MapHealthEndpoints();
    app.MapFeatureEndpoints();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ClinicBoost.Api terminó de forma inesperada.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
