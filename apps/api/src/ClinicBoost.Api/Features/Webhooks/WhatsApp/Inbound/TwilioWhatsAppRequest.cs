namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// TwilioWhatsAppRequest
//
// Proyección de los campos del webhook de WhatsApp de Twilio relevantes para
// el pipeline de recepción de mensajes inbound.
//
// Twilio envía el body como application/x-www-form-urlencoded.
// Ref: https://www.twilio.com/docs/whatsapp/api#received-whatsapp-message
//
// CAMPOS PRINCIPALES
// ──────────────────
// MessageSid     → SM… (identificador único del mensaje, clave de idempotencia)
// SmsMessageSid  → igual que MessageSid en WA (legacy alias)
// AccountSid     → AC… (cuenta Twilio)
// From           → whatsapp:+34612345678  (número del paciente, prefijado "whatsapp:")
// To             → whatsapp:+34910123456  (número de la clínica, prefijado "whatsapp:")
// Body           → texto del mensaje (puede ser vacío si es media)
// NumMedia       → número de adjuntos (0..N)
// MediaUrl0      → URL del primer adjunto (si NumMedia >= 1)
// MediaContentType0 → MIME del primer adjunto
// WaId           → número E.164 del emisor SIN prefijo "whatsapp:"
//                  (campo directo, más cómodo para resolver tenant)
// ProfileName    → nombre de perfil de WhatsApp del emisor (sin garantía de exactitud)
//
// PREFIJO "whatsapp:" EN FROM/TO
// ───────────────────────────────
// Twilio prefija los números de WhatsApp con "whatsapp:". Necesitamos el número
// en E.164 puro para la resolución de tenant y el lookup de paciente.
// Usamos WaId cuando está disponible; si no, limpiamos el prefijo de From/To.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// DTO de los campos del webhook de WhatsApp de Twilio.
/// Construir mediante <see cref="FromForm"/>.
/// </summary>
public sealed record TwilioWhatsAppRequest
{
    /// <summary>SID único del mensaje ("SM…"). Clave de idempotencia.</summary>
    public string MessageSid { get; init; } = string.Empty;

    /// <summary>Account SID de Twilio para trazabilidad.</summary>
    public string AccountSid { get; init; } = string.Empty;

    /// <summary>
    /// Número del paciente en formato Twilio: "whatsapp:+34612345678".
    /// Usar <see cref="CallerPhone"/> para el número E.164 limpio.
    /// </summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Número de la clínica en formato Twilio: "whatsapp:+34910123456".
    /// Usar <see cref="ClinicPhone"/> para el número E.164 limpio.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Cuerpo textual del mensaje. Vacío si es un mensaje de sólo media.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Número de archivos multimedia adjuntos.</summary>
    public int NumMedia { get; init; }

    /// <summary>URL del primer adjunto (si <see cref="NumMedia"/> >= 1).</summary>
    public string? MediaUrl0 { get; init; }

    /// <summary>MIME type del primer adjunto.</summary>
    public string? MediaContentType0 { get; init; }

    /// <summary>
    /// Número E.164 del emisor sin el prefijo "whatsapp:" (campo WaId de Twilio).
    /// Presente en mensajes WA estándar. Preferir sobre limpiar <see cref="From"/>.
    /// </summary>
    public string WaId { get; init; } = string.Empty;

    /// <summary>Nombre de perfil de WhatsApp del emisor (no fiable para identidad).</summary>
    public string ProfileName { get; init; } = string.Empty;

    // ── Propiedades derivadas ─────────────────────────────────────────────────

    /// <summary>
    /// Número E.164 del paciente sin prefijo "whatsapp:".
    /// Prefiere <see cref="WaId"/> (campo directo) sobre limpiar <see cref="From"/>.
    /// </summary>
    public string CallerPhone =>
        !string.IsNullOrWhiteSpace(WaId)
            ? (WaId.StartsWith('+') ? WaId : "+" + WaId)
            : StripWhatsAppPrefix(From);

    /// <summary>
    /// Número E.164 de la clínica sin prefijo "whatsapp:".
    /// Usado para la resolución de tenant por número.
    /// </summary>
    public string ClinicPhone => StripWhatsAppPrefix(To);

    /// <summary>True si el mensaje tiene texto (Body no vacío).</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Body);

    /// <summary>True si el mensaje tiene al menos un adjunto multimedia.</summary>
    public bool HasMedia => NumMedia > 0;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Construye el DTO desde un <see cref="IFormCollection"/> del webhook de Twilio.
    /// </summary>
    public static TwilioWhatsAppRequest FromForm(IFormCollection form) => new()
    {
        MessageSid        = form["MessageSid"].ToString(),
        AccountSid        = form["AccountSid"].ToString(),
        From              = form["From"].ToString(),
        To                = form["To"].ToString(),
        Body              = form["Body"].ToString(),
        WaId              = form["WaId"].ToString(),
        ProfileName       = form["ProfileName"].ToString(),
        NumMedia          = int.TryParse(form["NumMedia"].ToString(), out var n) ? n : 0,
        MediaUrl0         = form["MediaUrl0"].ToString().NullIfEmpty(),
        MediaContentType0 = form["MediaContentType0"].ToString().NullIfEmpty(),
    };

    /// <summary>
    /// Genera los pares clave-valor del form para pasarlos al validador de firma HMAC.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> ToSignatureParams(
        IFormCollection form) =>
        form.SelectMany(f => f.Value.Select(v =>
            new KeyValuePair<string, string>(f.Key, v ?? string.Empty)));

    // ── Helpers privados ──────────────────────────────────────────────────────

    /// <summary>Elimina el prefijo "whatsapp:" de un número Twilio.</summary>
    private static string StripWhatsAppPrefix(string number) =>
        number.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase)
            ? number["whatsapp:".Length..]
            : number;
}

internal static class StringExtensions
{
    /// <summary>Devuelve null si la cadena está vacía o es whitespace.</summary>
    internal static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
