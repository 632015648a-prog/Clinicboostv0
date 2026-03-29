using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Automation;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallWorker  (flow_00 — Llamada perdida)
//
// IHostedService que consume la cola IMissedCallJobQueue y ejecuta el trabajo
// pesado de forma asíncrona, fuera del ciclo de vida del request HTTP.
//
// SECUENCIA DE PASOS (flow_00)
// ────────────────────────────
// 1. Crear un DI scope por job (el DbContext es Scoped).
// 2. Filtrar: solo procesar estados que son realmente "perdida".
// 3. Resolver o crear el paciente por número de teléfono.
// 4. Verificar consentimiento RGPD del paciente.
// 5. Registrar el AutomationRun para observabilidad.
// 6. Registrar el evento en WebhookEvents para trazabilidad completa.
// 7. [Stub] Encolar/enviar el WhatsApp de flow_00 → implementación en
//    el sprint de integración con Twilio WhatsApp API.
//
// GARANTÍAS DE RESILIENCIA
// ─────────────────────────
// · Cada job se ejecuta en su propio scope → fallo de un job no afecta a otros.
// · CancellationToken del host: el worker se detiene limpiamente en shutdown.
// · Errores no propagados: se loguean y el worker continúa con el siguiente job.
//   (El procesamiento ya fue marcado en processed_events; no hay re-entrega.)
// · El DbContext recibe el tenant_id mediante TenantContext.Initialize para que
//   el interceptor de RLS funcione correctamente.
//
// LO QUE NO HACE EL WORKER (deliberadamente)
// ────────────────────────────────────────────
// · NO envía WhatsApp (stub que se implementará en la feature de mensajería).
// · NO confirma citas (la IA propone; el backend confirma en otro endpoint).
// · NO actualiza el estado de la llamada en Twilio.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Background service que ejecuta el flujo "flow_00 — Llamada perdida"
/// consumiendo jobs de <see cref="IMissedCallJobQueue"/>.
/// </summary>
public sealed class MissedCallWorker : BackgroundService
{
    // Estados de Twilio que representan una llamada realmente perdida
    private static readonly HashSet<string> MissedCallStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "no-answer",
            "busy",
            "failed"
        };

    private readonly IMissedCallJobQueue          _queue;
    private readonly IServiceScopeFactory         _scopeFactory;
    private readonly ILogger<MissedCallWorker>    _logger;

    public MissedCallWorker(
        IMissedCallJobQueue        queue,
        IServiceScopeFactory       scopeFactory,
        ILogger<MissedCallWorker>  logger)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <summary>
    /// Loop principal: lee jobs del channel y los procesa uno a uno.
    /// Se detiene limpiamente cuando <paramref name="stoppingToken"/> es cancelado.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MissedCallWorker] Iniciado. Esperando jobs de flow_00.");

        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            // Cada job en su propio try/catch: un fallo no detiene el worker.
            try
            {
                using var _ = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["CallSid"]       = job.CallSid,
                    ["TenantId"]      = job.TenantId,
                    ["CorrelationId"] = job.CorrelationId
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
                // Continuar con el siguiente job.
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

        // Inicializar el TenantContext para que el interceptor RLS funcione.
        // En el worker usamos rol "service" (no hay usuario humano).
        var tenantCtx = sp.GetRequiredService<TenantContext>();
        tenantCtx.Initialize(job.TenantId, TenantRole.Service, userId: null);

        var run = new AutomationRun
        {
            TenantId     = job.TenantId,
            FlowId       = "flow_00",
            TriggerType  = "event",
            TriggerRef   = job.CallSid,
            Status       = "running",
            CorrelationId = Guid.TryParse(job.CorrelationId, out var corrId)
                                ? corrId
                                : Guid.NewGuid()
        };
        db.AutomationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── Paso 1: buscar/crear paciente por teléfono ────────────────────
            var patient = await ResolveOrCreatePatientAsync(
                db, job.TenantId, job.CallerPhone, ct);

            // ── Paso 2: verificar consentimiento RGPD ─────────────────────────
            if (!patient.RgpdConsent)
            {
                _logger.LogInformation(
                    "[MissedCallWorker] Paciente sin consentimiento RGPD. " +
                    "PatientId={PatientId} TenantId={TenantId}. " +
                    "Se omite el envío de WhatsApp.",
                    patient.Id, job.TenantId);

                await CompleteRunAsync(db, run, "skipped",
                    "Paciente sin consentimiento RGPD", ct);
                return;
            }

            // ── Paso 3: [STUB] Envío de WhatsApp flow_00 ──────────────────────
            // TODO: implementar en la feature de mensajería.
            // Aquí se llamaría a IWhatsAppSender.SendMissedCallFlowAsync(...)
            // que construiría el mensaje de plantilla aprobada y lo enviaría
            // a través del HttpClient de Twilio con resiliencia.
            _logger.LogInformation(
                "[MissedCallWorker] [STUB] Envío WhatsApp flow_00 pendiente de implementar. " +
                "PatientId={PatientId} Phone={Phone} TenantId={TenantId}",
                patient.Id, job.CallerPhone, job.TenantId);

            // ── Actualizar run como completado ─────────────────────────────────
            run.ItemsProcessed = 1;
            run.ItemsSucceeded = 1;
            await CompleteRunAsync(db, run, "completed", errorMessage: null, ct);

            _logger.LogInformation(
                "[MissedCallWorker] flow_00 completado. " +
                "PatientId={PatientId} CallSid={CallSid}",
                patient.Id, job.CallSid);
        }
        catch (Exception ex)
        {
            run.ItemsFailed = 1;
            await CompleteRunAsync(db, run, "failed", ex.Message, ct);
            throw; // re-throw para que el loop lo logueé con el contexto completo
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Busca el paciente por teléfono dentro del tenant o lo crea como nuevo.
    /// </summary>
    private static async Task<Patient> ResolveOrCreatePatientAsync(
        AppDbContext      db,
        Guid              tenantId,
        string            callerPhone,
        CancellationToken ct)
    {
        var existing = await db.Patients
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId &&
                     p.Phone    == callerPhone &&
                     p.Status   != PatientStatus.Blocked,
                ct);

        if (existing is not null)
            return existing;

        // Crear paciente nuevo (sin nombre conocido aún)
        var newPatient = new Patient
        {
            TenantId    = tenantId,
            FullName    = $"Nuevo paciente ({callerPhone})",
            Phone       = callerPhone,
            Status      = PatientStatus.Active,
            RgpdConsent = false   // se pide consentimiento en el flujo de WhatsApp
        };
        db.Patients.Add(newPatient);
        await db.SaveChangesAsync(ct);

        return newPatient;
    }

    /// <summary>
    /// Marca el AutomationRun como finalizado con el estado dado.
    /// </summary>
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
