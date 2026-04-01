using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-06: Webhook de estado Twilio → messages y delivery events se actualizan
//
// OBJETIVO
//   Verificar que el pipeline de actualizaciones de estado de mensaje funciona:
//   Twilio POST /webhooks/whatsapp/status → MessageStatusService.ProcessAsync →
//   · MessageDeliveryEvent insertado (inmutable, insert-only)
//   · Message.Status actualizado con regla de no-regresión
//   · Timestamps SentAt / DeliveredAt / ReadAt establecidos en el momento correcto
//   · Campos de error (ErrorCode, ErrorMessage) en status=failed/undelivered
//
// REGLA DE NO-REGRESIÓN
//   El estado de un mensaje solo puede avanzar: pending→sent→delivered→read
//   Nunca puede retrogradar (e.g., un "sent" no puede volver a "pending").
//   La única excepción es "failed" que puede sobreescribir cualquier estado.
//
// PRE-CONDITIONS
//   · Message existente con ProviderMessageId (Twilio SID) y status="sent"
//
// DATOS DE PRUEBA
//   · ProviderMessageId: SMsmoke_tc06_*
//   · Estados de prueba: sent → delivered → read → failed (no-regresión)
//
// QUÉ SE AUTOMATIZA
//   ✅ Inserción de MessageDeliveryEvent para sent/delivered/read/failed
//   ✅ Regla de no-regresión (delivered no regresa a sent)
//   ✅ Timestamps SentAt / DeliveredAt / ReadAt
//   ✅ Campos de error en failed/undelivered
//   ✅ Comportamiento si MessageSid no existe en BD (MessageId=null)
//   ✅ Correlación correcta: MessageId + ConversationId en el delivery event
//   ✅ Aislamiento multi-tenant en delivery events
//
// QUÉ SE VALIDA MANUALMENTE
//   ⚠ Que el webhook de Twilio envía la firma correcta (X-Twilio-Signature)
//   ⚠ Que los timestamps del webhook coinciden con los de la BD
//   ⚠ Que los errores de Twilio (código 30006, 30007, etc.) son los esperados
//   ⚠ Que el dashboard refleja los estados de entrega en tiempo real
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-06")]
public sealed class TC06_TwilioStatusWebhookTests : SmokeTestDb
{
    // ── Helpers de construcción ───────────────────────────────────────────────

    /// <summary>
    /// Construye el SUT con un IIdempotencyService que permite todos los eventos
    /// (simula primera recepción, no duplicado).
    /// </summary>
    private MessageStatusService BuildSut() =>
        BuildSut(SmokeFixtures.BuildIdempotencyAllowAll());

    /// <summary>
    /// Construye el SUT con un IIdempotencyService personalizado para tests de
    /// comportamiento de idempotencia (TC-06-F).
    /// </summary>
    private MessageStatusService BuildSut(IIdempotencyService idempotency)
    {
        var variantTracking = Substitute.For<IVariantTrackingService>();
        return new MessageStatusService(
            Db,
            variantTracking,
            idempotency,
            NullLogger<MessageStatusService>.Instance);
    }

    private static TwilioMessageStatusRequest BuildRequest(
        string  sid,
        string  status,
        string  from         = "whatsapp:+34910000001",
        string? errorCode    = null,
        string? errorMessage = null) => new()
    {
        MessageSid    = sid,
        MessageStatus = status,
        AccountSid    = "ACsmoke_tc06",
        From          = from,
        To            = "whatsapp:+34600111222",
        ErrorCode     = errorCode,
        ErrorMessage  = errorMessage,
    };

    private async Task<Message> SeedMessageAsync(
        string messageSid = "SMsmoke_tc06_001",
        string status     = "sent")
    {
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var conv    = await SmokeFixtures.SeedConversationAsync(Db, TenantId, patient.Id);

        var msg = new Message
        {
            TenantId          = TenantId,
            ConversationId    = conv.Id,
            Direction         = "outbound",
            Channel           = "whatsapp",
            ProviderMessageId = messageSid,
            Body              = "Hola Ana, le llamo de Fisioterapia Ramírez...",
            Status            = status,
        };
        Db.Messages.Add(msg);
        await Db.SaveChangesAsync();
        return msg;
    }

    // ── TC-06-A: Transición sent → delivered ─────────────────────────────────

    [Fact(DisplayName = "TC-06-A: sent→delivered → DeliveryEvent insertado + Message.Status=delivered")]
    public async Task TC06A_SentToDelivered_EventInsertedAndMessageUpdated()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────
        var msg = await SeedMessageAsync("SMsmoke_tc06_A", "sent");
        var sut = BuildSut();
        var req = BuildRequest("SMsmoke_tc06_A", "delivered");

        // ── ACT ──────────────────────────────────────────────────────────────
        await sut.ProcessAsync(TenantId, req);

        // ── ASSERT ───────────────────────────────────────────────────────────

        // 1. MessageDeliveryEvent insertado (inmutable, insert-only)
        var delivEvent = await Db.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.ProviderMessageId == "SMsmoke_tc06_A");
        delivEvent.Should().NotBeNull("cada cambio de estado genera un DeliveryEvent");
        delivEvent!.Status    .Should().Be("delivered");
        delivEvent.MessageId  .Should().Be(msg.Id,
            "el DeliveryEvent debe correlacionarse con el Message por Id");
        delivEvent.ConversationId.Should().Be(msg.ConversationId,
            "el DeliveryEvent debe incluir el ConversationId para análisis de funnel");
        delivEvent.TenantId  .Should().Be(TenantId,
            "aislamiento multi-tenant obligatorio en DeliveryEvents");
        delivEvent.Channel   .Should().Be("whatsapp");

        // 2. Message.Status actualizado a "delivered"
        var updatedMsg = await Db.Messages.FindAsync(msg.Id);
        updatedMsg!.Status     .Should().Be("delivered");
        updatedMsg.DeliveredAt .Should().NotBeNull(
            "el timestamp DeliveredAt debe establecerse al confirmar entrega");
    }

    // ── TC-06-B: Transición delivered → read ─────────────────────────────────

    [Fact(DisplayName = "TC-06-B: delivered→read → ReadAt establecido")]
    public async Task TC06B_DeliveredToRead_ReadAtTimestampSet()
    {
        var msg = await SeedMessageAsync("SMsmoke_tc06_B", "delivered");
        var sut = BuildSut();

        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_B", "read"));

        var updatedMsg = await Db.Messages.FindAsync(msg.Id);
        updatedMsg!.Status .Should().Be("read");
        updatedMsg.ReadAt  .Should().NotBeNull(
            "ReadAt debe establecerse cuando el paciente lee el mensaje");
    }

    // ── TC-06-C: Regla de no-regresión — read no vuelve a delivered ───────────

    [Fact(DisplayName = "TC-06-C: no-regresión — read no retrocede a delivered")]
    public async Task TC06C_NoRegression_ReadDoesNotRevertToDelivered()
    {
        var msg = await SeedMessageAsync("SMsmoke_tc06_C", "read");
        var sut = BuildSut();

        // Intentar aplicar un estado inferior (delivered < read)
        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_C", "delivered"));

        var updatedMsg = await Db.Messages.FindAsync(msg.Id);
        updatedMsg!.Status.Should().Be("read",
            "no-regresión: el estado no puede retroceder de 'read' a 'delivered'");

        // Pero el DeliveryEvent SÍ se inserta (log inmutable de todos los eventos)
        var events = await Db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == "SMsmoke_tc06_C")
            .ToListAsync();
        events.Should().HaveCount(1,
            "el DeliveryEvent se inserta incluso si no actualiza el Message.Status");
        events[0].Status.Should().Be("delivered",
            "el evento de entrega refleja el estado recibido de Twilio, no el estado del mensaje");
    }

    // ── TC-06-D: Status=failed → ErrorCode y ErrorMessage guardados ───────────

    [Fact(DisplayName = "TC-06-D: failed → ErrorCode + ErrorMessage en Message y DeliveryEvent")]
    public async Task TC06D_Failed_ErrorFieldsPersistedInBothTables()
    {
        var msg = await SeedMessageAsync("SMsmoke_tc06_D", "sent");
        var sut = BuildSut();

        await sut.ProcessAsync(TenantId, BuildRequest(
            "SMsmoke_tc06_D", "failed",
            errorCode:    "30006",
            errorMessage: "Destination unreachable. Twilio is unable to route this message."));

        // Message actualizado con error
        var updatedMsg = await Db.Messages.FindAsync(msg.Id);
        updatedMsg!.Status      .Should().Be("failed");
        updatedMsg.ErrorCode    .Should().Be("30006");
        updatedMsg.ErrorMessage .Should().Contain("unreachable");

        // DeliveryEvent con campos de error
        var delivEvent = await Db.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.ProviderMessageId == "SMsmoke_tc06_D");
        delivEvent.Should().NotBeNull();
        delivEvent!.Status      .Should().Be("failed");
        delivEvent.ErrorCode    .Should().Be("30006");
        delivEvent.ErrorMessage .Should().NotBeNullOrEmpty();
    }

    // ── TC-06-E: ProviderMessageSid desconocido → DeliveryEvent sin MessageId ─

    [Fact(DisplayName = "TC-06-E: SID desconocido → DeliveryEvent insertado con MessageId=null")]
    public async Task TC06E_UnknownSid_DeliveryEventInsertedWithNullMessageId()
    {
        // No seeding de mensaje — el SID no existe en BD
        var sut = BuildSut();

        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_E_unknown", "delivered"));

        // El DeliveryEvent se inserta (para auditoría) aunque no haya Message correlacionado
        var delivEvent = await Db.MessageDeliveryEvents
            .FirstOrDefaultAsync(e => e.ProviderMessageId == "SMsmoke_tc06_E_unknown");
        delivEvent.Should().NotBeNull(
            "se debe insertar el DeliveryEvent aunque no se encuentre el Message en BD");
        delivEvent!.MessageId.Should().BeNull(
            "si el SID no corresponde a ningún mensaje conocido, MessageId es null");
        delivEvent.Status.Should().Be("delivered");
    }

    // ── TC-06-F: Múltiples webhooks para el mismo SID → múltiples events ──────

    [Fact(DisplayName = "TC-06-F: múltiples webhooks mismo SID → múltiples DeliveryEvents (log completo)")]
    public async Task TC06F_MultipleWebhooksForSameSid_MultipleEventsStored()
    {
        var msg = await SeedMessageAsync("SMsmoke_tc06_F", "sent");
        var sut = BuildSut();

        // Secuencia de estados como los enviaría Twilio
        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_F", "sent"));
        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_F", "delivered"));
        await sut.ProcessAsync(TenantId, BuildRequest("SMsmoke_tc06_F", "read"));

        // Deben existir 3 DeliveryEvents (registro inmutable de cada cambio)
        var events = await Db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == "SMsmoke_tc06_F")
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        events.Should().HaveCount(3,
            "cada webhook de Twilio genera un DeliveryEvent independiente (log inmutable)");
        events[0].Status.Should().Be("sent");
        events[1].Status.Should().Be("delivered");
        events[2].Status.Should().Be("read");

        // El Message.Status final es "read" (el más avanzado)
        var finalMsg = await Db.Messages.FindAsync(msg.Id);
        finalMsg!.Status.Should().Be("read",
            "el estado final del mensaje debe ser el más avanzado recibido");
    }

    // ── TC-06-H: Webhook duplicado exacto → idempotencia, sin DeliveryEvent doble
    //
    // SEC-02 / GAP-04 RESUELTO: MessageStatusService ahora usa IIdempotencyService.
    // Clave: "twilio.message_status" + "{MessageSid}_{MessageStatus}".
    // Si Twilio re-entrega exactamente el mismo evento (mismo SID + mismo status),
    // debe ignorarse → no se crea un DeliveryEvent duplicado.

    [Fact(DisplayName = "TC-06-H: webhook duplicado exacto → idempotencia activa, sin DeliveryEvent doble")]
    public async Task TC06H_DuplicateWebhook_IdempotencyPreventsDoubleInsert()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────
        var msg = await SeedMessageAsync("SMsmoke_tc06_H", "sent");

        // Mock de idempotencia con comportamiento real:
        // primera llamada → ShouldProcess=true
        // segunda llamada (mismo SID+status) → ShouldProcess=false (duplicado)
        var idempotency = Substitute.For<IIdempotencyService>();
        var firstCall   = true;
        idempotency
            .TryProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(),  Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return Task.FromResult(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
                }
                // Segunda llamada: evento ya procesado
                return Task.FromResult(IdempotencyResult.Duplicate(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1)));
            });

        var sut = BuildSut(idempotency);
        var req = BuildRequest("SMsmoke_tc06_H", "delivered");

        // ── ACT: primera entrega (procesada) ──────────────────────────────────
        await sut.ProcessAsync(TenantId, req);

        // ── ACT: re-entrega exacta (duplicado — Twilio reintenta por timeout) ──
        // La idempotencia bloquea la segunda ejecución.
        await sut.ProcessAsync(TenantId, req);

        // ── ASSERT ───────────────────────────────────────────────────────────

        // Solo debe existir UN DeliveryEvent, no dos
        var events = await Db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == "SMsmoke_tc06_H")
            .ToListAsync();
        events.Should().HaveCount(1,
            "SEC-02/GAP-04: un webhook duplicado exacto (mismo SID + mismo status) " +
            "no debe generar un segundo DeliveryEvent");

        // El Message.Status sigue siendo "delivered" (correcto)
        var updatedMsg = await Db.Messages.FindAsync(msg.Id);
        updatedMsg!.Status.Should().Be("delivered");
    }

    // ── TC-06-G: Aislamiento multi-tenant en DeliveryEvents ──────────────────
    [Fact(DisplayName = "TC-06-G: DeliveryEvents filtrados por TenantId — no hay cross-tenant")]
    public async Task TC06G_DeliveryEvents_IsolatedByTenant()
    {
        // ARRANGE: tenant A y B con mensajes diferentes
        var tenantA = TenantId;
        var tenantB = Guid.NewGuid();

        // Mensaje de tenant A
        var patA = await SmokeFixtures.SeedPatientAsync(Db, tenantA,
            phone: "+34600100001", fullName: "Paciente Tenant A");
        var convA = await SmokeFixtures.SeedConversationAsync(Db, tenantA, patA.Id);
        var msgA  = new Message
        {
            TenantId = tenantA, ConversationId = convA.Id,
            Direction = "outbound", Channel = "whatsapp",
            ProviderMessageId = "SMsmoke_tc06_G_A",
            Body = "Mensaje Tenant A", Status = "sent",
        };
        Db.Messages.Add(msgA);

        // Mensaje de tenant B
        var patB = await SmokeFixtures.SeedPatientAsync(Db, tenantB,
            phone: "+34600200002", fullName: "Paciente Tenant B");
        var convB = await SmokeFixtures.SeedConversationAsync(Db, tenantB, patB.Id);
        var msgB  = new Message
        {
            TenantId = tenantB, ConversationId = convB.Id,
            Direction = "outbound", Channel = "whatsapp",
            ProviderMessageId = "SMsmoke_tc06_G_B",
            Body = "Mensaje Tenant B", Status = "sent",
        };
        Db.Messages.Add(msgB);
        await Db.SaveChangesAsync();

        var sut = BuildSut();

        // ACT: webhook para mensaje de tenant A únicamente
        await sut.ProcessAsync(tenantA, BuildRequest("SMsmoke_tc06_G_A", "delivered"));

        // ASSERT: solo el delivery event de tenant A existe
        var eventsA = await Db.MessageDeliveryEvents
            .Where(e => e.TenantId == tenantA).ToListAsync();
        var eventsB = await Db.MessageDeliveryEvents
            .Where(e => e.TenantId == tenantB).ToListAsync();

        eventsA.Should().HaveCount(1, "tenant A debe tener su DeliveryEvent");
        eventsB.Should().BeEmpty("tenant B no debe tener DeliveryEvents todavía");

        eventsA.Should().OnlyContain(e => e.TenantId == tenantA,
            "CRÍTICO: ningún DeliveryEvent de tenant A debe pertenecer a tenant B");
    }
}
