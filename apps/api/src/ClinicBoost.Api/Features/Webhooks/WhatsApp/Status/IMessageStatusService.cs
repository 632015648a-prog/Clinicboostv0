namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;

// ════════════════════════════════════════════════════════════════════════════
// IMessageStatusService
//
// Abstrae la lógica de procesamiento síncrona del callback de estado.
// El handler del endpoint lo llama DESPUÉS de validar la firma y la
// idempotencia, pasándole los datos ya parseados.
//
// RESPONSABILIDADES
// ─────────────────
// 1. Buscar el Message por ProviderMessageId (MessageSid).
//    Si no se encuentra, registra el evento de entregabilidad igualmente
//    (MessageId/ConversationId nulos) para trazabilidad.
// 2. Actualizar los campos mutables de Message (Status, SentAt, DeliveredAt,
//    ReadAt, ErrorCode, ErrorMessage) según la transición.
// 3. Insertar un MessageDeliveryEvent con todas las dimensiones de
//    agrupación (FlowId, TemplateId, MessageVariant, Channel).
//
// NOTA SOBRE "INMUTABILIDAD" DE Message
// ──────────────────────────────────────
// La entidad Message tiene campos "mutable" declarados con { get; set; }
// (Status, ErrorCode, ErrorMessage, SentAt, DeliveredAt, ReadAt).
// Son los únicos que se actualizan y solo mediante callbacks de entregabilidad.
// El resto (Body, Direction, Channel, etc.) permanecen inmutables.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Procesa una transición de estado de mensaje recibida por callback de Twilio.
/// Registrar como <b>Scoped</b>.
/// </summary>
public interface IMessageStatusService
{
    /// <summary>
    /// Actualiza el estado del Message correspondiente al MessageSid y
    /// persiste un <c>MessageDeliveryEvent</c> con las dimensiones de análisis.
    /// </summary>
    /// <param name="tenantId">Tenant propietario del mensaje.</param>
    /// <param name="request">DTO del callback ya parseado.</param>
    /// <param name="ct">Token de cancelación.</param>
    Task ProcessAsync(
        Guid                         tenantId,
        TwilioMessageStatusRequest   request,
        CancellationToken            ct = default);
}
