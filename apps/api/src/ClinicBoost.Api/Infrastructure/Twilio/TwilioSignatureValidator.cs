using System.Security.Cryptography;
using System.Text;

namespace ClinicBoost.Api.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// TwilioSignatureValidator
//
// Implementa el algoritmo oficial de verificación de Twilio usando HMAC-SHA1.
// Ref: https://www.twilio.com/docs/usage/webhooks/webhooks-security
//
// NOTAS DE IMPLEMENTACIÓN
// ───────────────────────
// · Se usa HMAC-SHA1 porque Twilio lo especifica así (no es elección nuestra).
//   SHA-1 aquí no es una vulnerabilidad criptográfica porque el secreto
//   (AuthToken, 32 chars hex) es el que protege el canal, no la función hash.
// · FixedTimeEquals previene timing attacks al comparar los hashes.
// · authToken se almacena como bytes (conversión lazy en constructor) para
//   evitar allocations repetidas en el hot path del webhook.
// · La instancia es Singleton: el AuthToken es constante en tiempo de ejecución.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación del validador de firma HMAC-SHA1 de Twilio.
/// Registrar como <b>Singleton</b>.
/// </summary>
public sealed class TwilioSignatureValidator : ITwilioSignatureValidator
{
    private readonly byte[] _authTokenBytes;

    public TwilioSignatureValidator(string authToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken, nameof(authToken));
        // El AuthToken es la clave HMAC; se convierte a bytes una sola vez.
        _authTokenBytes = Encoding.UTF8.GetBytes(authToken);
    }

    /// <inheritdoc/>
    public bool IsValid(
        IEnumerable<KeyValuePair<string, string>> formParams,
        string                                     requestUrl,
        string                                     twilioSignature)
    {
        // Defensa ante null/vacío — nunca fallar abierto.
        if (string.IsNullOrWhiteSpace(requestUrl) ||
            string.IsNullOrWhiteSpace(twilioSignature))
            return false;

        // ── Paso 1: construir el string a firmar ──────────────────────────────
        // URL + pares nombre|valor ordenados alfabéticamente (sin separador entre pares).
        var sb = new StringBuilder(requestUrl);

        foreach (var kv in formParams.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }

        var dataToSign = sb.ToString();

        // ── Paso 2: HMAC-SHA1 ────────────────────────────────────────────────
        var dataBytes      = Encoding.UTF8.GetBytes(dataToSign);
        var computedBytes  = HMACSHA1.HashData(_authTokenBytes, dataBytes);
        var computedBase64 = Convert.ToBase64String(computedBytes);

        // ── Paso 3: comparación timing-safe ──────────────────────────────────
        // Decodificamos la firma entrante a bytes para comparar en bytes,
        // evitando diferencias de codificación Base64 que enmascaren timing.
        try
        {
            var incomingBytes = Convert.FromBase64String(twilioSignature);
            return CryptographicOperations.FixedTimeEquals(
                computedBytes,
                incomingBytes);
        }
        catch (FormatException)
        {
            // La firma entrante no es Base64 válido → inválida.
            return false;
        }
    }
}
