using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Http.Resilience;
using ClinicBoost.Api.Infrastructure.Database;
using EFCore.NamingConventions;
using System.Text;

namespace ClinicBoost.Api.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    // ─── Base de datos ────────────────────────────────────────────────────────
    public static IServiceCollection AddClinicBoostDatabase(
        this IServiceCollection services,
        IConfiguration config)
    {
        var connStr = config.GetConnectionString("Supabase")
            ?? throw new InvalidOperationException(
                "Connection string 'Supabase' no encontrada. " +
                "Revisa appsettings o variables de entorno.");

        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(connStr, npgsql =>
            {
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
                npgsql.CommandTimeout(30);
            })
            .UseSnakeCaseNamingConvention());

        return services;
    }

    // ─── Auth JWT (Supabase GoTrue) ───────────────────────────────────────────
    public static IServiceCollection AddClinicBoostAuth(
        this IServiceCollection services,
        IConfiguration config)
    {
        var jwtSecret = config["Supabase:JwtSecret"]
            ?? throw new InvalidOperationException("Supabase:JwtSecret no configurado.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,   // Supabase no pone un issuer fijo en dev
                    ValidateAudience = true,
                    ValidAudience = "authenticated",
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                // Leer token de cookie httpOnly si no viene en header
                opts.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Cookies.TryGetValue("sb-access-token", out var cookie))
                            ctx.Token = cookie;
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
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
                 .AllowCredentials()));   // Necesario para cookies httpOnly

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

    // ─── Resiliencia para HttpClients externos (Microsoft.Extensions.Http.Resilience) ────
    public static IServiceCollection AddClinicBoostResilience(
        this IServiceCollection services)
    {
        // Twilio HttpClient con retry exponencial + circuit breaker (Polly v8 API)
        services.AddHttpClient("Twilio", c =>
            {
                c.BaseAddress = new Uri("https://api.twilio.com/");
                c.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddStandardResilienceHandler(opts =>
            {
                opts.Retry.MaxRetryAttempts = 3;
                opts.Retry.Delay = TimeSpan.FromSeconds(2);
                opts.CircuitBreaker.SamplingDuration  = TimeSpan.FromSeconds(60);
                opts.CircuitBreaker.BreakDuration      = TimeSpan.FromSeconds(30);
            });

        return services;
    }

    // ─── Feature services aggregator ─────────────────────────────────────────
    public static IServiceCollection AddFeatureServices(
        this IServiceCollection services)
    {
        // Cada feature puede registrar sus servicios aquí conforme crece
        // Ejemplo:  services.AddScoped<AppointmentService>();
        return services;
    }
}

// ─── Extension para el pipeline HTTP ─────────────────────────────────────────
public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseClinicBoostCors(
        this IApplicationBuilder app)
        => app.UseCors("ClinicBoostPolicy");
}

// ─── Extension para mapear endpoints de features ─────────────────────────────
public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(
        this IEndpointRouteBuilder app)
    {
        // Cada feature mapea sus propios endpoints vía extension methods
        // Ejemplo:  app.MapAppointmentEndpoints();
        return app;
    }
}
