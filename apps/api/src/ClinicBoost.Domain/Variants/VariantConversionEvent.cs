namespace ClinicBoost.Domain.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantConversionEvent
//
// Evento inmutable del funnel de conversión por variante A/B.
//
// DISEÑO
// ──────
// · INSERT-only: nunca se actualiza ni borra (trazabilidad + RGPD).
// · Una fila = un paso del funnel para un mensaje concreto:
//     outbound_sent → delivered → read → reply → booked
// · La tabla permite calcular el funnel completo por variante con una
//   sola query de agregación (ver v_variant_conversion_funnel en SQL).
//
// FUNNEL
// ──────
//   1. outbound_sent  — se crea en TwilioOutboundMessageSender al enviar.
//   2. delivered      — se crea en MessageStatusService al recibir callback Twilio.
//   3. read           — se crea en MessageStatusService al recibir callback Twilio.
//   4. reply          — se crea en WhatsAppInboundWorker cuando llega un inbound
//                       correlacionado con el mensaje outbound de la variante.
//   5. booked         — se crea en Flow01Orchestrator.RecordAppointmentBookedAsync.
//
// CORRELACIÓN
// ───────────
// · MessageId           → messages.id (mensaje outbound que inició el funnel)
// · ProviderMessageId   → Twilio MessageSid (punto de correlación con Twilio)
// · ConversationId      → conversations.id (para JOIN con historial)
// · CorrelationId       → ID end-to-end compartido con FlowMetricsEvent
//
// REGLA DE NEGOCIO
// ────────────────
// · RecoveredRevenue solo se rellena para event_type = "booked".
// · ElapsedMs se mide desde el OccurredAt del evento outbound_sent de la misma
//   variante + mensaje (el orchestrator lo calcula y lo pasa).
// · La lógica de precios y éxito fee vive en Flow01Orchestrator + RevenueEvent,
//   no aquí.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Evento inmutable del funnel de conversión A/B.
/// Una fila por paso del funnel (outbound_sent / delivered / read / reply / booked).
/// </summary>
public sealed class VariantConversionEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Tenant propietario (ADR-001). Nunca null.</summary>
    public Guid TenantId { get; init; }

    // ── Variante ──────────────────────────────────────────────────────────

    /// <summary>
    /// FK a message_variants. Nunca null: todo evento pertenece a una variante.
    /// </summary>
    public Guid MessageVariantId { get; init; }

    // ── Correlación ───────────────────────────────────────────────────────

    /// <summary>
    /// ID interno del mensaje outbound en la tabla messages.
    /// Null en el caso raro de que el callback llegue antes del INSERT en BD.
    /// </summary>
    public Guid? MessageId { get; init; }

    /// <summary>ID de la conversación para JOIN con historial.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>ID de la cita. Solo se rellena cuando EventType = "booked".</summary>
    public Guid? AppointmentId { get; init; }

    /// <summary>Twilio MessageSid para correlación con message_delivery_events.</summary>
    public string? ProviderMessageId { get; init; }

    // ── Tipo de evento ─────────────────────────────────────────────────────

    /// <summary>
    /// Paso del funnel.
    /// Valores: outbound_sent | delivered | read | reply | booked.
    /// </summary>
    public required string EventType { get; init; }

    // ── Métricas temporales ───────────────────────────────────────────────

    /// <summary>
    /// Milisegundos desde el evento outbound_sent de este mismo mensaje
    /// hasta el evento actual. Null para outbound_sent (es el evento base).
    /// </summary>
    public long? ElapsedMs { get; init; }

    // ── Revenue (solo para event_type = "booked") ─────────────────────────

    /// <summary>Revenue recuperado en EUR atribuido a esta variante + reserva.</summary>
    public decimal? RecoveredRevenue { get; init; }

    /// <summary>Moneda ISO 4217. Siempre "EUR" en la fase actual.</summary>
    public string Currency { get; init; } = "EUR";

    // ── Trazabilidad ─────────────────────────────────────────────────────

    /// <summary>ID de correlación end-to-end (mismo que FlowMetricsEvent).</summary>
    public required string CorrelationId { get; init; }

    /// <summary>JSON con metadatos adicionales (canal, modelo IA, etc.).</summary>
    public string Metadata { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

// ── Constantes del funnel ─────────────────────────────────────────────────────

/// <summary>
/// Tipos de evento del funnel de conversión por variante.
/// Deben coincidir con el CHECK en la migración SQL.
/// </summary>
public static class VariantEventType
{
    public const string OutboundSent = "outbound_sent";
    public const string Delivered    = "delivered";
    public const string Read         = "read";
    public const string Reply        = "reply";
    public const string Booked       = "booked";
}
