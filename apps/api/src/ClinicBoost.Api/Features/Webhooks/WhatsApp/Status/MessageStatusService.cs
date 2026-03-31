using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Variants;
using ClinicBoost.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;

// ════════════════════════════════════════════════════════════════════════════
// MessageStatusService
//
// Implementación de IMessageStatusService.
//
// FLUJO POR TRANSICIÓN DE ESTADO
// ──────────────────────────────
//  1. Buscar Message por (TenantId, ProviderMessageId = MessageSid).
//     · Si no existe: crear el evento de entregabilidad con MessageId = null
//       y retornar.  El mensaje puede haberse enviado por otra instancia o
//       antes de que nuestra BD lo registrase.
//
//  2. Según el nuevo estado:
//     · "sent"        → Message.Status = "sent",       SentAt = now
//     · "delivered"   → Message.Status = "delivered",  DeliveredAt = now
//     · "read"        → Message.Status = "read",       ReadAt = now
//     · "failed"      → Message.Status = "failed",     ErrorCode/Msg
//     · "undelivered" → Message.Status = "undelivered",ErrorCode/Msg
//     Regla de no-regresión: no actualizar si el estado actual ya es
//     posterior en el ciclo de vida (read > delivered > sent > pending).
//
//  3. Insertar MessageDeliveryEvent con:
//     · Dimensiones: FlowId + TemplateId + MessageVariant del Message padre.
//     · Timestamps del proveedor y de recepción en nuestro servidor.
//
//  4. Persistir todo en una sola llamada a SaveChangesAsync.
//
// NOTA DE DISEÑO — Acoplamiento con Message
// ──────────────────────────────────────────
// Message tiene campos mutables (Status, SentAt, DeliveredAt, ReadAt,
// ErrorCode, ErrorMessage) para soportar los callbacks de Twilio.
// El servicio es el único punto que los actualiza, preservando la
// inmutabilidad del resto de campos (Body, Direction, Channel, etc.).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IMessageStatusService"/>.
/// Registrar como <b>Scoped</b>.
/// </summary>
public sealed class MessageStatusService : IMessageStatusService
{
    // Orden de ciclo de vida: índice más alto = estado más avanzado.
    // Un estado nunca puede retroceder en el ciclo.
    private static readonly Dictionary<string, int> StatusOrder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pending"]     = 0,
            ["sent"]        = 1,
            ["delivered"]   = 2,
            ["read"]        = 3,
            ["failed"]      = 10,   // terminal; no compite con el ciclo normal
            ["undelivered"] = 10,   // terminal
        };

    private readonly AppDbContext                   _db;
    private readonly IVariantTrackingService         _variantTracking;
    private readonly ILogger<MessageStatusService>   _logger;

    public MessageStatusService(
        AppDbContext                  db,
        IVariantTrackingService       variantTracking,
        ILogger<MessageStatusService> logger)
    {
        _db              = db;
        _variantTracking = variantTracking;
        _logger          = logger;
    }

    /// <inheritdoc/>
    public async Task ProcessAsync(
        Guid                       tenantId,
        TwilioMessageStatusRequest request,
        CancellationToken          ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // ── 1. Buscar Message por ProviderMessageId ───────────────────────────
        var message = await _db.Messages
            .FirstOrDefaultAsync(
                m => m.TenantId          == tenantId &&
                     m.ProviderMessageId == request.MessageSid,
                ct);

        if (message is null)
        {
            _logger.LogWarning(
                "[MsgStatus] Message no encontrado en BD para MessageSid={Sid} " +
                "TenantId={TenantId}. Se registra el evento sin MessageId.",
                request.MessageSid, tenantId);
        }

        // ── 2. Actualizar Message si se encontró ──────────────────────────────
        if (message is not null)
        {
            ApplyStatusTransition(message, request, now);
        }

        // N-P1-03: cargar la conversación para propagar FlowId al DeliveryEvent.
        // Message no tiene FlowId propio; lo hereda de su Conversation padre.
        // AsNoTracking porque solo necesitamos el FlowId para el evento.
        string? flowId = null;
        if (message?.ConversationId is not null)
        {
            var conv = await _db.Conversations
                .AsNoTracking()
                .Where(c => c.Id == message.ConversationId && c.TenantId == tenantId)
                .Select(c => new { c.FlowId })
                .FirstOrDefaultAsync(ct);
            flowId = conv?.FlowId;
        }

        // ── 3. Insertar MessageDeliveryEvent ──────────────────────────────────
        var deliveryEvent = new MessageDeliveryEvent
        {
            TenantId          = tenantId,
            MessageId         = message?.Id,
            ConversationId    = message?.ConversationId,
            ProviderMessageId = request.MessageSid,
            Status            = request.MessageStatus,
            // N-P1-03: FlowId propagado desde Conversation padre.
            FlowId            = flowId,
            TemplateId        = message?.TemplateId,
            // MessageVariant (string key) y MessageVariantId FK propagados si disponibles
            MessageVariant    = null,   // campo string legacy; Message usa MessageVariantId FK
            MessageVariantId  = message?.MessageVariantId,
            Channel           = request.Channel,
            ErrorCode         = request.ErrorCode,
            ErrorMessage      = request.ErrorMessage,
            ProviderTimestamp = request.ProviderTimestamp,
            OccurredAt        = now,
        };

        _db.MessageDeliveryEvents.Add(deliveryEvent);
        await _db.SaveChangesAsync(ct);

        // ── 4. Registrar en funnel de variante si corresponde ─────────────────
        if (message?.MessageVariantId.HasValue == true)
        {
            var variantEventType = request.MessageStatus switch
            {
                "delivered" => VariantEventType.Delivered,
                "read"      => VariantEventType.Read,
                _           => (string?)null,
            };

            if (variantEventType is not null)
            {
                var sentAt    = message.SentAt ?? message.CreatedAt;
                var elapsedMs = (long)(now - sentAt).TotalMilliseconds;

                await _variantTracking.RecordEventAsync(new VariantConversionEvent
                {
                    TenantId          = tenantId,
                    MessageVariantId  = message.MessageVariantId!.Value,
                    MessageId         = message.Id,
                    ConversationId    = message.ConversationId,
                    ProviderMessageId = request.MessageSid,
                    EventType         = variantEventType,
                    ElapsedMs         = elapsedMs,
                    // N-P2-05: usar ProviderMessageId como correlationId es correcto
                    // (es el SID de Twilio, único por mensaje y disponible en todos
                    //  los callbacks de estado para este mensaje).
                    CorrelationId     = request.MessageSid,
                    Metadata          = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        twilio_status = request.MessageStatus,
                        channel       = request.Channel,
                        flow_id       = flowId,   // N-P1-03: añadir flow_id al metadata del evento
                    }),
                }, ct);
            }
        }

        _logger.LogInformation(
            "[MsgStatus] Evento de entregabilidad registrado. " +
            "MessageSid={Sid} Status={Status} MessageId={MsgId} " +
            "VariantId={VarId} TenantId={TenantId} DeliveryEventId={EventId}",
            request.MessageSid, request.MessageStatus,
            message?.Id, message?.MessageVariantId, tenantId, deliveryEvent.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Aplica la transición de estado al Message respetando la regla de no-regresión:
    /// el estado nunca puede retroceder en el ciclo de vida del mensaje.
    /// </summary>
    private void ApplyStatusTransition(
        Message                    message,
        TwilioMessageStatusRequest request,
        DateTimeOffset             now)
    {
        var currentOrder = StatusOrder.GetValueOrDefault(message.Status, 0);
        var newOrder     = StatusOrder.GetValueOrDefault(request.MessageStatus, 0);

        // Los estados terminales (failed/undelivered) siempre se aplican.
        // El ciclo normal no retrocede (read no puede volver a delivered).
        bool isTerminal = request.IsFailure;
        bool isProgression = newOrder > currentOrder;

        if (!isTerminal && !isProgression)
        {
            _logger.LogDebug(
                "[MsgStatus] No-regresión: ignorando transición {Current}→{New} " +
                "para MessageSid={Sid}.",
                message.Status, request.MessageStatus, request.MessageSid);
            return;
        }

        var previousStatus = message.Status;
        message.Status = request.MessageStatus;

        switch (request.MessageStatus)
        {
            case "sent":
                message.SentAt      = now;
                break;
            case "delivered":
                message.SentAt    ??= now;   // garantizar que SentAt tiene valor
                message.DeliveredAt = now;
                break;
            case "read":
                message.SentAt    ??= now;
                message.DeliveredAt ??= now; // puede llegar read sin delivered previo
                message.ReadAt      = now;
                break;
            case "failed":
            case "undelivered":
                message.ErrorCode    = request.ErrorCode;
                message.ErrorMessage = request.ErrorMessage;
                break;
        }

        _logger.LogInformation(
            "[MsgStatus] Message actualizado. " +
            "MessageSid={Sid} {Prev}→{New} MessageId={MsgId}",
            request.MessageSid, previousStatus, request.MessageStatus, message.Id);
    }
}
