using ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusServiceTests
//
// Tests unitarios de MessageStatusService.
// Cada test usa su propia BD InMemory aislada.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MessageStatusServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("MsgStatusSvc_" + Guid.NewGuid().ToString("N"))
            .Options);

    private static MessageStatusService BuildSut(AppDbContext db) =>
        new(db, NullLogger<MessageStatusService>.Instance);

    private static TwilioMessageStatusRequest BuildRequest(
        string  messageSid,
        string  status,
        string? errorCode    = null,
        string? errorMessage = null,
        string  from         = "whatsapp:+34910000001",
        string  to           = "whatsapp:+34612000001") => new()
    {
        MessageSid    = messageSid,
        MessageStatus = status,
        AccountSid    = "ACtest",
        From          = from,
        To            = to,
        ErrorCode     = errorCode,
        ErrorMessage  = errorMessage,
    };

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
            Body              = "Test body",
            Status            = status,
        };
        db.Messages.Add(msg);
        await db.SaveChangesAsync();
        return msg;
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: MessageDeliveryEvent siempre se crea
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_CreatesDeliveryEvent_EvenWhenMessageNotFound()
    {
        using var db  = CreateDb();
        var       sut = BuildSut(db);
        var tenantId  = Guid.NewGuid();
        var req       = BuildRequest("SMorpan001", "delivered");

        // No hay Message en la BD → MessageId debe quedar null
        await sut.ProcessAsync(tenantId, req);

        var events = await db.MessageDeliveryEvents.ToListAsync();
        events.Should().HaveCount(1);
        events[0].ProviderMessageId.Should().Be("SMorpan001");
        events[0].Status.Should().Be("delivered");
        events[0].MessageId.Should().BeNull(
            "si el Message no existe en BD, MessageId queda null");
        events[0].TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task ProcessAsync_CreatesDeliveryEvent_WithCorrectChannel()
    {
        using var db  = CreateDb();
        var       sut = BuildSut(db);
        var tenantId  = Guid.NewGuid();

        // WhatsApp (From lleva prefijo "whatsapp:")
        var reqWa = BuildRequest("SMwa001", "sent",
            from: "whatsapp:+34910000001", to: "whatsapp:+34612000001");
        await sut.ProcessAsync(tenantId, reqWa);

        // SMS (sin prefijo)
        var reqSms = BuildRequest("SMsms001", "sent",
            from: "+34910000002", to: "+34612000002");
        await sut.ProcessAsync(tenantId, reqSms);

        var events = await db.MessageDeliveryEvents
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        events[0].Channel.Should().Be("whatsapp");
        events[1].Channel.Should().Be("sms");
    }

    [Fact]
    public async Task ProcessAsync_SetsErrorFields_WhenFailed()
    {
        using var db  = CreateDb();
        var       sut = BuildSut(db);
        var tenantId  = Guid.NewGuid();
        var req = BuildRequest("SMfail001", "failed",
            errorCode: "30008", errorMessage: "Unknown destination handset");

        await sut.ProcessAsync(tenantId, req);

        var evt = await db.MessageDeliveryEvents.SingleAsync();
        evt.Status.Should().Be("failed");
        evt.ErrorCode.Should().Be("30008");
        evt.ErrorMessage.Should().Be("Unknown destination handset");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Actualización de Message.Status (transiciones)
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("sent",        "sent",      true)]
    [InlineData("delivered",   "delivered", true)]
    [InlineData("read",        "read",      true)]
    [InlineData("failed",      "failed",    true)]
    [InlineData("undelivered", "undelivered", true)]
    public async Task ProcessAsync_UpdatesMessageStatus_ForAllTerminalAndProgressive(
        string incomingStatus, string expectedStatus, bool _)
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMtrans001", "pending");

        await sut.ProcessAsync(tenant, BuildRequest("SMtrans001", incomingStatus));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.Status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task ProcessAsync_SetsDeliveredAt_WhenDelivered()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMda001", "sent");
        var       before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await sut.ProcessAsync(tenant, BuildRequest("SMda001", "delivered"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.DeliveredAt.Should().NotBeNull();
        updated.DeliveredAt!.Value.Should().BeAfter(before);
    }

    [Fact]
    public async Task ProcessAsync_SetsReadAt_WhenRead()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMread001", "delivered");

        await sut.ProcessAsync(tenant, BuildRequest("SMread001", "read"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.Status.Should().Be("read");
        updated.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_SetsSentAt_WhenSent()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMsent001", "pending");

        await sut.ProcessAsync(tenant, BuildRequest("SMsent001", "sent"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.SentAt.Should().NotBeNull();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Regla de no-regresión
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_DoesNotRegressStatus_FromReadToDelivered()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMreg001", "read");

        // Llega un "delivered" tardío (out-of-order) → no debe revertir "read"
        await sut.ProcessAsync(tenant, BuildRequest("SMreg001", "delivered"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.Status.Should().Be("read",
            "el estado no puede retroceder: read > delivered");
    }

    [Fact]
    public async Task ProcessAsync_DoesNotRegressStatus_FromDeliveredToSent()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMreg002", "delivered");

        await sut.ProcessAsync(tenant, BuildRequest("SMreg002", "sent"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.Status.Should().Be("delivered");
    }

    [Fact]
    public async Task ProcessAsync_AlwaysAppliesFailed_EvenFromRead()
    {
        // "failed" es terminal: puede aplicarse desde cualquier estado
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMreg003", "read");

        await sut.ProcessAsync(tenant,
            BuildRequest("SMreg003", "failed",
                errorCode: "30001", errorMessage: "Queue overflow"));

        var updated = await db.Messages.FindAsync(msg.Id);
        updated!.Status.Should().Be("failed",
            "failed es terminal y debe aplicarse aunque el estado actual sea read");
        updated.ErrorCode.Should().Be("30001");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Correlación MessageDeliveryEvent ↔ Message
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_LinksDeliveryEvent_ToExistingMessage()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        var       msg    = await SeedMessageAsync(db, tenant, "SMcorr001", "sent");

        await sut.ProcessAsync(tenant, BuildRequest("SMcorr001", "delivered"));

        var evt = await db.MessageDeliveryEvents
            .FirstAsync(e => e.ProviderMessageId == "SMcorr001");

        evt.MessageId.Should().Be(msg.Id,
            "MessageDeliveryEvent.MessageId debe correlacionarse con Message.Id");
        evt.ConversationId.Should().Be(msg.ConversationId,
            "ConversationId debe propagarse desde el Message padre");
        evt.TenantId.Should().Be(tenant);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Múltiples transiciones del mismo SID
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAsync_CreatesMultipleDeliveryEvents_ForAllTransitions()
    {
        using var db     = CreateDb();
        var       sut    = BuildSut(db);
        var       tenant = Guid.NewGuid();
        await SeedMessageAsync(db, tenant, "SMseq001", "pending");

        await sut.ProcessAsync(tenant, BuildRequest("SMseq001", "sent"));
        await sut.ProcessAsync(tenant, BuildRequest("SMseq001", "delivered"));
        await sut.ProcessAsync(tenant, BuildRequest("SMseq001", "read"));

        var events = await db.MessageDeliveryEvents
            .Where(e => e.ProviderMessageId == "SMseq001")
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        events.Should().HaveCount(3);
        events.Select(e => e.Status).Should()
            .ContainInOrder("sent", "delivered", "read");
    }
}
