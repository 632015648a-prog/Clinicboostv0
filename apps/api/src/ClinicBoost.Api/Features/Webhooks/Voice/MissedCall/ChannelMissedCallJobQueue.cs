using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// ChannelMissedCallJobQueue
//
// Implementación de IMissedCallJobQueue basada en System.Threading.Channels.
//
// CONFIGURACIÓN DEL CHANNEL
// ─────────────────────────
// · BoundedChannel con capacidad 500 (configurable).
//   Justificación: Twilio entrega hasta ~1 req/s por número por defecto.
//   500 slots = ~8 minutos de buffer ante un pico o lentitud del worker.
// · BoundedChannelFullMode.DropWrite: si el canal está lleno, el handler
//   recibe false de EnqueueAsync y puede registrar la métrica / alertar.
//   NO se bloquea el request HTTP (la alternativa Wait bloquearía el thread).
// · SingleReader = false: permite múltiples workers si se necesita en futuro.
// · SingleWriter = false: múltiples requests concurrentes escriben al mismo tiempo.
//
// POR QUÉ NO Hangfire/RabbitMQ EN EL MVP
// ─────────────────────────────────────────
// · Zero dependencias externas: no necesita Redis, Postgres de jobs ni broker.
// · Tiempo de encolado < 1 μs (escritura en memoria).
// · La idempotencia con processed_events garantiza que en caso de caída del
//   proceso, Twilio reintentará y el webhook se procesará en el restart.
// · Cuando se necesite escalado horizontal o durabilidad persistida,
//   se sustituye únicamente esta clase implementando la misma interfaz.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Cola de jobs de llamada perdida basada en <see cref="Channel{T}"/> de .NET.
/// Registrar como <b>Singleton</b>.
/// </summary>
public sealed class ChannelMissedCallJobQueue : IMissedCallJobQueue
{
    /// <summary>Máximo de jobs en vuelo antes de aplicar back-pressure.</summary>
    private const int ChannelCapacity = 500;

    private readonly Channel<MissedCallJob> _channel;
    private readonly ILogger<ChannelMissedCallJobQueue> _logger;

    public ChannelMissedCallJobQueue(ILogger<ChannelMissedCallJobQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<MissedCallJob>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode     = BoundedChannelFullMode.DropWrite,
            SingleReader = false,   // permite múltiples workers en el futuro
            SingleWriter = false    // múltiples requests concurrentes
        });
    }

    /// <inheritdoc/>
    public async ValueTask<bool> EnqueueAsync(MissedCallJob job, CancellationToken ct = default)
    {
        // TryWrite es O(1) y no bloquea el thread del request.
        // WriteAsync solo se usa aquí para el caso de canal lleno con espera,
        // pero en BoundedChannelFullMode.DropWrite TryWrite basta.
        var enqueued = _channel.Writer.TryWrite(job);

        if (!enqueued)
        {
            _logger.LogWarning(
                "[MissedCallQueue] Canal lleno (capacity={Capacity}). " +
                "Job descartado en memoria. Twilio reintentará el webhook. " +
                "CallSid={CallSid} TenantId={TenantId}",
                ChannelCapacity, job.CallSid, job.TenantId);
        }
        else
        {
            _logger.LogDebug(
                "[MissedCallQueue] Job encolado. " +
                "CallSid={CallSid} TenantId={TenantId} CorrelationId={CorrelationId}",
                job.CallSid, job.TenantId, job.CorrelationId);
        }

        return await ValueTask.FromResult(enqueued);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<MissedCallJob> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Completa el canal para que ReadAllAsync finalice ordenadamente.
    /// Llamar durante el shutdown del host.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
