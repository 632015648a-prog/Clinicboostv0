using ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// ConversationServiceTests
//
// Tests unitarios de ConversationService.
//
// ESTRATEGIA:
//   · EF Core InMemory (base de datos aislada por test mediante nombre único).
//   · NullLogger para silenciar logs en los tests.
//   · Un Guid de TenantId y PatientId únicos por test para evitar interferencia.
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationServiceTests
{
    // ── Helpers de setup ─────────────────────────────────────────────────────

    private static AppDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ConvServiceTests_" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(opts);
    }

    private static ConversationService BuildSut(AppDbContext db) =>
        new(db, NullLogger<ConversationService>.Instance);

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: UpsertConversationAsync — conversación nueva
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpsertConversation_CreatesNew_WhenNoActiveExists()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var conv = await sut.UpsertConversationAsync(
            tenant, patient, "whatsapp", "flow_00");

        conv.Should().NotBeNull();
        conv.Id.Should().NotBe(Guid.Empty);
        conv.TenantId.Should().Be(tenant);
        conv.PatientId.Should().Be(patient);
        conv.Channel.Should().Be("whatsapp");
        conv.FlowId.Should().Be("flow_00");
        conv.Status.Should().Be("open");
        conv.AiContext.Should().Be("{}");

        var stored = await db.Conversations.FindAsync(conv.Id);
        stored.Should().NotBeNull("debe estar persistida en la BD");
    }

    [Fact]
    public async Task UpsertConversation_ReusesExisting_WhenActiveConvExists()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        // Primera llamada: crea la conversación
        var first  = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");

        // Segunda llamada con los mismos parámetros: debe reutilizar
        var second = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");

        second.Id.Should().Be(first.Id,
            "una conversación activa existente debe ser reutilizada");

        var count = await db.Conversations.CountAsync();
        count.Should().Be(1, "no debe crearse una segunda conversación");
    }

    [Fact]
    public async Task UpsertConversation_CreatesNew_WhenExistingIsResolved()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        // Primera conversación en estado "resolved"
        var resolved = new Conversation
        {
            TenantId  = tenant,
            PatientId = patient,
            Channel   = "whatsapp",
            FlowId    = "flow_00",
            Status    = "resolved",
            AiContext = "{}",
        };
        db.Conversations.Add(resolved);
        await db.SaveChangesAsync();

        // El upsert debe crear una nueva conversación porque la existente está resuelta
        var newConv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");

        newConv.Id.Should().NotBe(resolved.Id,
            "una conversación resuelta no debe reutilizarse");
        newConv.Status.Should().Be("open");

        var count = await db.Conversations.CountAsync();
        count.Should().Be(2);
    }

    [Fact]
    public async Task UpsertConversation_ReusesExisting_WhenStatusIsWaitingAi()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var waiting = new Conversation
        {
            TenantId  = tenant,
            PatientId = patient,
            Channel   = "whatsapp",
            FlowId    = "flow_00",
            Status    = "waiting_ai",
            AiContext = "{}",
        };
        db.Conversations.Add(waiting);
        await db.SaveChangesAsync();

        var result = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");

        result.Id.Should().Be(waiting.Id,
            "waiting_ai es un estado activo; debe reutilizarse");
    }

    [Fact]
    public async Task UpsertConversation_IsolatedByTenant()
    {
        using var db       = CreateDb();
        var       sut      = BuildSut(db);
        var       patient  = Guid.NewGuid();
        var       tenant1  = Guid.NewGuid();
        var       tenant2  = Guid.NewGuid();

        var conv1 = await sut.UpsertConversationAsync(tenant1, patient, "whatsapp", "flow_00");
        var conv2 = await sut.UpsertConversationAsync(tenant2, patient, "whatsapp", "flow_00");

        conv1.Id.Should().NotBe(conv2.Id,
            "conversaciones de distintos tenants deben ser independientes");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: AppendInboundMessageAsync
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AppendInboundMessage_PersistsMessage_WithCorrectFields()
    {
        using var db       = CreateDb();
        var       sut      = BuildSut(db);
        var       tenant   = Guid.NewGuid();
        var       patient  = Guid.NewGuid();
        const string sid   = "SM123456789";
        const string body  = "Hola, quiero cita";

        var conv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");
        var msg  = await sut.AppendInboundMessageAsync(
            conv.Id, tenant, sid, body, null, null);

        msg.Should().NotBeNull();
        msg.Id.Should().NotBe(Guid.Empty);
        msg.TenantId.Should().Be(tenant);
        msg.ConversationId.Should().Be(conv.Id);
        msg.Direction.Should().Be("inbound");
        msg.Channel.Should().Be("whatsapp");
        msg.ProviderMessageId.Should().Be(sid,
            "MessageSid de Twilio debe guardarse en ProviderMessageId para correlación");
        msg.Body.Should().Be(body);
        msg.Status.Should().Be("received");
        msg.GeneratedByAi.Should().BeFalse();
        msg.MediaUrl.Should().BeNull();
        msg.MediaType.Should().BeNull();

        var stored = await db.Messages.FindAsync(msg.Id);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task AppendInboundMessage_IncrementsConversationMessageCount()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var conv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");
        conv.MessageCount.Should().Be(0);

        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SM001", "msg1", null, null);
        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SM002", "msg2", null, null);

        var updated = await db.Conversations.FindAsync(conv.Id);
        updated!.MessageCount.Should().Be(2,
            "MessageCount debe incrementarse con cada mensaje inbound");
    }

    [Fact]
    public async Task AppendInboundMessage_UpdatesLastMessageAt()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var conv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");
        conv.LastMessageAt.Should().BeNull();

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SM003", "hola", null, null);

        var updated = await db.Conversations.FindAsync(conv.Id);
        updated!.LastMessageAt.Should().NotBeNull();
        updated.LastMessageAt!.Value.Should().BeAfter(before);
    }

    [Fact]
    public async Task AppendInboundMessage_SetsSessionExpiresAt_To24HoursFromNow()
    {
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var conv   = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");
        var before = DateTimeOffset.UtcNow;

        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SM004", "hola", null, null);

        var updated = await db.Conversations.FindAsync(conv.Id);
        updated!.SessionExpiresAt.Should().NotBeNull();

        var expectedMin = before.AddHours(23).AddMinutes(59);
        var expectedMax = before.AddHours(24).AddMinutes(1);

        updated.SessionExpiresAt!.Value.Should().BeAfter(expectedMin)
            .And.BeBefore(expectedMax,
                "la ventana de sesión de WhatsApp Business es de 24 h");
    }

    [Fact]
    public async Task AppendInboundMessage_PersistsMediaFields_WhenPresent()
    {
        using var db       = CreateDb();
        var       sut      = BuildSut(db);
        var       tenant   = Guid.NewGuid();
        var       patient  = Guid.NewGuid();
        const string mediaUrl  = "https://api.twilio.com/media/xyz";
        const string mediaType = "image/jpeg";

        var conv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");
        var msg  = await sut.AppendInboundMessageAsync(
            conv.Id, tenant, "SM005", string.Empty, mediaUrl, mediaType);

        msg.MediaUrl.Should().Be(mediaUrl);
        msg.MediaType.Should().Be(mediaType);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Correlación MessageSid → Conversation → Tenant
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MessageSid_IsUnique_PerConversation()
    {
        // Verificar que cada MessageSid de Twilio se puede localizar inequívocamente
        // dentro de una conversación (no es necesario un unique constraint en InMemory,
        // pero el endpoint ya garantiza idempotencia a nivel de processed_events).
        using var db      = CreateDb();
        var       sut     = BuildSut(db);
        var       tenant  = Guid.NewGuid();
        var       patient = Guid.NewGuid();

        var conv = await sut.UpsertConversationAsync(tenant, patient, "whatsapp", "flow_00");

        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SMA001", "msg A", null, null);
        await sut.AppendInboundMessageAsync(conv.Id, tenant, "SMB001", "msg B", null, null);

        var msgs = await db.Messages
            .Where(m => m.ConversationId == conv.Id)
            .ToListAsync();

        msgs.Should().HaveCount(2);
        msgs.Select(m => m.ProviderMessageId)
            .Should().BeEquivalentTo(["SMA001", "SMB001"],
                "cada mensaje debe conservar su MessageSid para correlación");
    }
}
