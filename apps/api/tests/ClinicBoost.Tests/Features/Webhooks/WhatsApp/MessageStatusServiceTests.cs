using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusServiceTests
//
// Tests unitarios de MessageStatusService sobre EF InMemory.
//
// COBERTURA:
//   · Inserción de MessageDeliveryEvent para cada estado (sent/delivered/read/failed)
//   · Actualización de Message.Status con regla de no-regresión
//   · Correlación MessageId / ConversationId en el evento de entregabilidad
//   · Comportamiento cuando el Message no existe en BD (MessageId null)
//   · Campos de error en failed/undelivered
//   · Timestamps SentAt / DeliveredAt / ReadAt establecidos correctamente
// ════════════════════════════════════════════════════════════════════════════

public sealed class MessageStatusServiceTests
{
    // ── Setup helpers ─────────────────────────────────────────────────────────

    private static AppDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("MsgStatusSvc_" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(opts);
    }

    private static MessageStatusService BuildSut(AppDbContext db)
    {
        var variantTracking = Substitute.For<IVariantTrackingService>();
        // GAP-04 / SEC-02: idempotency mock permite procesamiento por defecto (NewEvent).
        // Para tests de re-entrega, usar BuildSutWithDuplicateIdempotency.
        var idempotency = Substitute.For<IIdempotencyService>();
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        return new MessageStatusService(
            db, variantTracking, idempotency, NullLogger<MessageStatusService>.Instance);
    }

    /// <summary>Crea y persiste un Message de prueba con status "sent".</summary>
    private static async Task<Message> SeedMessageAsync(
        AppDbContext db,
        Guid         tenantId,
        string       messageSid,
        string       status = "sent")
    {
        var msg = new Message
        {
            TenantId          = tenantId,
            ConversationId    = Guid.NewGuid(),
            Direction         = "outbound",
            Channel           = "whatsapp",
            ProviderMessageId = messageSid,
            Body              = "Mensaje de prueba",
            Status            = status,
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
        return msg;
    }

    private static TwilioMessageStatusRequest BuildRequest(
        string  sid,
        string  status,
        string  from          = "whatsapp:+34910000001",
        string? errorCode     = null,
        string? errorMessage  = null) => new()
    {
        MessageSid    = sid,
        MessageStatus = status,
        AccountSid    = "ACtest",
        From          = from,
        To            = "whatsapp:+34612000001",
        ErrorCode     = errorCode,
        ErrorMessage  = errorMessage,
    };

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: MessageDeliveryEvent — inserción y campos
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_InsertsDeliveryEvent_ForDeliveredStatus()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMdeliv001";

        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));

        var evt = await db.MessageDeliveryEvents
            .SingleOrDefaultAsync(e => e.ProviderMessageId == sid);

        evt.Should().NotBeNull();
        evt!.TenantId.Should().Be(tenant);
        evt.Status.Should().Be("delivered");
        evt.Channel.Should().Be("whatsapp");
        evt.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow,
            precision: TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProcessAsync_InsertsDeliveryEvent_ForReadStatus()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMread001";

        await sut.ProcessAsync(tenant, BuildRequest(sid, "read"));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == sid);

        evt.Status.Should().Be("read");
    }

    [Fact]
    public async Task ProcessAsync_InsertsDeliveryEvent_WithErrorFields_OnFailed()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMfail001";

        await sut.ProcessAsync(tenant,
            BuildRequest(sid, "failed",
                errorCode:    "30008",
                errorMessage: "Unknown destination handset"));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == sid);

        evt.Status.Should().Be("failed");
        evt.ErrorCode.Should().Be("30008");
        evt.ErrorMessage.Should().Be("Unknown destination handset");
    }

    [Fact]
    public async Task ProcessAsync_InsertsDeliveryEvent_WithErrorFields_OnUndelivered()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMundlv001";

        await sut.ProcessAsync(tenant,
            BuildRequest(sid, "undelivered",
                errorCode: "30006", errorMessage: "Landline or unreachable"));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == sid);

        evt.Status.Should().Be("undelivered");
        evt.ErrorCode.Should().Be("30006");
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("delivered")]
    [InlineData("read")]
    [InlineData("failed")]
    [InlineData("undelivered")]
    public async Task ProcessAsync_InsertsExactlyOneEvent_PerCall(string status)
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = $"SM_{status}_001";

        await sut.ProcessAsync(tenant, BuildRequest(sid, status));

        var count = await db.MessageDeliveryEvents.CountAsync();
        count.Should().Be(1);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Correlación MessageId / ConversationId
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_SetsMessageId_WhenMessageExistsInDb()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMcorr001";

        var msg = await SeedMessageAsync(db, tenant, sid);

        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == sid);

        evt.MessageId.Should().Be(msg.Id,
            "el DeliveryEvent debe correlacionarse con el Message interno");
        evt.ConversationId.Should().Be(msg.ConversationId,
            "el ConversationId se copia del Message para JOIN con conversations");
    }

    [Fact]
    public async Task ProcessAsync_SetsMessageIdNull_WhenMessageNotInDb()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();

        // No se hace seed de ningún Message
        await sut.ProcessAsync(tenant, BuildRequest("SMorphan001", "delivered"));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == "SMorphan001");

        evt.MessageId.Should().BeNull(
            "si el Message no está en BD, el evento se registra sin MessageId");
        evt.ConversationId.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Actualización de Message.Status y timestamps
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_UpdatesMessageStatus_ToDelivered()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMupd001";

        var msg = await SeedMessageAsync(db, tenant, sid, "sent");

        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("delivered");
        msg.DeliveredAt.Should().NotBeNull();
        msg.SentAt.Should().NotBeNull(
            "SentAt debe estar establecido al llegar 'delivered' si aún era null");
    }

    [Fact]
    public async Task ProcessAsync_UpdatesMessageStatus_ToRead_AndSetsAllTimestamps()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMread002";

        var msg = await SeedMessageAsync(db, tenant, sid, "sent");

        await sut.ProcessAsync(tenant, BuildRequest(sid, "read"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("read");
        msg.SentAt.Should().NotBeNull();
        msg.DeliveredAt.Should().NotBeNull(
            "al llegar 'read' sin 'delivered' previo, DeliveredAt debe rellenarse");
        msg.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_UpdatesMessageStatus_ToFailed_WithErrorFields()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMfail002";

        var msg = await SeedMessageAsync(db, tenant, sid, "sent");

        await sut.ProcessAsync(tenant,
            BuildRequest(sid, "failed",
                errorCode: "30003", errorMessage: "Unreachable destination handset"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("failed");
        msg.ErrorCode.Should().Be("30003");
        msg.ErrorMessage.Should().Be("Unreachable destination handset");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Regla de no-regresión de estado
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_DoesNotRegressStatus_FromReadToDelivered()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMnoregress001";

        // Estado inicial ya es "read" (el más avanzado del ciclo normal)
        var msg = await SeedMessageAsync(db, tenant, sid, "read");

        // Intentar retroceder a "delivered"
        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("read",
            "un callback 'delivered' no debe retroceder el estado desde 'read'");
    }

    [Fact]
    public async Task ProcessAsync_DoesNotRegressStatus_FromDeliveredToSent()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMnoregress002";

        var msg = await SeedMessageAsync(db, tenant, sid, "delivered");

        await sut.ProcessAsync(tenant, BuildRequest(sid, "sent"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("delivered",
            "'sent' no debe retroceder el estado desde 'delivered'");
    }

    [Fact]
    public async Task ProcessAsync_AppliesFailed_EvenIfCurrentIsDelivered()
    {
        // Los estados terminales (failed/undelivered) siempre se aplican,
        // independientemente del estado actual. Esto modela una situación
        // donde el mensaje fue marcado como delivered por un nodo pero
        // luego Twilio reportó un error definitivo.
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMterminal001";

        var msg = await SeedMessageAsync(db, tenant, sid, "delivered");

        await sut.ProcessAsync(tenant,
            BuildRequest(sid, "failed",
                errorCode: "30005", errorMessage: "Unknown destination number"));

        await db.Entry(msg).ReloadAsync();
        msg.Status.Should().Be("failed",
            "los estados terminales siempre se aplican sobre cualquier estado previo");
        msg.ErrorCode.Should().Be("30005");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Canal detectado desde el prefijo del número
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("whatsapp:+34910000001", "whatsapp")]
    [InlineData("+34910000001",          "sms")]
    public async Task ProcessAsync_DetectsChannel_FromFromField(
        string from, string expectedChannel)
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = $"SMchan_{expectedChannel}_001";

        await sut.ProcessAsync(tenant,
            BuildRequest(sid, "delivered", from: from));

        var evt = await db.MessageDeliveryEvents
            .SingleAsync(e => e.ProviderMessageId == sid);

        evt.Channel.Should().Be(expectedChannel);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 6: Múltiples transiciones del mismo MessageSid
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_InsertsMultipleEvents_ForDifferentStatusTransitions()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       sid    = "SMmulti001";

        await SeedMessageAsync(db, tenant, sid, "pending");

        // Simular el ciclo completo: sent → delivered → read
        await sut.ProcessAsync(tenant, BuildRequest(sid, "sent"));
        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));
        await sut.ProcessAsync(tenant, BuildRequest(sid, "read"));

        var events = await db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == sid)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        events.Should().HaveCount(3,
            "cada transición de estado debe generar un DeliveryEvent independiente");
        events.Select(e => e.Status).Should()
            .Equal(["sent", "delivered", "read"]);

        // El Message debe estar en su estado más avanzado
        var db2 = CreateDb(); // contexto limpio no tiene los datos; usamos el mismo
        var msg = await db.Messages
            .FirstAsync(m => m.ProviderMessageId == sid);
        msg.Status.Should().Be("read");
        msg.SentAt.Should().NotBeNull();
        msg.DeliveredAt.Should().NotBeNull();
        msg.ReadAt.Should().NotBeNull();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 7: Idempotencia (GAP-04 / SEC-02)
    // ════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "GAP-04: re-entrega del mismo webhook no inserta segundo DeliveryEvent")]
    public async Task ProcessAsync_DuplicateWebhook_BlockedByIdempotency()
    {
        using var db     = CreateDb();
        var       tenant = Guid.NewGuid();
        var       sid    = "SMdup_idemp_001";

        // SUT con idempotency que retorna Duplicate en la segunda llamada
        var variantTracking = Substitute.For<IVariantTrackingService>();
        var idempotency     = Substitute.For<IIdempotencyService>();
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
                IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow),
                IdempotencyResult.Duplicate(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1)));

        var sut = new MessageStatusService(
            db, variantTracking, idempotency, NullLogger<MessageStatusService>.Instance);

        await SeedMessageAsync(db, tenant, sid, "sent");

        // Primera entrega
        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));
        // Re-entrega (Twilio re-delivers)
        await sut.ProcessAsync(tenant, BuildRequest(sid, "delivered"));

        // Solo debe existir UN evento de entregabilidad
        var events = await db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == sid)
            .ToListAsync();

        events.Should().HaveCount(1,
            "GAP-04: la re-entrega de Twilio debe ser ignorada por el guard de idempotencia; " +
            "solo se inserta un MessageDeliveryEvent por (MessageSid, MessageStatus)");
    }
}
