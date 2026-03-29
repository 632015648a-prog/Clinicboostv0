namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// IMissedCallJobQueue
//
// PROPÓSITO
// ─────────
// Desacopla el handler HTTP del worker de fondo.
// El handler encola el job en < 1 ms y devuelve 200 a Twilio.
// El worker consume el job y ejecuta el flujo pesado (búsqueda de paciente,
// construcción de mensaje, envío WhatsApp, etc.) de forma asíncrona.
//
// IMPLEMENTACIÓN EN MEMORIA
// ─────────────────────────
// · Sistema.Threading.Channels: FIFO, bounded capacity, sin dependencias externas.
// · Sufficient para single-instance deployment (MVP / Scale plan inicial).
// · Para multi-instancia horizontal → sustituir por Azure Service Bus o SQS
//   implementando la misma interfaz sin cambiar el handler.
//
// GARANTÍAS
// ─────────
// · EnqueueAsync: fire-and-forget desde el handler. Si el channel está lleno
//   (capacidad superada) devuelve false en lugar de bloquear el request.
// · El worker lee con ReadAllAsync que respeta el CancellationToken del host.
// · Si el proceso muere con jobs pendientes en el channel: los jobs se pierden.
//   La idempotencia en processed_events garantiza que Twilio reintentará el
//   webhook y el job se reencolará en el siguiente intento.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Cola en memoria para <see cref="MissedCallJob"/>.
/// Registrar como <b>Singleton</b> (el Channel vive toda la aplicación).
/// </summary>
public interface IMissedCallJobQueue
{
    /// <summary>
    /// Encola un job para procesamiento asíncrono.
    /// </summary>
    /// <returns>
    /// <c>true</c> si el job fue encolado exitosamente.
    /// <c>false</c> si el canal está lleno (back-pressure: no bloquear el request).
    /// </returns>
    ValueTask<bool> EnqueueAsync(MissedCallJob job, CancellationToken ct = default);

    /// <summary>
    /// Expone los jobs como secuencia asíncrona para consumo por el worker.
    /// Se cierra automáticamente cuando el host inicia el shutdown.
    /// </summary>
    IAsyncEnumerable<MissedCallJob> ReadAllAsync(CancellationToken ct = default);
}
