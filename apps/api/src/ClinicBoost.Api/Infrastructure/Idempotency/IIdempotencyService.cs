namespace ClinicBoost.Api.Infrastructure.Idempotency;

// ════════════════════════════════════════════════════════════════════════════
// IIdempotencyService
//
// PROPÓSITO
// ─────────
// Garantiza que un evento externo o interno se procesa exactamente una vez
// ("exactly-once processing") a pesar de re-entregas, timeouts y reintentos.
//
// CASOS DE USO
// ────────────
// ┌────────────────────────────────┬──────────────────────────────────────────┐
// │ Origen del evento              │ EventType convención                     │
// ├────────────────────────────────┼──────────────────────────────────────────┤
// │ Twilio Voice inbound           │ "twilio.voice_inbound"                   │
// │ Twilio WhatsApp inbound        │ "twilio.whatsapp_inbound"                │
// │ Twilio SMS inbound             │ "twilio.sms_inbound"                     │
// │ Twilio status callback         │ "twilio.message_status"                  │
// │ Twilio voice status callback   │ "twilio.voice_status"                    │
// │ Job interno de recordatorio    │ "internal.appointment_reminder"          │
// │ Job interno de facturación     │ "internal.billing_run"                   │
// │ Cualquier job de automatización│ "internal.{flow_id}"                     │
// └────────────────────────────────┴──────────────────────────────────────────┘
//
// GARANTÍAS
// ─────────
// · INSERT atómico con ON CONFLICT DO NOTHING + lectura del registro existente.
// · tenant_id propagado desde ITenantContext cuando está disponible.
// · payload_hash SHA-256 detecta re-entregas con mismo ID pero cuerpo alterado.
// · Sin estado en memoria: seguro en multi-instancia / scale-out.
// · Inmutable: una vez registrado, ProcessedEvent nunca se modifica.
//
// USO BÁSICO EN UN HANDLER
// ─────────────────────────
//   var result = await _idempotency.TryProcessAsync(
//       eventType : "twilio.whatsapp_inbound",
//       eventId   : request.MessageSid,
//       payload   : JsonSerializer.Serialize(request),
//       ct        : ct);
//
//   if (result.AlreadyProcessed)
//       return Results.Ok();   // devolver 200 idempotente sin re-procesar
//
//   // … lógica de negocio …
//
//   return Results.Ok();
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Servicio transversal de idempotencia.
/// Registra eventos en <c>processed_events</c> y detecta duplicados atómicamente.
/// Ciclo de vida: <b>Scoped</b> (una instancia por request HTTP / job scope).
/// </summary>
public interface IIdempotencyService
{
    // ── API principal ────────────────────────────────────────────────────────

    /// <summary>
    /// Intenta registrar el evento como procesado.
    /// </summary>
    /// <param name="eventType">
    /// Tipo de evento según la convención "{proveedor}.{subtipo}" en minúsculas.
    /// Ejemplos: "twilio.whatsapp_inbound", "internal.appointment_reminder".
    /// </param>
    /// <param name="eventId">
    /// Identificador único asignado por el proveedor externo o por el caller.
    /// Para Twilio: el SID del mensaje/llamada. Para jobs: el run ID del job.
    /// </param>
    /// <param name="tenantId">
    /// UUID del tenant al que pertenece el evento.
    /// Si es null, se intenta obtener del <see cref="ITenantContext"/> activo.
    /// Pasar null explícito es válido para eventos globales sin tenant.
    /// </param>
    /// <param name="payload">
    /// Cuerpo serializado del evento (para calcular el payload_hash).
    /// Null si el caller no necesita detección de replay alterado.
    /// </param>
    /// <param name="metadata">
    /// JSON adicional para diagnóstico (IP de origen, correlation ID, etc).
    /// </param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>
    /// <see cref="IdempotencyResult"/> con el resultado del intento.
    /// Consultar <see cref="IdempotencyResult.AlreadyProcessed"/> para decidir.
    /// </returns>
    Task<IdempotencyResult> TryProcessAsync(
        string            eventType,
        string            eventId,
        Guid?             tenantId = null,
        string?           payload  = null,
        string?           metadata = null,
        CancellationToken ct       = default);

    // ── Sobrecarga tipada para payloads serializables ─────────────────────────

    /// <summary>
    /// Versión tipada: serializa <paramref name="payload"/> a JSON internamente
    /// antes de calcular el hash.
    /// </summary>
    Task<IdempotencyResult> TryProcessAsync<TPayload>(
        string            eventType,
        string            eventId,
        TPayload          payload,
        Guid?             tenantId = null,
        string?           metadata = null,
        CancellationToken ct       = default);

    // ── Consulta sin registrar ────────────────────────────────────────────────

    /// <summary>
    /// Comprueba si el evento ya fue procesado sin registrarlo.
    /// Útil en flujos donde la decisión de insertar la toma otro componente.
    /// </summary>
    Task<bool> IsAlreadyProcessedAsync(
        string            eventType,
        string            eventId,
        Guid?             tenantId = null,
        CancellationToken ct       = default);
}
