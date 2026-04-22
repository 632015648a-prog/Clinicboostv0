using ClinicBoost.Api.Features.Conversations;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ConversationInboxServiceSendTests
//
// Prueba ConversationInboxService.SendManualMessageAsync:
//
//   TC-SEND-01  Envío correcto en conversación waiting_human → 200 + TwilioSid.
//   TC-SEND-02  Envío correcto en conversación open → 200.
//   TC-SEND-03  Conversación no encontrada (otro tenant) → null (404).
//   TC-SEND-04  Conversación en estado resolved → ManualSendException 422.
//   TC-SEND-05  Tenant sin WhatsAppNumber → ManualSendException 422.
//   TC-SEND-06  Fallo de Twilio → ManualSendException 502.
//   TC-SEND-07  AiModel queda como "operator" tras envío exitoso.
//
// Estrategia:
//   · AppDbContext InMemory (datos controlados por test).
//   · IOutboundMessageSender mockeado con NSubstitute.
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationInboxServiceSendTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid         _tenantId  = Guid.NewGuid();
    private readonly Guid         _patientId = Guid.NewGuid();
    private const string          WhatsAppNumber = "+34600000001";

    public ConversationInboxServiceSendTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedTenantAsync(string? whatsAppNumber = WhatsAppNumber)
    {
        _db.Tenants.Add(new Tenant
        {
            Id             = _tenantId,
            Name           = "Clínica Test",
            WhatsAppNumber = whatsAppNumber ?? "",
            Slug           = "clinica-test",
            TimeZone       = "Europe/Madrid",
        });
        _db.Patients.Add(new Patient
        {
            Id          = _patientId,
            TenantId    = _tenantId,
            FullName    = "Ana Paciente",
            Phone       = "+34666000001",
            Status      = PatientStatus.Active,
            RgpdConsent = true,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<Conversation> SeedConversationAsync(string status = "waiting_human")
    {
        var conv = new Conversation
        {
            TenantId  = _tenantId,
            PatientId = _patientId,
            Channel   = "whatsapp",
            FlowId    = "flow_00",
            Status    = status,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv;
    }

    private ConversationInboxService BuildService(IOutboundMessageSender? sender = null)
    {
        sender ??= Substitute.For<IOutboundMessageSender>();
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.UserId.Returns(Guid.NewGuid());
        return new ConversationInboxService(
            _db,
            sender,
            tenantCtx,
            NullLogger<ConversationInboxService>.Instance);
    }

    private static IOutboundMessageSender SenderThatSucceeds(Guid messageId, string twilioSid)
    {
        var sender = Substitute.For<IOutboundMessageSender>();
        sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(OutboundSendResult.Success(messageId, twilioSid)));
        return sender;
    }

    private static IOutboundMessageSender SenderThatFails(Guid messageId)
    {
        var sender = Substitute.For<IOutboundMessageSender>();
        sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(OutboundSendResult.TwilioFailure(
                  messageId, "21211", "No phone capability for this destination")));
        return sender;
    }

    // ── Casos de prueba ───────────────────────────────────────────────────────

    // TC-SEND-01: Envío correcto en waiting_human
    [Fact]
    public async Task SendManualMessage_WaitingHuman_ReturnsResponse()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("waiting_human");
        var msgId   = Guid.NewGuid();
        var service = BuildService(SenderThatSucceeds(msgId, "SMtest0001"));

        var result = await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Hola, soy el operador." });

        result.Should().NotBeNull();
        result!.Status.Should().Be("sent");
        result.TwilioSid.Should().Be("SMtest0001");
        result.Direction.Should().Be("outbound");
    }

    // TC-SEND-02: Envío correcto en open
    [Fact]
    public async Task SendManualMessage_OpenStatus_ReturnsResponse()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var msgId   = Guid.NewGuid();
        var service = BuildService(SenderThatSucceeds(msgId, "SMtest0002"));

        var result = await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Mensaje en conv abierta." });

        result.Should().NotBeNull();
        result!.Status.Should().Be("sent");
    }

    // TC-SEND-03: Conversación de otro tenant → null
    [Fact]
    public async Task SendManualMessage_WrongTenant_ReturnsNull()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("open");
        var service = BuildService();

        var result = await service.SendManualMessageAsync(
            Guid.NewGuid(),   // tenant diferente
            conv.Id,
            new SendManualMessageRequest { Body = "Intento cross-tenant." });

        result.Should().BeNull();
    }

    // TC-SEND-04: Estado resolved → ManualSendException 422
    [Fact]
    public async Task SendManualMessage_ResolvedConversation_Throws422()
    {
        await SeedTenantAsync();
        var conv    = await SeedConversationAsync("resolved");
        var service = BuildService();

        var act = async () => await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Mensaje en conv resuelta." });

        await act.Should().ThrowAsync<ManualSendException>()
            .Where(ex => ex.HttpStatusCode == 422);
    }

    // TC-SEND-05: Tenant sin WhatsAppNumber → ManualSendException 422
    [Fact]
    public async Task SendManualMessage_TenantWithoutWhatsAppNumber_Throws422()
    {
        await SeedTenantAsync(whatsAppNumber: "");   // cadena vacía = no configurado
        var conv    = await SeedConversationAsync("waiting_human");
        var service = BuildService();

        var act = async () => await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Hola." });

        await act.Should().ThrowAsync<ManualSendException>()
            .Where(ex => ex.HttpStatusCode == 422 &&
                         ex.Message.Contains("WhatsApp"));
    }

    // TC-SEND-06: Fallo de Twilio → ManualSendException 502
    [Fact]
    public async Task SendManualMessage_TwilioFails_Throws502()
    {
        await SeedTenantAsync();
        var conv      = await SeedConversationAsync("waiting_human");
        var failMsgId = Guid.NewGuid();

        // El sender falla pero crea el Message en BD antes (inmutabilidad)
        // — en el test real no hay acceso a BD del sender mock, así que solo
        //   verificamos el 502 lanzado por el servicio.
        var service = BuildService(SenderThatFails(failMsgId));

        var act = async () => await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Mensaje que fallará." });

        await act.Should().ThrowAsync<ManualSendException>()
            .Where(ex => ex.HttpStatusCode == 502);
    }

    // TC-SEND-07: AiModel="operator" guardado tras envío exitoso
    [Fact]
    public async Task SendManualMessage_Success_SetsAiModelToOperator()
    {
        await SeedTenantAsync();
        var conv  = await SeedConversationAsync("waiting_human");

        // Crear el Message manualmente como lo haría el sender real
        // (el mock no escribe en DB, así que lo simulamos)
        var msgId = Guid.NewGuid();
        _db.Messages.Add(new Message
        {
            Id             = msgId,
            TenantId       = _tenantId,
            ConversationId = conv.Id,
            Direction      = "outbound",
            Channel        = "whatsapp",
            Status         = "sent",
            GeneratedByAi  = false,
            AiModel        = null,   // todavía null antes del patch del servicio
        });
        await _db.SaveChangesAsync();

        var sender = Substitute.For<IOutboundMessageSender>();
        sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(OutboundSendResult.Success(msgId, "SMtest0007")));

        var service = BuildService(sender);
        await service.SendManualMessageAsync(
            _tenantId, conv.Id,
            new SendManualMessageRequest { Body = "Verificando trazabilidad." });

        // Refrescar desde BD
        var msg = await _db.Messages.FindAsync(msgId);
        msg.Should().NotBeNull();
        msg!.AiModel.Should().Be("operator");
    }
}
