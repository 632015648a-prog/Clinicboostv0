using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Conversations;

/// <summary>
/// Sesión de conversación entre la clínica y un paciente en un canal y flujo.
/// La IA lee el estado y el historial para decidir la siguiente acción.
/// REGLA ABSOLUTA: la IA nunca confirma citas — solo propone. El backend ejecuta.
/// </summary>
public sealed class Conversation : BaseEntity
{
    public Guid PatientId { get; init; }

    public required string Channel { get; set; }    // whatsapp | sms | email | web_chat
    public required string FlowId { get; set; }     // flow_00 … flow_07
    public required string Status { get; set; } = "open";
    // open | waiting_ai | waiting_human | resolved | expired | opted_out

    /// <summary>
    /// Contexto serializado que se pasa a la IA en cada turno.
    /// No debe contener PII directa; usar referencias (patient_id).
    /// </summary>
    public string AiContext { get; set; } = "{}";   // JSON

    public int MessageCount { get; set; } = 0;
    public DateTimeOffset? LastMessageAt { get; set; }
    public Guid? AppointmentId { get; set; }

    /// <summary>
    /// Ventana de sesión de WhatsApp (24 h desde el último mensaje del paciente).
    /// Fuera de esta ventana solo se pueden usar plantillas aprobadas por Meta.
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Mensaje individual dentro de una conversación.
/// INMUTABLE: no se actualiza ni borra (trazabilidad RGPD).
/// </summary>
public sealed class Message
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid ConversationId { get; init; }

    public required string Direction { get; init; }     // inbound | outbound
    public required string Channel { get; init; }
    public string? ProviderMessageId { get; set; }      // Twilio MessageSid, etc.

    public string? Body { get; init; }
    public string? TemplateId { get; init; }
    public string? TemplateVars { get; init; }          // JSON

    /// <summary>
    /// FK a message_variants. Null si el mensaje no pertenece a ninguna variante A/B activa.
    /// Se propaga a MessageDeliveryEvent para poder agregar entregabilidad por variante sin JOIN.
    /// </summary>
    public Guid? MessageVariantId { get; set; }

    public string? MediaUrl { get; init; }
    public string? MediaType { get; init; }

    public required string Status { get; set; }         // pending | sent | delivered | read | failed | received

    // Trazabilidad IA
    public bool GeneratedByAi { get; init; } = false;
    public string? AiModel { get; set; }
    public int? AiPromptTokens { get; init; }
    public int? AiCompletionTokens { get; init; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Sin UpdatedAt: inmutable
}
