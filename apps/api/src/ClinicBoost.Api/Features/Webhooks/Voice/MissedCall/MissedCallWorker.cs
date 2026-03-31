using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Automation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallWorker  (flow_01 — Llamada perdida → WA recovery)
//
// IHostedService que consume la cola IMissedCallJobQueue y ejecuta el
// flujo end-to-end delegando en Flow01Orchestrator.
//
// SECUENCIA DE PASOS
// ──────────────────
// 1. Crear un DI scope por job (DbContext es Scoped).
// 2. Filtrar: solo procesar estados realmente "perdida".
// 3. Inicializar TenantContext con rol "service" para RLS.
// 4. Registrar AutomationRun (observabilidad).
// 5. Delegar en Flow01Orchestrator:
//    · Idempotencia via IIdempotencyService.
//    · Resolver/crear paciente.
//    · Verificar consentimiento RGPD.
//    · Enviar WhatsApp de recovery via IOutboundMessageSender.
//    · Registrar métricas: missed_call_received, outbound_sent/failed.
// 6. Actualizar AutomationRun como completed/skipped/failed.
//
// GARANTÍAS DE RESILIENCIA
// ─────────────────────────
// · Cada job en su propio scope → fallo de un job no afecta a otros.
// · CancellationToken del host: el worker se detiene limpiamente.
// · El event ya está en processed_events → no hay re-entrega de Twilio.
// · Errores de Twilio: no propagan excepción; se registran en métricas.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Background service que ejecuta el flujo flow_01 (llamada perdida → WA recovery)
/// consumiendo jobs de <see cref="IMissedCallJobQueue"/>.
/// </summary>
public sealed class MissedCallWorker : BackgroundService
{
    private static readonly HashSet<string> MissedCallStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "no-answer",
            "busy",
            "failed"
        };

    private readonly IMissedCallJobQueue       _queue;
    private readonly IServiceScopeFactory      _scopeFactory;
    private readonly ILogger<MissedCallWorker> _logger;
    private readonly Flow01Options             _flow01Opts;

    public MissedCallWorker(
        IMissedCallJobQueue        queue,
        IServiceScopeFactory       scopeFactory,
        ILogger<MissedCallWorker>  logger,
        IOptions<Flow01Options>    flow01Opts)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _flow01Opts   = flow01Opts.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MissedCallWorker] Iniciado. Esperando jobs de flow_01.");

        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var _ = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["CallSid"]       = job.CallSid,
                    ["TenantId"]      = job.TenantId,
                    ["CorrelationId"] = job.CorrelationId,
                });

                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[MissedCallWorker] Shutdown solicitado. Deteniendo procesamiento.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[MissedCallWorker] Error inesperado procesando job. " +
                    "CallSid={CallSid} TenantId={TenantId}",
                    job.CallSid, job.TenantId);
            }
        }

        _logger.LogInformation("[MissedCallWorker] Detenido.");
    }

    // ── Lógica de procesamiento ───────────────────────────────────────────────

    private async Task ProcessJobAsync(MissedCallJob job, CancellationToken ct)
    {
        // ── Filtro: solo llamadas realmente perdidas ──────────────────────────
        if (!MissedCallStatuses.Contains(job.CallStatus))
        {
            _logger.LogDebug(
                "[MissedCallWorker] Estado '{Status}' no es llamada perdida. " +
                "CallSid={CallSid} Ignorado.",
                job.CallStatus, job.CallSid);
            return;
        }

        _logger.LogInformation(
            "[MissedCallWorker] Procesando llamada perdida. " +
            "CallSid={CallSid} Caller={Caller} Status={Status}",
            job.CallSid, job.CallerPhone, job.CallStatus);

        // ── Scope con tenant inicializado para RLS ────────────────────────────
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp  = scope.ServiceProvider;
        var db  = sp.GetRequiredService<AppDbContext>();

        var tenantCtx = sp.GetRequiredService<TenantContext>();
        tenantCtx.Initialize(job.TenantId, TenantRole.Service, userId: null);

        // ── AutomationRun (observabilidad) ────────────────────────────────────
        var run = new AutomationRun
        {
            TenantId      = job.TenantId,
            FlowId        = "flow_01",
            TriggerType   = "event",
            TriggerRef    = job.CallSid,
            Status        = "running",
            CorrelationId = Guid.TryParse(job.CorrelationId, out var corrId)
                                ? corrId
                                : Guid.NewGuid(),
        };
        db.AutomationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── P1: Verificar ventana máxima de tiempo ────────────────────────
            // Si el job lleva más de MaxDelayMinutes esperando, no tiene sentido
            // enviar el WhatsApp de recovery (experiencia degradada).
            var elapsedSinceCall = DateTimeOffset.UtcNow - job.ReceivedAt;
            if (elapsedSinceCall.TotalMinutes > _flow01Opts.MaxDelayMinutes)
            {
                _logger.LogWarning(
                    "[MissedCallWorker] Job expirado. ElapsedMin={Min} MaxDelayMin={Max} " +
                    "CallSid={CallSid} TenantId={TenantId}. Se omite el envío.",
                    (int)elapsedSinceCall.TotalMinutes, _flow01Opts.MaxDelayMinutes,
                    job.CallSid, job.TenantId);

                run.ItemsProcessed = 1;
                run.ItemsFailed    = 0;
                await CompleteRunAsync(db, run, "skipped",
                    $"Job expirado: {(int)elapsedSinceCall.TotalMinutes}min > MaxDelay:{_flow01Opts.MaxDelayMinutes}min", ct);
                return;
            }

            // ── Delegar en Flow01Orchestrator ─────────────────────────────────
            var orchestrator = sp.GetRequiredService<Flow01Orchestrator>();

            var result = await orchestrator.ExecuteAsync(
                tenantId:       job.TenantId,
                callSid:        job.CallSid,
                callerPhone:    job.CallerPhone,
                clinicPhone:    job.ClinicPhone,
                callReceivedAt: job.ReceivedAt,
                correlationId:  job.CorrelationId,
                ct:             ct);

            // ── Actualizar AutomationRun ──────────────────────────────────────
            if (result.IsSuccess)
            {
                run.ItemsProcessed = 1;
                run.ItemsSucceeded = 1;
                var status = result.FlowStep == "skipped" ? "skipped" : "completed";
                await CompleteRunAsync(db, run, status, null, ct);

                _logger.LogInformation(
                    "[MissedCallWorker] flow_01 {Status}. PatientId={PatientId} " +
                    "ResponseTimeMs={Ms} TwilioSid={Sid}",
                    status, result.PatientId, result.ResponseTimeMs, result.TwilioMessageSid);
            }
            else
            {
                run.ItemsProcessed = 1;
                run.ItemsFailed    = 1;
                await CompleteRunAsync(db, run, "failed", result.ErrorMessage, ct);

                _logger.LogWarning(
                    "[MissedCallWorker] flow_01 failed. PatientId={PatientId} " +
                    "Step={Step} Error={Error}",
                    result.PatientId, result.FlowStep, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            run.ItemsFailed = 1;
            await CompleteRunAsync(db, run, "failed", ex.Message, ct);
            throw;
        }
    }

    private static async Task CompleteRunAsync(
        AppDbContext      db,
        AutomationRun     run,
        string            status,
        string?           errorMessage,
        CancellationToken ct)
    {
        run.Status       = status;
        run.ErrorMessage = errorMessage;
        run.FinishedAt   = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
