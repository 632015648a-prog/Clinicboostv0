using System.Net;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// TwilioOutboundMessageSenderTests
//
// Prueba la implementación real de TwilioOutboundMessageSender:
//   · Upsert de Conversation antes del envío.
//   · Creación de Message con status=pending antes de llamar a Twilio.
//   · Actualización a sent/failed según la respuesta del HttpClient.
//   · Manejo de errores HTTP y JSON de Twilio.
//   · Nunca lanza excepciones externas (fallos de Twilio encapsulados).
//
// Estrategia:
//   · AppDbContext InMemory.
//   · HttpClient con DelegatingHandler (FakeHttpMessageHandler) para simular Twilio.
//   · IHttpClientFactory mockeado con NSubstitute.
// ════════════════════════════════════════════════════════════════════════════

public sealed class TwilioOutboundMessageSenderTests : IDisposable
{
    private readonly AppDbContext  _db;
    private readonly Guid          _tenantId = Guid.NewGuid();
    private readonly TwilioOptions _twilioOpts = new()
    {
        AccountSid = "ACtest_account_sid_12345",
        AuthToken  = "test_auth_token_secret_xyz",
    };

    public TwilioOutboundMessageSenderTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TwilioOutboundMessageSender CreateSender(HttpStatusCode httpStatus, string responseBody)
    {
        var handler = new FakeHttpMessageHandler(httpStatus, responseBody);
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        return new TwilioOutboundMessageSender(
            _db,
            Options.Create(_twilioOpts),
            factory,
            NullLogger<TwilioOutboundMessageSender>.Instance);
    }

    private OutboundMessageRequest BuildRequest(
        Guid?   conversationId  = null,
        string? templateSid     = null,
        string? templateVars    = null,
        string? body            = null)
    {
        var patientId = Guid.NewGuid();
        _db.Patients.Add(new Patient
        {
            Id          = patientId,
            TenantId    = _tenantId,
            FullName    = "Test Patient",
            Phone       = "+34600111222",
            Status      = PatientStatus.Active,
            RgpdConsent = true,
        });
        _db.SaveChanges();

        return new OutboundMessageRequest
        {
            ToPhone        = "whatsapp:+34600111222",
            FromPhone      = "whatsapp:+34900000000",
            Channel        = "whatsapp",
            Body           = body,
            TemplateSid    = templateSid,
            TemplateVars   = templateVars,
            FlowId         = "flow_01",
            TenantId       = _tenantId,
            PatientId      = patientId,
            ConversationId = conversationId,
            CorrelationId  = "test-corr-001",
        };
    }

    private static string TwilioSuccessJson(string sid = "SMtest_success_sid_001")
        => $"{{\"sid\":\"{sid}\",\"status\":\"queued\"}}";

    private static string TwilioErrorJson(int code = 30006, string message = "Destination unreachable")
        => $"{{\"code\":{code},\"message\":\"{message}\",\"status\":400}}";

    // ── Test 1: Envío exitoso crea Message con status=sent ────────────────────

    [Fact]
    public async Task SendAsync_TwilioReturns200_MessageStatusIsSentWithTwilioSid()
    {
        // Arrange
        var sender  = CreateSender(HttpStatusCode.Created, TwilioSuccessJson("SMsuccess001"));
        var request = BuildRequest(templateSid: "HXtest_template");

        // Act
        var result = await sender.SendAsync(request);

        // Assert: resultado OK
        result.IsSuccess .Should().BeTrue();
        result.TwilioSid .Should().Be("SMsuccess001");
        result.Status    .Should().Be("sent");
        result.ErrorCode .Should().BeNull();

        // Message en BD con status=sent
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull();
        msg!.Status            .Should().Be("sent");
        msg.ProviderMessageId  .Should().Be("SMsuccess001");
        msg.Direction          .Should().Be("outbound");
        msg.Channel            .Should().Be("whatsapp");
        msg.TemplateId         .Should().Be("HXtest_template");
        msg.SentAt             .Should().NotBeNull();
        msg.TenantId           .Should().Be(_tenantId);
    }

    // ── Test 2: Twilio devuelve error → Message=failed, resultado sin excepción

    [Fact]
    public async Task SendAsync_TwilioReturns400_MessageStatusIsFailedNoException()
    {
        // Arrange
        var sender  = CreateSender(HttpStatusCode.BadRequest, TwilioErrorJson(30006, "Destination unreachable"));
        var request = BuildRequest(body: "Hola, llamaste y no pudimos atenderte.");

        // Act
        var result = await sender.SendAsync(request);

        // Assert: resultado fallo (NO excepción)
        result.IsSuccess  .Should().BeFalse();
        result.ErrorCode  .Should().Be("30006");
        result.ErrorMessage.Should().Contain("unreachable");
        result.TwilioSid  .Should().BeNull();
        result.Status     .Should().Be("failed");

        // Message en BD con status=failed
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull();
        msg!.Status       .Should().Be("failed");
        msg.ErrorCode     .Should().Be("30006");
        msg.ErrorMessage  .Should().Contain("unreachable");
        msg.SentAt        .Should().BeNull();
    }

    // ── Test 3: Twilio 500 → ErrorCode como HTTP status, no lanza ─────────────

    [Fact]
    public async Task SendAsync_Twilio500_CapturesHttpStatusAsErrorCode()
    {
        // Arrange: respuesta de servidor con JSON malformado (no JSON)
        var sender  = CreateSender(HttpStatusCode.InternalServerError, "Internal Server Error");
        var request = BuildRequest(body: "Test body");

        // Act
        var result = await sender.SendAsync(request);

        // Assert
        result.IsSuccess .Should().BeFalse();
        result.ErrorCode .Should().Be("500"); // HTTP status como fallback
    }

    // ── Test 4: Crea conversación si no existe ─────────────────────────────────

    [Fact]
    public async Task SendAsync_NoExistingConversation_CreatesConversationAndMessage()
    {
        // Arrange
        var sender  = CreateSender(HttpStatusCode.Created, TwilioSuccessJson());
        var request = BuildRequest(conversationId: null); // sin conversación preexistente

        // Act
        await sender.SendAsync(request);

        // Assert: se creó una conversación
        var conv = await _db.Conversations.FirstOrDefaultAsync(
            c => c.TenantId == _tenantId && c.FlowId == "flow_01");
        conv.Should().NotBeNull();
        conv!.Status  .Should().Be("open");
        conv.Channel  .Should().Be("whatsapp");

        // El mensaje apunta a esa conversación
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg!.ConversationId.Should().Be(conv.Id);
    }

    // ── Test 5: Reutiliza conversación activa existente ───────────────────────

    [Fact]
    public async Task SendAsync_ActiveConversationExists_ReusesItWithoutCreatingNew()
    {
        // Arrange: crear conversación activa preexistente
        var patientId = Guid.NewGuid();
        _db.Patients.Add(new Patient
        {
            Id = patientId, TenantId = _tenantId, FullName = "Test",
            Phone = "+34600999111", Status = PatientStatus.Active, RgpdConsent = true,
        });
        var existingConv = new Conversation
        {
            TenantId         = _tenantId,
            PatientId        = patientId,
            Channel          = "whatsapp",
            FlowId           = "flow_01",
            Status           = "open",
            SessionExpiresAt = DateTimeOffset.UtcNow.AddHours(20),
        };
        _db.Conversations.Add(existingConv);
        await _db.SaveChangesAsync();

        var sender  = CreateSender(HttpStatusCode.Created, TwilioSuccessJson());
        var request = new OutboundMessageRequest
        {
            ToPhone       = "whatsapp:+34600999111",
            FromPhone     = "whatsapp:+34900000000",
            Channel       = "whatsapp",
            Body          = "Test",
            FlowId        = "flow_01",
            TenantId      = _tenantId,
            PatientId     = patientId,
            CorrelationId = "test-reuse",
        };

        // Act
        await sender.SendAsync(request);

        // Assert: solo existe una conversación (la original reutilizada)
        var convCount = await _db.Conversations.CountAsync(
            c => c.TenantId == _tenantId && c.FlowId == "flow_01");
        convCount.Should().Be(1);

        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg!.ConversationId.Should().Be(existingConv.Id);
    }

    // ── Test 6: ConversationId explícito se respeta (no upsert) ──────────────

    [Fact]
    public async Task SendAsync_ExplicitConversationId_SkipsUpsert()
    {
        // Arrange: crear conversación real en BD
        var patientId  = Guid.NewGuid();
        _db.Patients.Add(new Patient
        {
            Id = patientId, TenantId = _tenantId, FullName = "P",
            Phone = "+34600000099", Status = PatientStatus.Active, RgpdConsent = true,
        });
        var conv = new Conversation
        {
            TenantId         = _tenantId,
            PatientId        = patientId,
            Channel          = "whatsapp",
            FlowId           = "flow_01",
            Status           = "waiting_ai",
            SessionExpiresAt = DateTimeOffset.UtcNow.AddHours(10),
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        var sender  = CreateSender(HttpStatusCode.Created, TwilioSuccessJson("SMexplicit001"));
        var request = new OutboundMessageRequest
        {
            ToPhone        = "whatsapp:+34600000099",
            FromPhone      = "whatsapp:+34900000000",
            Channel        = "whatsapp",
            Body           = "Mensaje de prueba",
            FlowId         = "flow_01",
            TenantId       = _tenantId,
            PatientId      = patientId,
            ConversationId = conv.Id, // explícito
            CorrelationId  = "test-explicit",
        };

        // Act
        var result = await sender.SendAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.TwilioSid.Should().Be("SMexplicit001");

        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg!.ConversationId.Should().Be(conv.Id);

        // Solo una conversación (no creó nueva)
        var convCount = await _db.Conversations.CountAsync();
        convCount.Should().Be(1);
    }

    // ── Test 7: Template Sid y TemplateVars se guardan en Message ─────────────

    [Fact]
    public async Task SendAsync_WithTemplateSid_StoresTemplateSidAndVarsInMessage()
    {
        // Arrange
        var vars    = JsonSerializer.Serialize(new Dictionary<string, string> { ["1"] = "María" });
        var sender  = CreateSender(HttpStatusCode.Created, TwilioSuccessJson());
        var request = BuildRequest(templateSid: "HXtemplate_test_123", templateVars: vars);

        // Act
        await sender.SendAsync(request);

        // Assert
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg!.TemplateId   .Should().Be("HXtemplate_test_123");
        msg.TemplateVars  .Should().Be(vars);
        msg.Body          .Should().BeNull(); // body es null cuando se usa template
    }

    // ── Test 8: Message persiste con status=pending antes del envío ──────────

    [Fact]
    public async Task SendAsync_MessagePersistedAsPendingBeforeTwilioCall()
    {
        // Arrange: handler que cuenta las peticiones y verifica la BD antes de responder
        int? messageCountBeforeTwilioCall = null;
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Created, TwilioSuccessJson(),
            beforeResponse: async () =>
            {
                // En este punto el Message ya debe estar en BD con status=pending
                messageCountBeforeTwilioCall = await _db.Messages.CountAsync();
            });
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        var sender  = new TwilioOutboundMessageSender(_db, Options.Create(_twilioOpts), factory,
            NullLogger<TwilioOutboundMessageSender>.Instance);
        var request = BuildRequest(body: "Mensaje pendiente");

        // Act
        await sender.SendAsync(request);

        // Assert: el Message ya existía antes de recibir la respuesta de Twilio
        messageCountBeforeTwilioCall.Should().Be(1);
        // Y ahora está sent
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg!.Status.Should().Be("sent");
    }
}

// ── FakeHttpMessageHandler ────────────────────────────────────────────────────

/// <summary>
/// Handler falso para simular respuestas de la Twilio API sin hacer peticiones reales.
/// </summary>
internal sealed class FakeHttpMessageHandler : DelegatingHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string         _responseBody;
    private readonly Func<Task>?    _beforeResponse;

    public FakeHttpMessageHandler(
        HttpStatusCode statusCode,
        string         responseBody,
        Func<Task>?    beforeResponse = null)
    {
        _statusCode     = statusCode;
        _responseBody   = responseBody;
        _beforeResponse = beforeResponse;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage   request,
        CancellationToken    ct)
    {
        if (_beforeResponse is not null)
            await _beforeResponse();

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
        };
    }
}
