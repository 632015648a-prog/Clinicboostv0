using System.Net;
using System.Reflection;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Tests.Infrastructure.Idempotency;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// WhatsAppInboundEndpointTests
//
// Tests de integración del endpoint POST /webhooks/twilio/whatsapp.
//
// ESTRATEGIA:
//   · WebApplicationFactory<Program> con overrides específicos:
//     - BD EF InMemory (sin Postgres real)
//     - ITwilioSignatureValidator: mock (controla si la firma es válida)
//     - ITenantPhoneResolver: mock (controla si el tenant existe)
//     - IWhatsAppJobQueue: mock (verifica que el job se encola)
//     - IIdempotencyService: TestableIdempotencyService (EF InMemory, sin SQL crudo)
//   · No necesitamos JWT porque el endpoint es AllowAnonymous.
//   · La firma Twilio la emulamos con el mock del validador.
// ════════════════════════════════════════════════════════════════════════════

public sealed class WhatsAppInboundEndpointTests
    : IClassFixture<WhatsAppInboundWebFactory>
{
    private readonly WhatsAppInboundWebFactory _factory;

    public WhatsAppInboundEndpointTests(WhatsAppInboundWebFactory factory)
    {
        _factory = factory;
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Validación de firma
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns403_WhenSignatureIsInvalid()
    {
        _factory.SignatureValidator
            .IsValid(Arg.Any<IEnumerable<KeyValuePair<string, string>>>(),
                     Arg.Any<string>(),
                     Arg.Any<string>())
            .Returns(false);

        var client   = _factory.CreateClient();
        var content  = BuildWhatsAppForm("SMtest001", "+34612000001", "+34910000001");
        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "firma inválida debe devolver 403");
    }

    [Fact]
    public async Task Post_Returns200TwiML_WhenSignatureIsValid()
    {
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);  // número no registrado

        var client   = _factory.CreateClient();
        var content  = BuildWhatsAppForm("SMtest002", "+34612000002", "+34910000002");
        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<Response>", "debe devolver TwiML válido");
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("text/xml");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Resolución de tenant
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenTenantNotFound_WithoutEnqueuing()
    {
        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        _factory.JobQueue.ClearReceivedCalls();

        var client   = _factory.CreateClient();
        var content  = BuildWhatsAppForm("SMtest003", "+34612000003", "+34920000003");
        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<WhatsAppInboundJob>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Idempotencia
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenDuplicateMessageSid_WithoutEnqueuing()
    {
        var messageSid = "SMdup001";
        var tenantId   = Guid.NewGuid();

        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);
        _factory.JobQueue
            .EnqueueAsync(Arg.Any<WhatsAppInboundJob>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var client  = _factory.CreateClient();
        var content = BuildWhatsAppForm(messageSid, "+34612000004", "+34910000004");

        // Primera entrega
        var first = await client.PostAsync("/webhooks/twilio/whatsapp", content);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Limpiar llamadas previas al mock para comprobar que la segunda entrega
        // no vuelve a encolar
        _factory.JobQueue.ClearReceivedCalls();

        // Segunda entrega idéntica (re-entrega de Twilio)
        var second = await client.PostAsync("/webhooks/twilio/whatsapp",
            BuildWhatsAppForm(messageSid, "+34612000004", "+34910000004"));

        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "re-entrega idempotente debe devolver 200");
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<WhatsAppInboundJob>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Job encolado correctamente (correlación completa)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_EnqueuesJob_WithCorrectFields()
    {
        var messageSid   = "SMnew001";
        var callerPhone  = "+34612111111";
        var clinicPhone  = "+34910000005";
        var tenantId     = Guid.NewGuid();
        const string body = "Hola, quiero una cita";

        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(clinicPhone, Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);

        WhatsAppInboundJob? capturedJob = null;
        _factory.JobQueue
            .EnqueueAsync(Arg.Do<WhatsAppInboundJob>(j => capturedJob = j),
                          Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var client   = _factory.CreateClient();
        var content  = BuildWhatsAppForm(messageSid, callerPhone, clinicPhone, body);
        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        capturedJob.Should().NotBeNull("el job debe haberse encolado");
        capturedJob!.MessageSid.Should().Be(messageSid,
            "el MessageSid debe trasladarse al job para correlación");
        capturedJob.CallerPhone.Should().Be(callerPhone);
        capturedJob.ClinicPhone.Should().Be(clinicPhone);
        capturedJob.Body.Should().Be(body);
        capturedJob.TenantId.Should().Be(tenantId);
        capturedJob.ProcessedEventId.Should().NotBe(Guid.Empty,
            "el ProcessedEventId vincula el job con processed_events");
        capturedJob.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "el CorrelationId permite rastrear el flujo end-to-end");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Campos vacíos / datos malformados
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_Returns200_WhenMessageSidIsMissing_WithoutEnqueuing()
    {
        SetupValidSignature();
        _factory.JobQueue.ClearReceivedCalls();

        var client  = _factory.CreateClient();
        // Form sin MessageSid
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("From",       "whatsapp:+34612000009"),
            new KeyValuePair<string, string>("To",         "whatsapp:+34910000009"),
            new KeyValuePair<string, string>("Body",       "Hola"),
            new KeyValuePair<string, string>("AccountSid", "ACtest"),
        });

        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "datos malformados deben devolver 200 para no provocar reintentos");
        await _factory.JobQueue.DidNotReceive()
            .EnqueueAsync(Arg.Any<WhatsAppInboundJob>(), Arg.Any<CancellationToken>());
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 6: Correlación MessageSid en el WebhookEvent persistido
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Post_PersistsWebhookEvent_WithMessageSidInPayload()
    {
        var messageSid  = "SMtrace001";
        var callerPhone = "+34612222222";
        var clinicPhone = "+34910000006";
        var tenantId    = Guid.NewGuid();

        SetupValidSignature();
        _factory.PhoneResolver
            .ResolveAsync(clinicPhone, Arg.Any<CancellationToken>())
            .Returns((Guid?)tenantId);
        _factory.JobQueue
            .EnqueueAsync(Arg.Any<WhatsAppInboundJob>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));

        var client   = _factory.CreateClient();
        var content  = BuildWhatsAppForm(messageSid, callerPhone, clinicPhone);
        var response = await client.PostAsync("/webhooks/twilio/whatsapp", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verificar que el WebhookEvent fue persistido con el MessageSid en el payload
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var webhookEvent = await db.WebhookEvents
            .Where(we => we.TenantId == tenantId &&
                         we.EventType == "whatsapp_inbound")
            .FirstOrDefaultAsync();

        webhookEvent.Should().NotBeNull(
            "debe existir un WebhookEvent para el mensaje recibido");
        webhookEvent!.Payload.Should().Contain(messageSid,
            "el payload debe incluir el MessageSid para correlación");
        webhookEvent.Source.Should().Be("twilio");
        webhookEvent.IdempotencyKey.Should().NotBeNullOrEmpty(
            "debe vincularse con processed_events via IdempotencyKey");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupValidSignature()
    {
        _factory.SignatureValidator
            .IsValid(Arg.Any<IEnumerable<KeyValuePair<string, string>>>(),
                     Arg.Any<string>(),
                     Arg.Any<string>())
            .Returns(true);
    }

    /// <summary>
    /// Construye un form típico de webhook WhatsApp de Twilio.
    /// Los números siguen el formato real de Twilio (prefijo "whatsapp:").
    /// </summary>
    private static FormUrlEncodedContent BuildWhatsAppForm(
        string messageSid,
        string from,
        string to,
        string body = "Hola")
    {
        // Twilio prefija los números WA con "whatsapp:"
        var fromWa = from.StartsWith("whatsapp:") ? from : $"whatsapp:{from}";
        var toWa   = to.StartsWith("whatsapp:")   ? to   : $"whatsapp:{to}";

        // WaId = número E.164 sin prefijo (campo directo de Twilio)
        var waId = from.StartsWith("whatsapp:")
            ? from["whatsapp:".Length..]
            : from;

        return new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("MessageSid",  messageSid),
            new KeyValuePair<string, string>("From",        fromWa),
            new KeyValuePair<string, string>("To",          toWa),
            new KeyValuePair<string, string>("Body",        body),
            new KeyValuePair<string, string>("WaId",        waId),
            new KeyValuePair<string, string>("NumMedia",    "0"),
            new KeyValuePair<string, string>("AccountSid",  "ACtest"),
            new KeyValuePair<string, string>("ProfileName", "Test User"),
        });
    }
}

// ════════════════════════════════════════════════════════════════════════════
// WhatsAppInboundWebFactory
//
// WebApplicationFactory personalizada que:
//   · Reemplaza la BD por EF InMemory (sin Postgres real)
//   · Expone mocks de ITwilioSignatureValidator, ITenantPhoneResolver y
//     IWhatsAppJobQueue para control fino desde los tests
//   · Usa TestableIdempotencyService (compatible con InMemory)
// ════════════════════════════════════════════════════════════════════════════

public sealed class WhatsAppInboundWebFactory : WebApplicationFactory<Program>
{
    public ITwilioSignatureValidator SignatureValidator { get; } =
        Substitute.For<ITwilioSignatureValidator>();

    public ITenantPhoneResolver PhoneResolver { get; } =
        Substitute.For<ITenantPhoneResolver>();

    public IWhatsAppJobQueue JobQueue { get; } =
        Substitute.For<IWhatsAppJobQueue>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Proveer valores válidos de Twilio vía settings para que
        // ValidateOnStart() no falle antes de ConfigureServices.
        builder.UseSetting("Twilio:AccountSid", "ACtest000000000000000000000000000");
        builder.UseSetting("Twilio:AuthToken",  "test_auth_token_32chars_minimum_xx");

        builder.ConfigureServices(services =>
        {
            // ── Reemplazar BD real por EF InMemory ────────────────────────────
            // Eliminamos todos los descriptores que EF registra para Npgsql
            // (incluido IDbContextOptionsConfiguration<AppDbContext>) para evitar
            // el error "two providers registered in same IServiceProvider".
            var efOptionsConfigType = typeof(AppDbContext).Assembly
                .GetReferencedAssemblies()
                .Select(Assembly.Load)
                .Concat([typeof(AppDbContext).Assembly])
                .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
                .FirstOrDefault(t =>
                    t.IsGenericTypeDefinition &&
                    t.Name == "IDbContextOptionsConfiguration`1");

            var typesToRemove = new HashSet<Type>
            {
                typeof(DbContextOptions<AppDbContext>),
                typeof(DbContextOptions),
                typeof(AppDbContext),
                typeof(TenantDbContextInterceptor),
            };

            var dbDescriptors = services
                .Where(d =>
                    typesToRemove.Contains(d.ServiceType) ||
                    (efOptionsConfigType is not null &&
                     d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition() == efOptionsConfigType &&
                     d.ServiceType.GenericTypeArguments.Length == 1 &&
                     d.ServiceType.GenericTypeArguments[0] == typeof(AppDbContext)))
                .ToList();

            foreach (var d in dbDescriptors) services.Remove(d);

            var dbName = "WhatsAppTests_" + Guid.NewGuid().ToString("N");
            services.AddDbContext<AppDbContext>(opts =>
                opts.UseInMemoryDatabase(dbName));

            services.AddScoped<TenantDbContextInterceptor>();

            // ── TwilioOptions: valores de test ────────────────────────────────
            services.RemoveAll<IOptions<TwilioOptions>>();
            services.RemoveAll<IOptionsSnapshot<TwilioOptions>>();
            services.RemoveAll<IOptionsMonitor<TwilioOptions>>();
            services.AddSingleton<IOptions<TwilioOptions>>(Options.Create(new TwilioOptions
            {
                AccountSid     = "ACtest000000000000000000000000000",
                AuthToken      = "test_auth_token_32chars_minimum_xx",
                WebhookBaseUrl = ""
            }));

            // ── Inyectar mocks ────────────────────────────────────────────────
            services.RemoveAll<ITwilioSignatureValidator>();
            services.AddSingleton(SignatureValidator);

            services.RemoveAll<ITenantPhoneResolver>();
            services.AddSingleton(PhoneResolver);

            services.RemoveAll<IWhatsAppJobQueue>();
            services.AddSingleton(JobQueue);

            // ── TestableIdempotencyService (EF puro, sin SQL crudo) ───────────
            services.RemoveAll<IIdempotencyService>();
            services.AddScoped<IIdempotencyService, TestableIdempotencyService>();

            // ── Quitar workers (no los necesitamos en tests de endpoint) ──────
            services.RemoveAll<WhatsAppInboundWorker>();
        });
    }
}
