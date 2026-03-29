using System.ComponentModel.DataAnnotations;

namespace ClinicBoost.Api.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// TwilioOptions
//
// Configuración tipada para la integración con Twilio.
// Se enlaza con la sección "Twilio" de appsettings.json.
//
// SEGURIDAD: AuthToken y AccountSid NUNCA deben aparecer en logs.
//   Usar variables de entorno en producción:
//     TWILIO__AUTHTOKEN=xxxxx
//     TWILIO__ACCOUNTSID=ACxxxxx
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Opciones de configuración de Twilio.
/// Validadas con DataAnnotations al arrancar la aplicación.
/// </summary>
public sealed class TwilioOptions
{
    public const string SectionName = "Twilio";

    /// <summary>
    /// Account SID de Twilio (comienza con "AC").
    /// Necesario para llamadas a la Twilio REST API.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public required string AccountSid { get; init; }

    /// <summary>
    /// Auth Token de Twilio. Se usa como clave HMAC-SHA1 en la validación
    /// de firma de webhooks. NUNCA loguear este valor.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public required string AuthToken { get; init; }

    /// <summary>
    /// URL base pública del webhook de voz de ClinicBoost, tal y como está
    /// configurada en el dashboard de Twilio. Debe coincidir exactamente
    /// (scheme, host, path, sin trailing slash) para que la firma sea válida.
    ///
    /// Ejemplo: "https://api.clinicboost.es"
    ///
    /// Si está vacía, el validador construye la URL dinámica desde la request.
    /// </summary>
    public string? WebhookBaseUrl { get; init; }
}
