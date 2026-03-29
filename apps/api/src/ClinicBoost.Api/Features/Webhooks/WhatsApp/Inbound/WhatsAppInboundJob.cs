namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// WhatsAppInboundJob
//
// Mensaje encolado en el Channel tras deduplicar y validar el webhook.
// Contiene todos los datos necesarios para que el worker ejecute el
// pipeline completo sin acceder al HttpContext (que ya no existe).
//
// DISEÑO
// ──────
// · Record inmutable: se crea una única vez en el handler y nunca se muta.
// · Tamaño mínimo: solo los campos que el worker necesita. El payload raw
//   completo de Twilio se guarda en WebhookEvent (trazabilidad).
// · CorrelationId propagado desde HttpContext.TraceIdentifier para rastrear
//   el flujo end-to-end en los logs (handler → queue → worker → agente IA).
// · ProcessedEventId vincula este job con la fila de processed_events
//   garantizando que el evento fue deduplicado antes de encolar.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Job de mensaje WhatsApp inbound encolado en <see cref="IWhatsAppJobQueue"/>
/// para procesamiento asíncrono fuera del ciclo HTTP.
/// </summary>
/// <param name="TenantId">Tenant al que pertenece el número de destino.</param>
/// <param name="MessageSid">SID de Twilio ("SM…"). Identificador primario del evento.</param>
/// <param name="CallerPhone">E.164 del paciente que envió el mensaje.</param>
/// <param name="ClinicPhone">E.164 del número de la clínica (destino).</param>
/// <param name="Body">Texto del mensaje. Vacío si es solo media.</param>
/// <param name="MediaUrl">URL del primer adjunto multimedia, o null.</param>
/// <param name="MediaType">MIME type del adjunto, o null.</param>
/// <param name="ProfileName">Nombre de perfil WA del emisor (referencial).</param>
/// <param name="ReceivedAt">Timestamp UTC de recepción del webhook.</param>
/// <param name="ProcessedEventId">ID del registro en processed_events (idempotencia ya garantizada).</param>
/// <param name="CorrelationId">TraceIdentifier del request HTTP original.</param>
public sealed record WhatsAppInboundJob(
    Guid            TenantId,
    string          MessageSid,
    string          CallerPhone,
    string          ClinicPhone,
    string          Body,
    string?         MediaUrl,
    string?         MediaType,
    string          ProfileName,
    DateTimeOffset  ReceivedAt,
    Guid            ProcessedEventId,
    string          CorrelationId
);
