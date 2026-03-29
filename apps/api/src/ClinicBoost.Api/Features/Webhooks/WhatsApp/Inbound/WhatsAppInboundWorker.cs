using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Patients;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;

// ════════════════════════════════════════════════════════════════════════════
// WhatsAppInboundWorker
//
// BackgroundService que consume la cola IWhatsAppJobQueue y ejecuta el
// pipeline completo de recepción de mensajes WhatsApp fuera del ciclo HTTP.
//
// SECUENCIA DE PASOS POR JOB
// ──────────────────────────
// 1. Crear un DI scope (el DbContext es Scoped).
// 2. Inicializar TenantContext con rol "service" para que RLS funcione.
// 3. Registrar AutomationRun (flow_00, status=running) para observabilidad.
// 4. Resolver o crear el paciente por número de teléfono (CallerPhone E.164).
// 5. Upsert de la conversación activa (canal "whatsapp", flujo "flow_00").
// 6. Persistir el mensaje inbound con su MessageSid para la correlación
//    MessageSid ↔ Message.Id ↔ Conversation.Id ↔ TenantId.
// 7. Verificar consentimiento RGPD del paciente.
//    · Si RgpdConsent == false → AutomationRun = "skipped", no hay acción IA.
// 8. Marcar AutomationRun como completed.
// 9. [Stub] Enrutar al agente conversacional (IConversationalAgent).
//    → TODO: implementar en el sprint de integración IA.
//
// GARANTÍAS DE RESILIENCIA
// ─────────────────────────
// · Cada job en su propio scope → fallo de un job no afecta a otros.
// · CancellationToken del host: el worker se detiene limpiamente en shutdown.
// · Errores no propagados al loop: se loguean y el worker continúa.
// · El evento ya está en processed_events → Twilio no re-entregará el job.
//
// LO QUE NO HACE ESTE WORKER (deliberadamente)
// ──────────────────────────────────────────────
// · NO llama directamente a Twilio API (lo hará el agente IA o el sender).
// · NO confirma citas (la IA propone; el backend confirma en otro endpoint).
// · NO actualiza estado en Twilio.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Background service que ejecuta el pipeline de recepción de mensajes
/// WhatsApp inbound consumiendo jobs de <see cref="IWhatsAppJobQueue"/>.
/// </summary>
public sealed class WhatsAppInboundWorker : BackgroundService
{
    private readonly IWhatsAppJobQueue              _queue;
    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly ILogger<WhatsAppInboundWorker> _logger;

    public WhatsAppInboundWorker(
        IWhatsAppJobQueue              queue,
        IServiceScopeFactory           scopeFactory,
        ILogger<WhatsAppInboundWorker> logger)
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
        _logger.LogInformation(
            "[WAWorker] Iniciado. Esperando mensajes WhatsApp inbound.");

        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var _ = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["MessageSid"]    = job.MessageSid,
                    ["TenantId"]      = job.TenantId,
                    ["CorrelationId"] = job.CorrelationId
                });

                await ProcessJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[WAWorker] Shutdown solicitado. Deteniendo procesamiento.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WAWorker] Error inesperado procesando job. " +
                    "MessageSid={Sid} TenantId={TenantId}",
                    job.MessageSid, job.TenantId);
                // Continuar con el siguiente job.
            }
        }

        _logger.LogInformation("[WAWorker] Detenido.");
    }

    // ── Lógica de procesamiento ───────────────────────────────────────────────

    private async Task ProcessJobAsync(WhatsAppInboundJob job, CancellationToken ct)
    {
        _logger.LogInformation(
            "[WAWorker] Procesando mensaje inbound. " +
            "MessageSid={Sid} Caller={Caller} TenantId={TenantId}",
            job.MessageSid, job.CallerPhone, job.TenantId);

        // ── 1. Scope con tenant inicializado para RLS ─────────────────────────
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp  = scope.ServiceProvider;
        var db  = sp.GetRequiredService<AppDbContext>();

        // El worker no tiene usuario humano → rol "service"
        var tenantCtx = sp.GetRequiredService<TenantContext>();
        tenantCtx.Initialize(job.TenantId, TenantRole.Service, userId: null);

        // ── 2. Registrar AutomationRun ────────────────────────────────────────
        var run = new AutomationRun
        {
            TenantId      = job.TenantId,
            FlowId        = "flow_00",
            TriggerType   = "event",
            TriggerRef    = job.MessageSid,
            Status        = "running",
            CorrelationId = Guid.TryParse(job.CorrelationId, out var corrId)
                                ? corrId
                                : Guid.NewGuid()
        };
        db.AutomationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            // ── 3. Resolver o crear paciente ──────────────────────────────────
            var patient = await ResolveOrCreatePatientAsync(
                db, job.TenantId, job.CallerPhone, job.ProfileName, ct);

            // ── 4. Upsert conversación activa ─────────────────────────────────
            var conversationService = sp.GetRequiredService<IConversationService>();
            var conversation = await conversationService.UpsertConversationAsync(
                tenantId:  job.TenantId,
                patientId: patient.Id,
                channel:   "whatsapp",
                flowId:    "flow_00",
                ct:        ct);

            // ── 5. Persistir mensaje inbound con MessageSid ───────────────────
            // Correlación garantizada: MessageSid → Message.Id → Conversation.Id → TenantId
            var message = await conversationService.AppendInboundMessageAsync(
                conversationId: conversation.Id,
                tenantId:       job.TenantId,
                messageSid:     job.MessageSid,
                body:           job.Body,
                mediaUrl:       job.MediaUrl,
                mediaType:      job.MediaType,
                ct:             ct);

            _logger.LogInformation(
                "[WAWorker] Mensaje persistido. " +
                "MessageId={MsgId} MessageSid={Sid} " +
                "ConvId={ConvId} PatientId={PatientId}",
                message.Id, job.MessageSid, conversation.Id, patient.Id);

            // ── 6. Verificar consentimiento RGPD ──────────────────────────────
            if (!patient.RgpdConsent)
            {
                _logger.LogInformation(
                    "[WAWorker] Paciente sin consentimiento RGPD. " +
                    "PatientId={PatientId} TenantId={TenantId}. " +
                    "Se omite el enrutado al agente IA.",
                    patient.Id, job.TenantId);

                await CompleteRunAsync(db, run, "skipped",
                    "Paciente sin consentimiento RGPD", ct);
                return;
            }

            // ── 7. [Stub] Enrutar al agente conversacional ────────────────────
            // TODO: implementar en el sprint de integración IA.
            // Aquí se invocaría a IConversationalAgent.HandleInboundAsync(...)
            // que leerá el AiContext de la conversation, llamará a OpenAI/Claude
            // y encolaría la respuesta para envío por Twilio.
            _logger.LogInformation(
                "[WAWorker] [STUB] Enrutado al agente IA pendiente de implementar. " +
                "ConvId={ConvId} PatientId={PatientId} " +
                "MessageSid={Sid} TenantId={TenantId}",
                conversation.Id, patient.Id, job.MessageSid, job.TenantId);

            // ── 8. Completar AutomationRun ────────────────────────────────────
            run.ItemsProcessed = 1;
            run.ItemsSucceeded = 1;
            await CompleteRunAsync(db, run, "completed", errorMessage: null, ct);

            _logger.LogInformation(
                "[WAWorker] Pipeline flow_00 completado. " +
                "MessageSid={Sid} ConvId={ConvId}",
                job.MessageSid, conversation.Id);
        }
        catch (Exception ex)
        {
            run.ItemsFailed = 1;
            await CompleteRunAsync(db, run, "failed", ex.Message, ct);
            throw;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Busca el paciente activo por teléfono dentro del tenant o lo crea.
    /// Si se crea, usa el <paramref name="profileName"/> de WhatsApp como
    /// nombre provisional hasta que el flujo complete la recopilación de datos.
    /// </summary>
    private static async Task<Patient> ResolveOrCreatePatientAsync(
        AppDbContext      db,
        Guid              tenantId,
        string            callerPhone,
        string            profileName,
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

        // Usar el nombre de perfil de WA si está disponible
        var displayName = !string.IsNullOrWhiteSpace(profileName)
            ? profileName
            : $"Nuevo paciente ({callerPhone})";

        var newPatient = new Patient
        {
            TenantId    = tenantId,
            FullName    = displayName,
            Phone       = callerPhone,
            Status      = PatientStatus.Active,
            RgpdConsent = false   // se pide consentimiento en el flujo WA
        };

        db.Patients.Add(newPatient);
        await db.SaveChangesAsync(ct);

        return newPatient;
    }

    /// <summary>Marca el AutomationRun con el estado final.</summary>
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
