namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// TwilioVoiceRequest
//
// Proyección de los campos del webhook de voz de Twilio que son relevantes
// para flow_00. Twilio envía el body como application/x-www-form-urlencoded.
//
// CAMPOS DOCUMENTADOS
// ───────────────────
// Ref: https://www.twilio.com/docs/voice/webhook/voice-webhook-request
//
// CallSid     → ID único de la llamada ("CA…")
// From        → número del llamante en E.164 (+34612345678)
// To          → número de la clínica en E.164 (+34910123456)
// CallStatus  → estado: queued | ringing | in-progress | completed |
//               busy | failed | no-answer | canceled
// AccountSid  → Account SID de Twilio ("AC…")
// Direction   → inbound | outbound-dial | …
// ApiVersion  → p.ej. "2010-04-01"
//
// NOTA: Twilio puede enviar más campos (CallerCity, etc.) que se ignoran.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Campos del webhook de voz de Twilio necesarios para flow_00.
/// Se enlaza desde IFormCollection en el handler del endpoint.
/// </summary>
public sealed record TwilioVoiceRequest
{
    /// <summary>ID único de la llamada ("CA…").</summary>
    public string CallSid     { get; init; } = string.Empty;

    /// <summary>Número del llamante en E.164.</summary>
    public string From        { get; init; } = string.Empty;

    /// <summary>Número de la clínica (destino de la llamada) en E.164.</summary>
    public string To          { get; init; } = string.Empty;

    /// <summary>Estado de la llamada: no-answer | busy | failed | completed…</summary>
    public string CallStatus  { get; init; } = string.Empty;

    /// <summary>Account SID de Twilio para trazabilidad.</summary>
    public string AccountSid  { get; init; } = string.Empty;

    /// <summary>
    /// Construye el request desde un IFormCollection.
    /// Usar en el handler tras leer el body de la petición.
    /// </summary>
    public static TwilioVoiceRequest FromForm(IFormCollection form) => new()
    {
        CallSid    = form["CallSid"].ToString(),
        From       = form["From"].ToString(),
        To         = form["To"].ToString(),
        CallStatus = form["CallStatus"].ToString(),
        AccountSid = form["AccountSid"].ToString()
    };

    /// <summary>
    /// Genera los pares clave-valor tal como los usaría Twilio para firmar el webhook.
    /// Necesario para pasar al validador de firma.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> ToSignatureParams(IFormCollection form) =>
        form.SelectMany(f => f.Value.Select(v =>
            new KeyValuePair<string, string>(f.Key, v ?? string.Empty)));
}
