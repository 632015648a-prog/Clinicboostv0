namespace ClinicBoost.Api.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// ITwilioSignatureValidator
//
// PROPÓSITO
// ─────────
// Valida la autenticidad de las peticiones entrantes de Twilio comprobando
// la cabecera X-Twilio-Signature según la especificación oficial:
//   https://www.twilio.com/docs/usage/webhooks/webhooks-security
//
// ALGORITMO TWILIO
// ────────────────
// 1. Concatenar la URL completa del webhook (scheme + host + path + querystring).
// 2. Ordenar los parámetros POST (form) alfabéticamente por nombre.
// 3. Concatenar cada par nombre+valor (sin delimitador) al final de la URL.
// 4. Calcular HMAC-SHA1 con el AuthToken del número de Twilio como clave.
// 5. Encodificar el resultado en Base64.
// 6. Comparar con el valor de X-Twilio-Signature (timing-safe).
//
// SEGURIDAD
// ─────────
// · La comparación usa CryptographicOperations.FixedTimeEquals para evitar
//   ataques de timing (side-channel).
// · El AuthToken NUNCA se loguea ni se incluye en respuestas de error.
// · Si la cabecera no existe → rechazar siempre (no fallar abierto).
// · Los endpoints de webhook deben llamar a este validador ANTES de procesar
//   cualquier dato del cuerpo de la petición.
//
// USO EN HANDLER
// ──────────────
//   var validationUrl = $"{req.Scheme}://{req.Host}{req.Path}{req.QueryString}";
//   if (!_validator.IsValid(req.Form, validationUrl, twilioSignature))
//       return Results.StatusCode(403);
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Valida la firma HMAC-SHA1 que Twilio incluye en cada webhook entrante.
/// Registrar como <b>Singleton</b>: no tiene estado mutable.
/// </summary>
public interface ITwilioSignatureValidator
{
    /// <summary>
    /// Verifica que la petición proviene realmente de Twilio.
    /// </summary>
    /// <param name="formParams">
    /// Parámetros del body application/x-www-form-urlencoded tal como los envía Twilio.
    /// </param>
    /// <param name="requestUrl">
    /// URL completa del webhook incluyendo scheme, host, path y querystring.
    /// Debe coincidir exactamente con la URL configurada en el dashboard de Twilio.
    /// </param>
    /// <param name="twilioSignature">
    /// Valor de la cabecera <c>X-Twilio-Signature</c> de la petición entrante.
    /// </param>
    /// <returns>
    /// <c>true</c> si la firma es válida; <c>false</c> en cualquier otro caso.
    /// </returns>
    bool IsValid(
        IEnumerable<KeyValuePair<string, string>> formParams,
        string                                     requestUrl,
        string                                     twilioSignature);
}
