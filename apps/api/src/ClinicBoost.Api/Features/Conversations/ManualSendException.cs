namespace ClinicBoost.Api.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ManualSendException.cs
//
// Excepción tipada para errores de negocio en el envío manual de mensajes
// desde el operador. El mensaje es legible y se muestra directamente al
// operador en la Inbox; no debe ser un error técnico interno.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Error de negocio al enviar un mensaje manual desde el operador.
/// Código HTTP sugerido: 422 para validaciones de negocio, 502 para fallos de Twilio.
/// </summary>
public sealed class ManualSendException : Exception
{
    /// <summary>Código HTTP sugerido para el endpoint.</summary>
    public int HttpStatusCode { get; }

    public ManualSendException(int httpStatusCode, string message)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
    }
}
