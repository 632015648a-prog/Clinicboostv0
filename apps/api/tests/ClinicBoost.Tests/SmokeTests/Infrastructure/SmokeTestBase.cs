using System.Net;
using System.Text;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace ClinicBoost.Tests.SmokeTests.Infrastructure;

// ════════════════════════════════════════════════════════════════════════════
// SmokeTestDb
//
// Base class para todos los smoke tests E2E.
// Proporciona un AppDbContext con EF InMemory aislado por test
// (nombre de BD único por instancia) y helpers de fixtures.
//
// PATRÓN:
//   · Cada test class hereda de SmokeTestDb y llama a Dispose().
//   · Las fixtures crean datos mínimos y realistas para cada caso.
//   · Los servicios externos (Twilio, OpenAI) se sustituyen por FakeHandlers.
// ════════════════════════════════════════════════════════════════════════════

public abstract class SmokeTestDb : IDisposable
{
    protected readonly AppDbContext Db;
    protected readonly Guid         TenantId = Guid.NewGuid();

    protected SmokeTestDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"smoke_{GetType().Name}_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId
                    .TransactionIgnoredWarning))
            .Options;
        Db = new AppDbContext(opts);
    }

    public void Dispose() => Db.Dispose();
}

// ════════════════════════════════════════════════════════════════════════════
// SmokeFixtures
//
// Factorías de datos de prueba realistas.
// Todos los datos son ficticios — no relacionados con personas reales.
// ════════════════════════════════════════════════════════════════════════════

public static class SmokeFixtures
{
    // ── Tenant ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Crea y persiste un Tenant de staging con timezone configurable.
    /// Por defecto usa "Europe/Madrid" (España peninsular, UTC+1/UTC+2 DST).
    /// </summary>
    public static async Task<Tenant> SeedTenantAsync(
        AppDbContext db,
        Guid         tenantId,
        string       timeZone      = "Europe/Madrid",
        string       whatsAppNumber = "+34910000001")
    {
        var tenant = new Tenant
        {
            Id             = tenantId,
            Name           = "Fisioterapia Ramírez (Smoke)",
            Slug           = $"ramirez-smoke-{tenantId.ToString("N")[..8]}",
            TimeZone       = timeZone,
            WhatsAppNumber = whatsAppNumber,
            IsActive       = true,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    // ── Paciente ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Crea y persiste un paciente activo con consentimiento RGPD.
    /// Número de teléfono en formato E.164.
    /// </summary>
    public static async Task<Patient> SeedPatientAsync(
        AppDbContext db,
        Guid         tenantId,
        string       phone       = "+34600111222",
        string       fullName    = "Ana García López",
        bool         rgpdConsent = true,
        PatientStatus status     = PatientStatus.Active)
    {
        var patient = new Patient
        {
            TenantId    = tenantId,
            FullName    = fullName,
            Phone       = phone,
            Status      = status,
            RgpdConsent = rgpdConsent,
        };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        return patient;
    }

    // ── Conversación ─────────────────────────────────────────────────────────

    /// <summary>
    /// Crea y persiste una conversación abierta con el estado dado.
    /// </summary>
    public static async Task<Conversation> SeedConversationAsync(
        AppDbContext db,
        Guid         tenantId,
        Guid         patientId,
        string       status  = "open",
        string       flowId  = "flow_00",
        string       channel = "whatsapp")
    {
        var conv = new Conversation
        {
            TenantId   = tenantId,
            PatientId  = patientId,
            Channel    = channel,
            FlowId     = flowId,
            Status     = status,
            AiContext  = "{}",
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv;
    }

    // ── Mensaje ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Crea y persiste un Message outbound con status "sent".
    /// </summary>
    public static async Task<Message> SeedOutboundMessageAsync(
        AppDbContext db,
        Guid         tenantId,
        Guid         conversationId,
        string       sid    = "SMsmoke_outbound_001",
        string       body   = "Hola, le llamamos de Fisioterapia Ramírez...",
        string       status = "sent")
    {
        var msg = new Message
        {
            TenantId          = tenantId,
            ConversationId    = conversationId,
            Direction         = "outbound",
            Channel           = "whatsapp",
            ProviderMessageId = sid,
            Body              = body,
            Status            = status,
            SentAt            = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
        return msg;
    }

    // ── RuleConfig ────────────────────────────────────────────────────────────

    /// <summary>
    /// Persiste una RuleConfig para el tenant dado.
    /// </summary>
    public static async Task<RuleConfig> SeedRuleConfigAsync(
        AppDbContext db,
        Guid         tenantId,
        string       flowId    = "global",
        string       ruleKey   = "discount_max_pct",
        string       ruleValue = "15",
        string       valueType = "decimal")
    {
        var rule = new RuleConfig
        {
            TenantId  = tenantId,
            FlowId    = flowId,
            RuleKey   = ruleKey,
            RuleValue = ruleValue,
            ValueType = valueType,
            IsActive  = true,
        };
        db.RuleConfigs.Add(rule);
        await db.SaveChangesAsync();
        return rule;
    }

    // ── Helpers de idempotencia ───────────────────────────────────────────────

    /// <summary>
    /// Crea un IIdempotencyService mockeado que siempre devuelve "evento nuevo"
    /// (permite todo procesamiento). Útil en la mayoría de smoke tests donde
    /// no se quiere testear la idempotencia en sí.
    /// </summary>
    public static IIdempotencyService BuildIdempotencyAllowAll()
    {
        var svc = Substitute.For<IIdempotencyService>();

        // Sobrecarga sin payload tipado
        svc.TryProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(),  Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));

        // Sobrecarga tipada genérica
        svc.TryProcessAsync<object>(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
           .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));

        return svc;
    }

    // ── Fake HTTP handlers ────────────────────────────────────────────────────

    /// <summary>
    /// Crea un HttpMessageHandler que devuelve siempre la respuesta dada.
    /// Usado para falsear llamadas a Twilio y OpenAI.
    /// </summary>
    public static HttpMessageHandler TwilioOkHandler(string? sid = null)
    {
        var effectiveSid = sid ?? ("SMsmoke_" + Guid.NewGuid().ToString("N")[..12]);
        var body = $"{{\"sid\":\"{effectiveSid}\",\"status\":\"queued\",\"to\":\"whatsapp:+34600111222\"}}";
        return new StaticFakeHandler(HttpStatusCode.Created, body);
    }

    public static HttpMessageHandler TwilioErrorHandler(int twilioCode = 30006) =>
        new StaticFakeHandler(
            HttpStatusCode.BadRequest,
            $$"""{"code":{{twilioCode}},"message":"Destination unreachable","more_info":"https://www.twilio.com/docs/errors/{{twilioCode}}"}""");

    /// <summary>
    /// Handler para OpenAI que devuelve una clasificación de intención y
    /// una respuesta de texto en secuencia (1ª = classify, 2ª = main response).
    /// </summary>
    public static HttpMessageHandler OpenAiBookingHandler() =>
        new SequentialFakeHandler([
            // 1ª llamada: clasificador de intención → BookAppointment con alta confianza
            """{"choices":[{"message":{"role":"assistant","content":"{\"intent\":\"BookAppointment\",\"confidence\":0.95,\"reasoning\":\"El paciente quiere reservar una cita\"}"}}]}""",
            // 2ª llamada: respuesta principal del agente
            """{"choices":[{"message":{"role":"assistant","content":"Perfecto, le reservo una cita para el martes a las 10:00. ¿Le parece bien?"}}],"usage":{"prompt_tokens":150,"completion_tokens":40}}"""
        ]);

    public static HttpMessageHandler OpenAiHumanHandoffHandler() =>
        new SequentialFakeHandler([
            // Clasificación: Queja → escalar directamente
            """{"choices":[{"message":{"role":"assistant","content":"{\"intent\":\"Complaint\",\"confidence\":0.98,\"reasoning\":\"El paciente está molesto con el servicio\"}"}}]}"""
        ]);

    public static HttpMessageHandler OpenAiOutOfHoursHandler() =>
        new SequentialFakeHandler([
            // Fuera de horario: el agente responde que no puede atender ahora
            """{"choices":[{"message":{"role":"assistant","content":"{\"intent\":\"Greeting\",\"confidence\":0.90,\"reasoning\":\"Saludo estándar\"}"}}]}""",
            """{"choices":[{"message":{"role":"assistant","content":"Gracias por contactarnos. Nuestro horario de atención es de 9:00 a 19:00 (hora de Madrid). Te responderemos en cuanto abramos. 😊"}}],"usage":{"prompt_tokens":100,"completion_tokens":30}}"""
        ]);
}

// ════════════════════════════════════════════════════════════════════════════
// HTTP Fake Handlers compartidos
// (versiones internas para evitar conflictos con las definiciones en otros tests)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Devuelve siempre la misma respuesta HTTP estática.
/// </summary>
internal sealed class StaticFakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string         _body;

    public StaticFakeHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body   = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        });
}

/// <summary>
/// Devuelve las respuestas en orden: 1ª llamada = responses[0], 2ª = responses[1], etc.
/// Después del último elemento, devuelve el último repetido.
/// </summary>
internal sealed class SequentialFakeHandler : HttpMessageHandler
{
    private readonly string[] _responses;
    private          int      _index;

    public SequentialFakeHandler(string[] responses)
        => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = _responses[Math.Min(_index++, _responses.Length - 1)];
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// Handler que captura la request enviada para assertions posteriores.
/// </summary>
internal sealed class CapturingFakeHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string         _body;

    public HttpRequestMessage?  LastRequest { get; private set; }
    public string?              LastRequestBody { get; private set; }

    public CapturingFakeHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body   = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}
