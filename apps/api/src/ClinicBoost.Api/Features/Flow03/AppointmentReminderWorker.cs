using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Automation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Flow03;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentReminderWorker
//
// Worker de polling que busca citas que necesitan recordatorio y las envía
// vía Flow03Orchestrator.
//
// DISEÑO
// ──────
//  · BackgroundService con bucle de sleep (patrón SessionCleanupWorker).
//  · Primer ciclo: espera un intervalo antes de procesar. Esto evita
//    que el worker intente envíos antes de que la BD esté lista al arrancar.
//  · Por ciclo: un AutomationRun cubre el lote completo.
//  · Por cita: scope DI aislado para garantizar AppDbContext sin tracking sucio.
//  · El query usa un horizonte configurable (MaxHoursAheadToQuery) para cubrir
//    cualquier configuración de reminder_hours_before de hasta N horas.
//  · El orchestrator re-verifica la ventana por tenant antes de enviar.
//
// OBSERVABILIDAD
// ──────────────
//  · AutomationRun por ciclo con ItemsProcessed / Succeeded / Failed / Skipped.
//  · FlowMetricsEvent por cita (en el orchestrator).
//  · Serilog con nivel Info/Warning para cada ciclo.
//
// FALTA DE ESTADO
// ───────────────
//  · El worker no guarda estado entre ciclos. La idempotencia está garantizada
//    por Appointment.ReminderSentAt (dominio) + IIdempotencyService (servicio).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Worker que envía recordatorios WhatsApp para citas próximas (flow_03).
/// </summary>
public sealed class AppointmentReminderWorker : BackgroundService
{
    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly IOptions<Flow03Options>             _opts;
    private readonly ILogger<AppointmentReminderWorker> _logger;

    public AppointmentReminderWorker(
        IServiceScopeFactory               scopeFactory,
        IOptions<Flow03Options>             opts,
        ILogger<AppointmentReminderWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _opts         = opts;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[Flow03Worker] Iniciado. PollInterval={Min} min MaxHoursAhead={H} h",
            _opts.Value.PollIntervalMinutes, _opts.Value.MaxHoursAheadToQuery);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Esperar antes de procesar (el primer ciclo también espera
            // para dar tiempo a la BD a estar disponible al arrancar).
            await Task.Delay(
                TimeSpan.FromMinutes(_opts.Value.PollIntervalMinutes),
                stoppingToken);

            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Flow03Worker] Error inesperado en el ciclo. Se reintentará en el próximo tick.");
            }
        }

        _logger.LogInformation("[Flow03Worker] Detenido.");
    }

    // ── Ciclo principal ───────────────────────────────────────────────────────

    private async Task RunCycleAsync(CancellationToken ct)
    {
        // ── 1. Resolver IDs de citas con un DbContext de sólo lectura ─────────
        var now              = DateTimeOffset.UtcNow;
        var windowUpperBound = now.AddHours(_opts.Value.MaxHoursAheadToQuery)
                                  .AddMinutes(_opts.Value.PollIntervalMinutes);

        List<(Guid Id, Guid TenantId)> dueAppointments;

        await using (var outerScope = _scopeFactory.CreateAsyncScope())
        {
            var db = outerScope.ServiceProvider.GetRequiredService<AppDbContext>();

            dueAppointments = await db.Appointments
                .AsNoTracking()
                .Where(a =>
                    a.StartsAtUtc > now &&
                    a.StartsAtUtc <= windowUpperBound &&
                    a.ReminderSentAt == null &&
                    a.Status == AppointmentStatus.Scheduled)
                .Select(a => new { a.Id, a.TenantId })
                .ToListAsync(ct)
                .ContinueWith(t =>
                    t.Result.Select(x => (x.Id, x.TenantId)).ToList(),
                    ct, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Current);
        }

        if (dueAppointments.Count == 0)
        {
            _logger.LogDebug(
                "[Flow03Worker] Sin citas en ventana (ahora={Now}, hasta={Upper}).",
                now, windowUpperBound);
            return;
        }

        _logger.LogInformation(
            "[Flow03Worker] {Count} cita(s) en ventana de recordatorio.",
            dueAppointments.Count);

        // ── 2. Crear AutomationRun para observabilidad ────────────────────────
        Guid runId;
        await using (var runScope = _scopeFactory.CreateAsyncScope())
        {
            var runDb = runScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var run = new AutomationRun
            {
                TenantId    = dueAppointments[0].TenantId,   // primer tenant del lote
                FlowId      = "flow_03",
                TriggerType = "scheduled",
                TriggerRef  = "appointment:reminder:flow_03",
                Status      = "running",
                CorrelationId = Guid.NewGuid(),
            };
            runDb.AutomationRuns.Add(run);
            await runDb.SaveChangesAsync(ct);
            runId = run.Id;
        }

        // ── 3. Procesar cada cita en su propio scope ──────────────────────────
        int succeeded = 0, failed = 0, skipped = 0;

        foreach (var (aptId, tenantId) in dueAppointments)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<Flow03Orchestrator>();

                var correlationId = $"flow03-{aptId:N}";
                var result = await orchestrator.ExecuteAsync(tenantId, aptId, correlationId, ct);

                if (result.IsSuccess && result.FlowStep == "reminder_sent")
                    succeeded++;
                else if (result.IsSuccess && result.FlowStep == "skipped")
                    skipped++;
                else
                    failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "[Flow03Worker] Error procesando cita. AppointmentId={AptId} TenantId={TenantId}",
                    aptId, tenantId);
            }
        }

        // ── 4. Cerrar AutomationRun ───────────────────────────────────────────
        await using (var closeScope = _scopeFactory.CreateAsyncScope())
        {
            var closeDb = closeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var run     = await closeDb.AutomationRuns.FindAsync([runId], ct);

            if (run is not null)
            {
                run.Status          = failed > 0 ? "failed" : "completed";
                run.ItemsProcessed  = dueAppointments.Count;
                run.ItemsSucceeded  = succeeded;
                run.ItemsFailed     = failed;
                run.FinishedAt      = DateTimeOffset.UtcNow;
                await closeDb.SaveChangesAsync(ct);
            }
        }

        _logger.LogInformation(
            "[Flow03Worker] Ciclo completado. Enviados={Sent} Saltados={Skip} Fallidos={Fail}",
            succeeded, skipped, failed);
    }
}
