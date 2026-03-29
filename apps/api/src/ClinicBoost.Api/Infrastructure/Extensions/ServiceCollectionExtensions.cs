using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Http.Resilience;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Middleware;
using System.Text;

namespace ClinicBoost.Api.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    // ─── Base de datos + TenantContext ────────────────────────────────────────
    public static IServiceCollection AddClinicBoostDatabase(
        this IServiceCollection services,
        IConfiguration config)
    {
        var connStr = config.GetConnectionString("Supabase")
            ?? throw new InvalidOperationException(
                "Connection string 'Supabase' no encontrada. " +
                "Revisa appsettings.json o variables de entorno (SUPABASE_DB_URL).");

        // ITenantContext + TenantContext (Scoped) + ClaimsExtractor (Singleton)
        services.AddTenantContext();

        // Interceptor que llama a claim_tenant_context() al abrir la conexión
        services.AddScoped<TenantDbContextInterceptor>();

        services.AddDbContext<AppDbContext>((sp, opts) =>
        {
            var interceptor = sp.GetRequiredService<TenantDbContextInterceptor>();

            opts.UseNpgsql(connStr, npgsql =>
                {
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                    npgsql.CommandTimeout(30);
                })
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptor);
        });

        return services;
    }

    // ─── Auth JWT (Supabase GoTrue) + cookies httpOnly ────────────────────────
    public static IServiceCollection AddClinicBoostAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        var jwtSecret = config["Supabase:JwtSecret"]
            ?? throw new InvalidOperationException(
                "Supabase:JwtSecret no configurado. " +
                "Usar variable de entorno SUPABASE__JWTSECRET en producción.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = new SymmetricSecurityKey(
                                                   Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer           = false,  // Supabase no fija issuer en dev
                    ValidateAudience         = true,
                    ValidAudience            = "authenticated",
                    ClockSkew                = TimeSpan.FromSeconds(30),
                    // Validar que el token no está expirado de forma explícita
                    ValidateLifetime         = true,
                    RequireExpirationTime    = true,
                };

                // ── Leer token de cookie httpOnly ───────────────────────────
                // Cookie tiene prioridad sobre el header Authorization para
                // peticiones del frontend. Las peticiones server-to-server
                // pueden usar el header Bearer normalmente.
                opts.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Cookies.TryGetValue("sb-access-token", out var cookie)
                            && !string.IsNullOrWhiteSpace(cookie))
                        {
                            ctx.Token = cookie;
                        }
                        return Task.CompletedTask;
                    },

                    OnAuthenticationFailed = ctx =>
                    {
                        // Log estructurado; no exponer detalles del error al cliente
                        var logger = ctx.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerEvents>>();
                        logger.LogWarning(
                            "JWT authentication failed. " +
                            "Path={Path} Error={ErrorType}",
                            ctx.HttpContext.Request.Path,
                            ctx.Exception?.GetType().Name ?? "unknown");

                        return Task.CompletedTask;
                    }
                };

                // No revelar detalles de validación JWT al cliente
                opts.IncludeErrorDetails = false;
            });

        services.AddAuthorization(opts =>
        {
            // Política por defecto: require authenticated
            opts.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // Política "RequireTenant": require authenticated + tenant_id en claims
            // Usada como fallback en endpoints que no usan [RequireRole]
            opts.AddPolicy("RequireTenant", p =>
                p.RequireAuthenticatedUser()
                 .RequireClaim("tenant_id"));
        });

        return services;
    }

    // ─── CORS ─────────────────────────────────────────────────────────────────
    public static IServiceCollection AddClinicBoostCors(
        this IServiceCollection services,
        IConfiguration config)
    {
        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? ["http://localhost:5173"];

        services.AddCors(opts =>
            opts.AddPolicy("ClinicBoostPolicy", p =>
                p.WithOrigins(origins)
                 .AllowAnyMethod()
                 .AllowAnyHeader()
                 .AllowCredentials()       // Requerido para cookies httpOnly
                 .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    // ─── Health Checks ────────────────────────────────────────────────────────
    public static IServiceCollection AddClinicBoostHealthChecks(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddHealthChecks()
            .AddNpgSql(
                config.GetConnectionString("Supabase")!,
                name: "postgres",
                tags: ["db", "ready"]);

        return services;
    }

    // ─── Resiliencia para HttpClients externos ────────────────────────────────
    public static IServiceCollection AddClinicBoostResilience(
        this IServiceCollection services)
    {
        services.AddHttpClient("Twilio", c =>
            {
                c.BaseAddress = new Uri("https://api.twilio.com/");
                c.Timeout     = TimeSpan.FromSeconds(15);
            })
            .AddStandardResilienceHandler(opts =>
            {
                opts.Retry.MaxRetryAttempts              = 3;
                opts.Retry.Delay                         = TimeSpan.FromSeconds(2);
                opts.CircuitBreaker.SamplingDuration     = TimeSpan.FromSeconds(60);
                opts.CircuitBreaker.BreakDuration        = TimeSpan.FromSeconds(30);
            });

        return services;
    }

    // ─── HttpClient para AI (OpenAI / Anthropic Claude) ──────────────────────
    public static IServiceCollection AddAiResilience(
        this IServiceCollection services)
    {
        services.AddHttpClient("OpenAI", c =>
            {
                c.BaseAddress = new Uri("https://api.openai.com/");
                c.Timeout     = TimeSpan.FromSeconds(60);
            })
            .AddStandardResilienceHandler(opts =>
            {
                opts.Retry.MaxRetryAttempts          = 2;
                opts.Retry.Delay                     = TimeSpan.FromSeconds(3);
                opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                opts.CircuitBreaker.BreakDuration    = TimeSpan.FromSeconds(60);
            });

        return services;
    }

    // ─── Idempotencia transversal ─────────────────────────────────────────────
    public static IServiceCollection AddIdempotencyService(
        this IServiceCollection services)
    {
        // Scoped: una instancia por request HTTP o por scope de job.
        // Depende de AppDbContext (Scoped) e ITenantContext (Scoped).
        services.AddScoped<IIdempotencyService, IdempotencyService>();
        return services;
    }

    // ─── Feature services aggregator ─────────────────────────────────────────
    public static IServiceCollection AddFeatureServices(
        this IServiceCollection services)
    {
        // Registrar idempotencia (transversal a todos los features)
        services.AddIdempotencyService();

        // Cada feature registra sus propios servicios aquí conforme crece.
        // Ejemplo:  services.AddScoped<MissedCallService>();
        return services;
    }
}

// ─── Extensions para pipeline HTTP ───────────────────────────────────────────

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseClinicBoostCors(
        this IApplicationBuilder app)
        => app.UseCors("ClinicBoostPolicy");
}

public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(
        this IEndpointRouteBuilder app)
    {
        // Cada feature mapea sus propios endpoints vía extension methods.
        // Ejemplo:  app.MapAppointmentsEndpoints();
        return app;
    }
}
