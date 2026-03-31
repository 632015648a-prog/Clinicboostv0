using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Http.Resilience;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Middleware;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;
using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Api.Features.Appointments;
using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Features.Variants;
using FluentValidation;
using System.Text;

namespace ClinicBoost.Api.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    // Base de datos + TenantContext
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

    // Auth JWT (Supabase GoTrue) + cookies httpOnly
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
                    ValidateIssuer           = false,
                    ValidateAudience         = true,
                    ValidAudience            = "authenticated",
                    ClockSkew                = TimeSpan.FromSeconds(30),
                    ValidateLifetime         = true,
                    RequireExpirationTime    = true,
                };

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

                opts.IncludeErrorDetails = false;
            });

        services.AddAuthorization(opts =>
        {
            opts.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            opts.AddPolicy("RequireTenant", p =>
                p.RequireAuthenticatedUser()
                 .RequireClaim("tenant_id"));
        });

        return services;
    }

    // CORS
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
                 .AllowCredentials()
                 .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    // Health Checks
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

    // Resiliencia para HttpClients externos
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

    // HttpClient para AI (OpenAI / Anthropic Claude)
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

    // Idempotencia transversal
    public static IServiceCollection AddIdempotencyService(
        this IServiceCollection services)
    {
        services.AddScoped<IIdempotencyService, IdempotencyService>();
        return services;
    }

    // Twilio: validador de firma + resolución de tenant por teléfono
    public static IServiceCollection AddTwilioServices(
        this IServiceCollection services,
        IConfiguration          config)
    {
        services
            .AddOptions<TwilioOptions>()
            .Bind(config.GetSection(TwilioOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // IMemoryCache necesario para TenantPhoneResolver
        services.AddMemoryCache();

        // Singleton: valida firmas con HMAC-SHA1; sin estado mutable
        services.AddSingleton<ITwilioSignatureValidator>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TwilioOptions>>().Value;
            return new TwilioSignatureValidator(opts.AuthToken);
        });

        // Singleton: resuelve tenant por numero E.164 con cache en memoria
        services.AddSingleton<ITenantPhoneResolver, TenantPhoneResolver>();

        return services;
    }

    // flow_00: Cola + Worker de llamada perdida
    public static IServiceCollection AddMissedCallFeature(
        this IServiceCollection services)
    {
        // Singleton: el Channel debe sobrevivir al ciclo de vida del request
        services.AddSingleton<IMissedCallJobQueue, ChannelMissedCallJobQueue>();

        // Background service: consume la cola y ejecuta flow_00
        services.AddHostedService<MissedCallWorker>();

        return services;
    }

    // WhatsApp message-status callbacks
    public static IServiceCollection AddMessageStatusFeature(
        this IServiceCollection services)
    {
        services.AddScoped<IMessageStatusService, MessageStatusService>();
        return services;
    }

    // WhatsApp inbound: Cola + Worker + ConversationService
    public static IServiceCollection AddWhatsAppInboundFeature(
        this IServiceCollection services)
    {
        // Singleton: el Channel debe sobrevivir al ciclo de vida del request
        services.AddSingleton<IWhatsAppJobQueue, ChannelWhatsAppJobQueue>();

        // Background service: consume la cola y ejecuta el pipeline WhatsApp
        services.AddHostedService<WhatsAppInboundWorker>();

        // Scoped: gestiona upsert de Conversation y append de Message
        services.AddScoped<IConversationService, ConversationService>();

        return services;
    }

    // Agente conversacional IA
    public static IServiceCollection AddConversationalAgentFeature(
        this IServiceCollection services)
    {
        // Singleton: sin estado mutable, compartible entre scopes
        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<HardLimitGuard>();

        // Scoped: dependen de AppDbContext (Scoped)
        services.AddScoped<IntentClassifier>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<IConversationalAgent, ConversationalAgent>();

        return services;
    }

    // Flow01: llamada perdida → WA recovery → reserva conversacional
    public static IServiceCollection AddFlow01Feature(
        this IServiceCollection services,
        IConfiguration          config)
    {
        // Opciones de configuración
        services
            .AddOptions<Flow01Options>()
            .Bind(config.GetSection(Flow01Options.SectionName));

        // Métricas KPI (Scoped: depende de AppDbContext)
        services.AddScoped<IFlowMetricsService, FlowMetricsService>();

        // Sender de mensajes outbound (Scoped: depende de AppDbContext)
        services.AddScoped<IOutboundMessageSender, TwilioOutboundMessageSender>();

        // Orquestador del flujo (Scoped)
        services.AddScoped<Flow01Orchestrator>();

        return services;
    }

    // Capa iCal read-only: caché persistida, freshness y fallback
    public static IServiceCollection AddCalendarFeature(
        this IServiceCollection services,
        IConfiguration          config)
    {
        // Opciones de configuración (sección "ICalOptions" en appsettings)
        services
            .AddOptions<ICalOptions>()
            .Bind(config.GetSection(ICalOptions.SectionName));

        // HttpClient dedicado para feeds iCal con resiliencia básica
        services.AddHttpClient("ICalReader", c =>
            {
                c.DefaultRequestHeaders.Add("User-Agent", "ClinicBoost/1.0 (iCal reader)");
            })
            .AddStandardResilienceHandler(opts =>
            {
                // Reintentos: 2 intentos extra, solo en errores transitorios
                opts.Retry.MaxRetryAttempts   = 2;
                opts.Retry.Delay              = TimeSpan.FromSeconds(1);

                // Timeout total: debe ser >= ICalOptions.HttpTimeout + margen
                opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });

        // IICalReader: parsea feeds .ics (Singleton — sin estado mutable)
        services.AddSingleton<IICalReader, HttpICalReader>();

        // ICalendarCacheStore: persiste la caché en Postgres via EF (Scoped)
        services.AddScoped<ICalendarCacheStore, EfCalendarCacheStore>();

        // ICalendarService: orquestador cache-aside + freshness + fallback (Scoped)
        services.AddScoped<ICalendarService, CalendarService>();

        return services;
    }

    // Gestión de citas: slots, reserva, cancelación y reprogramación
    public static IServiceCollection AddAppointmentsFeature(
        this IServiceCollection services)
    {
        // Servicio principal de citas (Scoped: depende de AppDbContext)
        services.AddScoped<IAppointmentService, AppointmentService>();

        // Validadores FluentValidation para los DTOs de citas
        services.AddScoped<IValidator<GetAvailableSlotsRequest>, GetAvailableSlotsValidator>();
        services.AddScoped<IValidator<BookAppointmentRequest>,    BookAppointmentValidator>();
        services.AddScoped<IValidator<CancelAppointmentRequest>,  CancelAppointmentValidator>();
        services.AddScoped<IValidator<RescheduleAppointmentRequest>, RescheduleAppointmentValidator>();

        return services;
    }

    // Feature services aggregator
    public static IServiceCollection AddFeatureServices(
        this IServiceCollection services,
        IConfiguration          config)
    {
        services.AddIdempotencyService();
        services.AddTwilioServices(config);
        services.AddMissedCallFeature();
        services.AddWhatsAppInboundFeature();
        services.AddMessageStatusFeature();
        services.AddConversationalAgentFeature();
        services.AddCalendarFeature(config);
        services.AddAppointmentsFeature();
        services.AddFlow01Feature(config);
        services.AddAuditSecurityFeature(config);
        services.AddVariantTrackingFeature();
        return services;
    }

    // ── Variant A/B tracking ──────────────────────────────────────────────────
    public static IServiceCollection AddVariantTrackingFeature(
        this IServiceCollection services)
    {
        services.AddScoped<IVariantTrackingService, VariantTrackingService>();
        return services;
    }

    // ── Audit / Security feature ───────────────────────────────────────────────

    public static IServiceCollection AddAuditSecurityFeature(
        this IServiceCollection services,
        IConfiguration config)
    {
        // IMemoryCache ya añadido por AddTwilioServices (idempotente)
        services.AddMemoryCache();

        // Opciones de refresh token
        services.Configure<RefreshTokenOptions>(
            config.GetSection(RefreshTokenOptions.SectionName));

        // Opciones CSP
        services.AddCspMiddleware(opts =>
        {
            config.GetSection(CspOptions.SectionName).Bind(opts);
            // Fallback por defecto
            if (string.IsNullOrWhiteSpace(opts.ReportUri))
                opts.ReportUri = "/auth/csp-report";
        });

        // Servicios scoped
        services.AddScoped<ISecurityAuditService, SecurityAuditService>();
        services.AddScoped<IRefreshTokenService,  RefreshTokenService>();
        services.AddScoped<ISessionInvalidationService, SessionInvalidationService>();

        // Background worker de limpieza de JTIs expirados
        services.AddHostedService<SessionCleanupWorker>();

        return services;
    }
}

// Extensions para pipeline HTTP

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
        app.MapMissedCallEndpoints();
        app.MapWhatsAppInboundEndpoints();
        app.MapMessageStatusEndpoints();
        app.MapAppointmentEndpoints();
        app.MapFlow01Endpoints();
        app.MapAuthEndpoints();
        app.MapVariantEndpoints();
        return app;
    }
}
