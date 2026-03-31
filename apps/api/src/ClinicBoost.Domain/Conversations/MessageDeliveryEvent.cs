namespace ClinicBoost.Domain.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// MessageDeliveryEvent
//
// Evento inmutable de entregabilidad de un mensaje outbound.
//
// DISEÑO
// ──────
// · INSERT-only: nunca se actualiza ni borra (trazabilidad RGPD + auditoría).
// · Una fila por transición de estado de Twilio: sent → delivered → read,
//   o sent → failed. Múltiples eventos por Message son normales.
// · Campos de dimensión (FlowId, TemplateId, MessageVariant) permiten
//   AGGREGATE queries para dashboards de entregabilidad por flujo y variante.
// · ErrorCode + ErrorMessage directamente del cuerpo del callback de Twilio,
//   sin transformación, para preservar la información diagnóstica original.
//
// AGRUPACIÓN
// ──────────
// La tabla permite responder preguntas como:
//   · ¿Cuántos mensajes del flow_01 fueron entregados en las últimas 24 h?
//   · ¿Qué variante de plantilla tiene mayor tasa de lectura?
//   · ¿Qué porcentaje de mensajes con error 30008 son de una clínica concreta?
//
// Consulta de ejemplo (SQL):
//   SELECT flow_id, template_id, message_variant,
//          COUNT(*) FILTER (WHERE status = 'delivered') AS delivered,
//          COUNT(*) FILTER (WHERE status = 'read')      AS read,
//          COUNT(*) FILTER (WHERE status = 'failed')    AS failed
//   FROM   message_delivery_events
//   WHERE  tenant_id = $1
//     AND  occurred_at >= now() - interval '7 days'
//   GROUP  BY flow_id, template_id, message_variant;
//
// RELACIONES
// ──────────
// · MessageId → messages.id  (Message padre; puede ser null si se recibe
//   un callback de Twilio para un SID no registrado en nuestra BD).
// · ProviderMessageId = Twilio MessageSid → punto de correlación primario.
//   Siempre presente; permite localizar el evento sin el MessageId interno.
// · ConversationId → conversations.id  (para JOIN con contexto de conversación).
// · TenantId → dimensión de particionamiento.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Evento inmutable de entregabilidad de un mensaje de canal (WhatsApp/SMS).
/// Una fila por callback de estado recibido de Twilio.
/// </summary>
public sealed class MessageDeliveryEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // ── Claves de correlación ─────────────────────────────────────────────

    /// <summary>Tenant propietario del mensaje. Nunca null.</summary>
    public Guid TenantId { get; init; }

    /// <summary>
    /// ID interno de la fila en messages.
    /// Null si el callback llegó antes de que el Message se insertara
    /// (race condition rara pero posible en reintentos de Twilio).
    /// </summary>
    public Guid? MessageId { get; init; }

    /// <summary>
    /// ID interno de la conversación a la que pertenece el mensaje.
    /// Null si MessageId es null.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>
    /// Twilio MessageSid ("SM…" o "MM…"). Punto de correlación primario.
    /// Siempre presente: Twilio siempre lo incluye en el callback.
    /// </summary>
    public required string ProviderMessageId { get; init; }

    // ── Estado ────────────────────────────────────────────────────────────

    /// <summary>
    /// Estado reportado por Twilio en este callback.
    /// Valores: sent | delivered | read | failed | undelivered
    /// </summary>
    public required string Status { get; init; }

    // ── Dimensiones de agrupación (para analytics) ────────────────────────

    /// <summary>
    /// Flujo de automatización que generó el mensaje (flow_00 … flow_07).
    /// Null si el mensaje fue enviado manualmente (fuera de un flujo).
    /// </summary>
    public string? FlowId { get; init; }

    /// <summary>
    /// ID de la plantilla de mensaje utilizada (p.ej. "missed_call_v1").
    /// Null para mensajes de texto libre (dentro de la ventana de sesión WA).
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Variante A/B del mensaje (p.ej. "A", "B", "control").
    /// Null si no hay A/B testing activo.
    /// </summary>
    public string? MessageVariant { get; init; }

    /// <summary>
    /// FK a message_variants.id.
    /// Copiado del Message padre para agregar entregabilidad por variante sin JOIN.
    /// Null si el mensaje no pertenece a ninguna variante A/B activa.
    /// </summary>
    public Guid? MessageVariantId { get; init; }

    /// <summary>Canal de envío ("whatsapp" | "sms").</summary>
    public required string Channel { get; init; }

    // ── Error (cuando Status = "failed" | "undelivered") ──────────────────

    /// <summary>
    /// Código de error de Twilio (p.ej. "30008" = Unknown destination handset).
    /// Null cuando Status ≠ "failed" y ≠ "undelivered".
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Mensaje de error literal de Twilio. Null cuando no hay error.
    /// </summary>
    public string? ErrorMessage { get; init; }

    // ── Timestamps ────────────────────────────────────────────────────────

    /// <summary>
    /// Timestamp UTC en que Twilio reportó el cambio de estado.
    /// Puede diferir de OccurredAt si el callback llegó con retraso.
    /// </summary>
    public DateTimeOffset? ProviderTimestamp { get; init; }

    /// <summary>
    /// Timestamp UTC en que nuestro servidor recibió el callback.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
