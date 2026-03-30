namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// IOutboundMessageSender
//
// Abstracción del envío de mensajes salientes a través de un canal.
//
// CONTRATO
// ────────
//  · SendAsync: registra el mensaje en la BD (status=pending), llama a Twilio,
//    actualiza status a sent/failed y devuelve OutboundSendResult.
//  · Si Twilio falla, devuelve IsSuccess=false pero NO lanza excepción.
//    El llamador decide si reintentar o registrar el fallo.
//  · Las credenciales de Twilio se leen de TwilioOptions (NUNCA del frontend).
//
// EXTENSIBILIDAD
// ──────────────
//  · TwilioOutboundMessageSender → implementación de producción.
//  · StubOutboundMessageSender   → para tests y entornos sin Twilio configurado.
//  · SmsOutboundMessageSender    → futuro canal SMS.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Envía mensajes salientes (WhatsApp / SMS) a través de Twilio.
/// Persiste el mensaje en BD antes y después del envío.
/// </summary>
public interface IOutboundMessageSender
{
    /// <summary>
    /// Envía el mensaje descrito en <paramref name="request"/>.
    /// Registra un <c>Message</c> en BD con status=pending antes del envío.
    /// Actualiza a sent/failed según la respuesta de Twilio.
    /// Nunca lanza excepción por fallos de Twilio; los encapsula en el resultado.
    /// </summary>
    Task<OutboundSendResult> SendAsync(
        OutboundMessageRequest request,
        CancellationToken      ct = default);
}
