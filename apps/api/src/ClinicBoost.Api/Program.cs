using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Extensions;
using ClinicBoost.Api.Features.Health;
using ClinicBoost.Api.Infrastructure.Middleware;
using Scalar.AspNetCore;
using Serilog;

// ─── Bootstrap Serilog early ─────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ─────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "ClinicBoost.Api")
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] [{TenantId}] {Message:lj}{NewLine}{Exception}"));

    // ─── Infrastructure ───────────────────────────────────────────────────────
    builder.Services.AddClinicBoostDatabase(builder.Configuration);
    builder.Services.AddClinicBoostAuth(builder.Configuration);
    builder.Services.AddClinicBoostCors(builder.Configuration);
    builder.Services.AddClinicBoostHealthChecks(builder.Configuration);
    builder.Services.AddClinicBoostResilience();
    builder.Services.AddAiResilience();

    // ─── OpenAPI / Scalar ────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── Vertical Slice features ─────────────────────────────────────────────
    // Cada feature registra sus propios servicios mediante extension methods
    builder.Services.AddFeatureServices();

    var app = builder.Build();

    // ─── Middleware pipeline ─────────────────────────────────────────────────
    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            // Leer TenantId desde ITenantContext (Scoped) si está disponible
            var tenantCtx = httpCtx.RequestServices
                .GetService<ClinicBoost.Api.Infrastructure.Middleware.ITenantContext>();
            diagCtx.Set("TenantId", tenantCtx?.TenantId?.ToString() ?? "unknown");
            diagCtx.Set("UserId",   httpCtx.User.FindFirst("sub")?.Value ?? "anon");
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

    app.UseClinicBoostCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // ─── Tenant middleware ────────────────────────────────────────────────────
    // Popula ITenantContext (Scoped) con tenant_id, user_role y user_id del JWT.
    // TenantDbContextInterceptor lo consume al inicio de cada transacción EF Core.
    app.UseTenantMiddleware();

    // ─── Map endpoints (Vertical Slice) ──────────────────────────────────────
    app.MapHealthEndpoints();
    app.MapFeatureEndpoints();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ClinicBoost.Api terminó de forma inesperada");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
