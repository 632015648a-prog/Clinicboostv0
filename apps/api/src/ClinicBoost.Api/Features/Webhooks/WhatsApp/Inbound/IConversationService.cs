using ClinicBoost.Domain.Conversations;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// IConversationService
//
// Abstracción de la lógica de conversaciones para el pipeline WhatsApp.
//
// RESPONSABILIDADES
// ─────────────────
// 1. Upsert de Conversation: buscar la conversación activa del paciente en
//    el canal whatsapp/flow deseado o crear una nueva.
// 2. Append Message: persistir el mensaje inbound con su MessageSid y la
//    correlación tenant–conversation–message.
// 3. Actualizar contadores y ventana de sesión (SessionExpiresAt) del
//    conversation al recibir cada mensaje del paciente.
//
// DISEÑO
// ──────
// · Scoped: cada request HTTP crea su propia instancia.
// · AppDbContext se inyecta directamente (no hay unit-of-work adicional).
// · Message es INMUTABLE: no se actualiza ni borra (trazabilidad RGPD).
//   Solo se inserta.
// · La ventana de 24 h de WhatsApp Business se renueva con cada mensaje
//   del paciente (SessionExpiresAt = UtcNow + 24 h).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Servicio de gestión de conversaciones y mensajes para el canal WhatsApp.
/// </summary>
public interface IConversationService
{
    /// <summary>
    /// Busca la conversación activa del paciente para el canal y flujo indicados,
    /// o crea una nueva si no existe (abierta).
    /// </summary>
    /// <param name="tenantId">Tenant al que pertenece la conversación.</param>
    /// <param name="patientId">Paciente participante en la conversación.</param>
    /// <param name="channel">Canal de comunicación (ej: "whatsapp").</param>
    /// <param name="flowId">Flujo de automatización (ej: "flow_00").</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Conversación existente o recién creada.</returns>
    Task<Conversation> UpsertConversationAsync(
        Guid              tenantId,
        Guid              patientId,
        string            channel,
        string            flowId,
        CancellationToken ct = default);

    /// <summary>
    /// Persiste un mensaje inbound en la conversación indicada.
    /// Establece <see cref="Message.ProviderMessageId"/> al MessageSid de Twilio
    /// para permitir la correlación SID–conversation–tenant.
    /// </summary>
    /// <param name="conversationId">Conversación a la que pertenece el mensaje.</param>
    /// <param name="tenantId">Tenant (necesario para la clave RLS del mensaje).</param>
    /// <param name="messageSid">MessageSid de Twilio ("SM…") para correlación y deduplicación.</param>
    /// <param name="body">Texto del mensaje (puede ser vacío si es solo media).</param>
    /// <param name="mediaUrl">URL del primer adjunto multimedia, o null.</param>
    /// <param name="mediaType">MIME type del adjunto, o null.</param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>Mensaje insertado con su Id asignado.</returns>
    Task<Message> AppendInboundMessageAsync(
        Guid              conversationId,
        Guid              tenantId,
        string            messageSid,
        string?           body,
        string?           mediaUrl,
        string?           mediaType,
        CancellationToken ct = default);
}
