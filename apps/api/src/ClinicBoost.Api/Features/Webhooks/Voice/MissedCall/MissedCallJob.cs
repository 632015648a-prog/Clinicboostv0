namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallJob
//
// Mensaje que se encola en el Channel tras validar y desduplicar el webhook.
// Contiene exactamente los datos necesarios para que el worker ejecute el
// flujo "flow_00 — Llamada perdida" sin acceder al HttpContext (que ya no
// existe en el momento de la ejecución asíncrona).
//
// DISEÑO
// ──────
// · Record immutable: no tiene setters, se crea de una sola vez en el handler.
// · Tamaño reducido: solo los campos que el worker necesita. El payload Twilio
//   completo se almacena en WebhookEvent (trazabilidad), no aquí.
// · CorrelationId propagado desde la request original para rastrear el flujo
//   end-to-end en los logs (Serilog enriched con CorrelationId).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Job de llamada perdida que se encola en <see cref="IMissedCallJobQueue"/>
/// para procesamiento asíncrono fuera del request HTTP.
/// </summary>
/// <param name="TenantId">
/// Tenant al que pertenece la llamada. Nunca null en este punto
/// (la resolución de tenant ocurre antes del encolado).
/// </param>
/// <param name="CallSid">
/// SID de la llamada en Twilio (p.ej. "CAxxxxxxxx"). Identificador primario del evento.
/// </param>
/// <param name="CallerPhone">
/// Número del paciente que llamó, en formato E.164.
/// Usado para buscar/crear el paciente y para el envío del WhatsApp de flow_00.
/// </param>
/// <param name="ClinicPhone">
/// Número de la clínica al que llegó la llamada (campo "To" de Twilio).
/// Necesario para construir el mensaje de WhatsApp con From correcto.
/// </param>
/// <param name="CallStatus">
/// Estado de la llamada según Twilio: "no-answer", "busy", "failed", "completed".
/// Solo se procesa en flow_00 si el estado indica llamada perdida real.
/// </param>
/// <param name="ReceivedAt">
/// Timestamp en UTC del momento en que el webhook fue recibido.
/// Se usa para calcular delays y para el audit log.
/// </param>
/// <param name="ProcessedEventId">
/// ID del registro en processed_events (idempotencia ya garantizada).
/// Propagado para correlacionar el job con el evento registrado.
/// </param>
/// <param name="CorrelationId">
/// ID de correlación de la request HTTP original (HttpContext.TraceIdentifier).
/// Permite rastrear el flujo completo en Serilog.
/// </param>
public sealed record MissedCallJob(
    Guid            TenantId,
    string          CallSid,
    string          CallerPhone,
    string          ClinicPhone,
    string          CallStatus,
    DateTimeOffset  ReceivedAt,
    Guid            ProcessedEventId,
    string          CorrelationId
);
