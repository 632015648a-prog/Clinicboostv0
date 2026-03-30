namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Status;

// ════════════════════════════════════════════════════════════════════════════
// TwilioMessageStatusRequest
//
// Proyección de los campos del callback de estado de mensaje de Twilio.
// Twilio envía estos callbacks cuando el estado de un mensaje outbound cambia.
//
// Ref: https://www.twilio.com/docs/messaging/guides/track-outbound-message-status
//
// CAMPOS PRINCIPALES (siempre presentes)
// ──────────────────────────────────────
// MessageSid      → "SM…" — identificador único del mensaje (clave de correlación)
// MessageStatus   → "sent" | "delivered" | "read" | "failed" | "undelivered"
// AccountSid      → "AC…" — cuenta Twilio
// To              → número destino ("whatsapp:+34612…" o "+34612…")
// From            → número origen ("whatsapp:+34910…" o "+34910…")
//
// CAMPOS DE ERROR (presentes cuando MessageStatus = "failed" | "undelivered")
// ────────────────────────────────────────────────────────────────────────────
// ErrorCode       → código numérico de Twilio (p.ej. "30008")
// ErrorMessage    → descripción textual del error
//
// CAMPOS OPCIONALES
// ─────────────────
// ChannelMessageSid   → SID del canal (WA Business API)
// ChannelPrefix       → "whatsapp" cuando es WA
// MessagePrice        → precio en USD (si el plan lo reporta)
// PriceUnit           → "USD"
// RawDlrDoneDate      → timestamp reportado por el operador (DLR done date)
//
// DEDUPLICACIÓN
// ─────────────
// La clave de idempotencia es (MessageSid + MessageStatus):
//   · Twilio puede re-entregar el mismo callback varias veces.
//   · El mismo mensaje puede tener callbacks para distintos estados.
//   · La combinación (SID, status) identifica unívocamente una transición.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// DTO del callback de estado de mensaje de Twilio (form-urlencoded).
/// </summary>
public sealed record TwilioMessageStatusRequest
{
    /// <summary>SID del mensaje ("SM…"). Clave de correlación con messages.provider_message_id.</summary>
    public string MessageSid { get; init; } = string.Empty;

    /// <summary>Estado reportado: sent | delivered | read | failed | undelivered.</summary>
    public string MessageStatus { get; init; } = string.Empty;

    /// <summary>Account SID de Twilio para trazabilidad.</summary>
    public string AccountSid { get; init; } = string.Empty;

    /// <summary>Número de destino (puede llevar prefijo "whatsapp:").</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Número de origen (puede llevar prefijo "whatsapp:").</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>Código de error de Twilio. Presente solo en failed/undelivered.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Descripción del error. Presente solo en failed/undelivered.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp reportado por el operador de red (DLR done date).
    /// Formato: "Fri, 14 Jul 2023 12:34:56 +0000" (RFC 2822).
    /// </summary>
    public string? RawDlrDoneDate { get; init; }

    // ── Propiedades derivadas ─────────────────────────────────────────────

    /// <summary>True si el estado indica un error de entrega.</summary>
    public bool IsFailure =>
        MessageStatus is "failed" or "undelivered";

    /// <summary>True si el callback indica entrega exitosa al dispositivo.</summary>
    public bool IsDelivered =>
        MessageStatus is "delivered";

    /// <summary>True si el destinatario leyó el mensaje.</summary>
    public bool IsRead =>
        MessageStatus is "read";

    /// <summary>
    /// Canal detectado a partir del prefijo del número (To o From).
    /// "whatsapp" cuando alguno tiene prefijo "whatsapp:", "sms" en caso contrario.
    /// </summary>
    public string Channel =>
        To.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ||
        From.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? "whatsapp"
            : "sms";

    /// <summary>
    /// Intenta parsear RawDlrDoneDate a DateTimeOffset.
    /// Devuelve null si el campo está vacío o el formato no es reconocido.
    /// </summary>
    public DateTimeOffset? ProviderTimestamp =>
        !string.IsNullOrWhiteSpace(RawDlrDoneDate) &&
        DateTimeOffset.TryParse(RawDlrDoneDate, out var ts)
            ? ts
            : null;

    // ── Factory ───────────────────────────────────────────────────────────

    /// <summary>Construye el DTO desde el IFormCollection del callback.</summary>
    public static TwilioMessageStatusRequest FromForm(IFormCollection form) => new()
    {
        MessageSid     = form["MessageSid"].ToString(),
        MessageStatus  = form["MessageStatus"].ToString(),
        AccountSid     = form["AccountSid"].ToString(),
        To             = form["To"].ToString(),
        From           = form["From"].ToString(),
        ErrorCode      = form["ErrorCode"].ToString().NullIfEmpty(),
        ErrorMessage   = form["ErrorMessage"].ToString().NullIfEmpty(),
        RawDlrDoneDate = form["RawDlrDoneDate"].ToString().NullIfEmpty(),
    };

    /// <summary>Pares clave-valor para el validador de firma HMAC.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ToSignatureParams(
        IFormCollection form) =>
        form.SelectMany(f => f.Value.Select(v =>
            new KeyValuePair<string, string>(f.Key, v ?? string.Empty)));

    // ── Clave de idempotencia ──────────────────────────────────────────────

    /// <summary>
    /// Identificador de idempotencia para la transición (SID, status).
    /// El mismo mensaje puede tener múltiples callbacks con distintos estados;
    /// la combinación (MessageSid + "_" + MessageStatus) es única por transición.
    /// </summary>
    public string IdempotencyEventId => $"{MessageSid}_{MessageStatus}";
}

// extensión local NullIfEmpty — la misma que en TwilioWhatsAppRequest
// (en un proyecto real se movería a un shared helper)
file static class StringEx
{
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
