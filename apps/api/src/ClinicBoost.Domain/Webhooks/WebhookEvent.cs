namespace ClinicBoost.Domain.Webhooks;

/// <summary>
/// Webhook externo ya validado criptográficamente, pendiente de procesar.
/// Si la validación de firma falla en el middleware HTTP, el evento NO se inserta aquí.
/// El worker de procesamiento consume esta tabla en orden FIFO con reintentos.
/// </summary>
public sealed class WebhookEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Puede ser null si el tenant aún no se ha resuelto
    /// (p.ej. webhook de signup antes de crear el tenant).
    /// </summary>
    public Guid? TenantId { get; set; }

    public required string Source { get; init; }        // twilio | supabase_auth | calendar_sync | stripe | internal
    public required string EventType { get; init; }     // tipo específico del proveedor
    public required string Payload { get; init; }       // JSON crudo ya validado
    public string? Headers { get; init; }               // JSON de cabeceras relevantes (sin Authorization)

    public required string Status { get; set; } = "pending";
    // pending | processing | processed | failed | skipped

    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; init; } = 3;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Hash de (source + provider event_id). Se cruza con processed_events
    /// para garantizar idempotencia.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    public Guid? CorrelationId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}
