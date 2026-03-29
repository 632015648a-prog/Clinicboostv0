using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// ChannelWhatsAppJobQueue
//
// Implementación de IWhatsAppJobQueue basada en System.Threading.Channels.
//
// CONFIGURACIÓN DEL CHANNEL
// ─────────────────────────
// · BoundedChannel con capacidad 1 000.
//   WhatsApp Business tiene un throughput limitado por número (~80 msg/s
//   en edge cases). 1 000 slots = buffer generoso para picos cortos.
// · BoundedChannelFullMode.DropWrite: si el canal está lleno el handler
//   recibe false y puede alertar. NO bloquea el thread HTTP.
// · SingleReader = false / SingleWriter = false: multi-producer, multi-consumer.
//
// CORRELACIÓN
// ───────────
// El CorrelationId del job proviene del HttpContext.TraceIdentifier del
// request original, permitiendo correlacionar logs del handler, la cola
// y el worker en Serilog.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Cola en memoria de <see cref="WhatsAppInboundJob"/> para procesamiento asíncrono.
/// Registrar como <b>Singleton</b>.
/// </summary>
public sealed class ChannelWhatsAppJobQueue : IWhatsAppJobQueue
{
    private const int ChannelCapacity = 1_000;

    private readonly Channel<WhatsAppInboundJob>       _channel;
    private readonly ILogger<ChannelWhatsAppJobQueue>  _logger;

    public ChannelWhatsAppJobQueue(ILogger<ChannelWhatsAppJobQueue> logger)
    {
        _logger  = logger;
        _channel = Channel.CreateBounded<WhatsAppInboundJob>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = false
            });
    }

    /// <inheritdoc/>
    public async ValueTask<bool> EnqueueAsync(
        WhatsAppInboundJob job,
        CancellationToken  ct = default)
    {
        var enqueued = _channel.Writer.TryWrite(job);

        if (enqueued)
        {
            _logger.LogDebug(
                "[WAQueue] Job encolado. " +
                "MessageSid={Sid} TenantId={TenantId} CorrelationId={Corr}",
                job.MessageSid, job.TenantId, job.CorrelationId);
        }
        else
        {
            _logger.LogWarning(
                "[WAQueue] Canal lleno (capacity={Cap}). Job descartado en memoria. " +
                "Twilio reintentará. MessageSid={Sid} TenantId={TenantId}",
                ChannelCapacity, job.MessageSid, job.TenantId);
        }

        return await ValueTask.FromResult(enqueued);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<WhatsAppInboundJob> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Señala el fin de la escritura para que <see cref="ReadAllAsync"/> termine
    /// ordenadamente en el shutdown del host.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
