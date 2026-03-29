using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// ConversationService
//
// Implementación de IConversationService para el pipeline WhatsApp inbound.
//
// MODELO DE DATOS
// ───────────────
// conversations (BaseEntity → TenantId, Id, CreatedAt, UpdatedAt)
//   · PatientId         — FK a patients
//   · Channel           — "whatsapp"
//   · FlowId            — "flow_00" … "flow_07"
//   · Status            — "open" | "waiting_ai" | "waiting_human" |
//                         "resolved" | "expired" | "opted_out"
//   · AiContext         — JSON con contexto para el agente
//   · MessageCount      — incrementado en cada AppendInboundMessageAsync
//   · LastMessageAt     — timestamp del último mensaje del paciente
//   · SessionExpiresAt  — ventana de 24 h de WA Business (se renueva en inbound)
//
// messages (INMUTABLE — ver clase Message)
//   · ProviderMessageId — MessageSid de Twilio ("SM…")
//   · Direction         — "inbound"
//   · Channel           — "whatsapp"
//   · Status            — "received"
//
// UPSERT STRATEGY
// ───────────────
// Buscamos una conversación en estado activo (open | waiting_ai | waiting_human)
// para el mismo paciente, canal y flujo. Si existe, la reutilizamos; si no,
// creamos una nueva. Esto permite que un mismo paciente tenga múltiples
// conversaciones para diferentes flujos y que conversaciones resueltas no se
// reabran automáticamente.
//
// VENTANA DE SESIÓN WhatsApp
// ──────────────────────────
// Cada mensaje inbound del paciente renueva la ventana de 24 h de WhatsApp
// Business. Si SessionExpiresAt < UtcNow el agente solo podrá usar plantillas
// aprobadas. El worker verifica esto antes de enviar mensajes de texto libre.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IConversationService"/>.
/// Registrar como <b>Scoped</b>.
/// </summary>
public sealed class ConversationService : IConversationService
{
    // Estados de conversación que consideramos "activa" para el upsert
    private static readonly HashSet<string> ActiveStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "open",
            "waiting_ai",
            "waiting_human",
        };

    private static readonly TimeSpan WhatsAppSessionWindow = TimeSpan.FromHours(24);

    private readonly AppDbContext                   _db;
    private readonly ILogger<ConversationService>   _logger;

    public ConversationService(
        AppDbContext                  db,
        ILogger<ConversationService>  logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── UpsertConversationAsync ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Conversation> UpsertConversationAsync(
        Guid              tenantId,
        Guid              patientId,
        string            channel,
        string            flowId,
        CancellationToken ct = default)
    {
        // Buscar conversación activa existente
        var existing = await _db.Conversations
            .Where(c =>
                c.TenantId  == tenantId  &&
                c.PatientId == patientId &&
                c.Channel   == channel   &&
                c.FlowId    == flowId    &&
                ActiveStatuses.Contains(c.Status))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            _logger.LogDebug(
                "[ConversationService] Conversación activa reutilizada. " +
                "ConvId={ConvId} PatientId={PatientId} TenantId={TenantId}",
                existing.Id, patientId, tenantId);
            return existing;
        }

        // Crear nueva conversación
        var conversation = new Conversation
        {
            TenantId  = tenantId,
            PatientId = patientId,
            Channel   = channel,
            FlowId    = flowId,
            Status    = "open",
            AiContext = "{}",
        };

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ConversationService] Nueva conversación creada. " +
            "ConvId={ConvId} Channel={Channel} FlowId={FlowId} " +
            "PatientId={PatientId} TenantId={TenantId}",
            conversation.Id, channel, flowId, patientId, tenantId);

        return conversation;
    }

    // ── AppendInboundMessageAsync ──────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Message> AppendInboundMessageAsync(
        Guid              conversationId,
        Guid              tenantId,
        string            messageSid,
        string?           body,
        string?           mediaUrl,
        string?           mediaType,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // ── Crear el mensaje (inmutable) ───────────────────────────────────
        var message = new Message
        {
            TenantId          = tenantId,
            ConversationId    = conversationId,
            Direction         = "inbound",
            Channel           = "whatsapp",
            ProviderMessageId = messageSid,   // correlación MessageSid ↔ Message.Id
            Body              = body,
            MediaUrl          = mediaUrl,
            MediaType         = mediaType,
            Status            = "received",
            GeneratedByAi     = false,
            CreatedAt         = now,
        };

        _db.Messages.Add(message);

        // ── Actualizar contadores y ventana de sesión ──────────────────────
        var conv = await _db.Conversations.FindAsync([conversationId], ct);
        if (conv is not null)
        {
            conv.MessageCount    += 1;
            conv.LastMessageAt    = now;
            conv.SessionExpiresAt = now + WhatsAppSessionWindow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogDebug(
            "[ConversationService] Mensaje inbound persistido. " +
            "MessageId={MsgId} MessageSid={Sid} ConvId={ConvId} " +
            "TenantId={TenantId}",
            message.Id, messageSid, conversationId, tenantId);

        return message;
    }
}
