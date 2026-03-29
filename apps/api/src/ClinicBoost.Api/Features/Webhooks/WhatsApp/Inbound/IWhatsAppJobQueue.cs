namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

/// <summary>
/// Cola de jobs de mensajes WhatsApp inbound.
/// La implementación en producción usa un <see cref="System.Threading.Channels.Channel{T}"/>
/// acotado con drop-on-full para no bloquear el thread HTTP.
/// Registrar como <b>Singleton</b>.
/// </summary>
public interface IWhatsAppJobQueue
{
    /// <summary>
    /// Encola un job para procesamiento asíncrono.
    /// </summary>
    /// <returns>
    /// <c>true</c> si el job fue encolado; <c>false</c> si la cola está llena
    /// (back-pressure: el evento ya fue deduplicado, Twilio reintentará el webhook).
    /// </returns>
    ValueTask<bool> EnqueueAsync(WhatsAppInboundJob job, CancellationToken ct = default);

    /// <summary>
    /// Secuencia asíncrona de jobs para consumir desde el worker.
    /// Se completa cuando el host solicita shutdown.
    /// </summary>
    IAsyncEnumerable<WhatsAppInboundJob> ReadAllAsync(CancellationToken ct = default);
}
