using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Domain.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Conversations;
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

            // ── Registrar evento reply en funnel de variante ──────────────────
            // Buscar el último mensaje outbound con variant_id para esta conversación
            // y registrar que el paciente respondió (reply = intención de conversión).
            var lastOutboundWithVariant = await db.Messages
                .Where(m =>
                    m.TenantId       == job.TenantId      &&
                    m.ConversationId == conversation.Id   &&
                    m.Direction      == "outbound"        &&
                    m.MessageVariantId != null)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (lastOutboundWithVariant?.MessageVariantId is not null)
            {
                var variantTracking = sp.GetRequiredService<IVariantTrackingService>();
                var sentAt    = lastOutboundWithVariant.SentAt ?? lastOutboundWithVariant.CreatedAt;
                var elapsedMs = (long)(DateTimeOffset.UtcNow - sentAt).TotalMilliseconds;

                await variantTracking.RecordEventAsync(new VariantConversionEvent
                {
                    TenantId         = job.TenantId,
                    MessageVariantId = lastOutboundWithVariant.MessageVariantId.Value,
                    MessageId        = lastOutboundWithVariant.Id,
                    ConversationId   = conversation.Id,
                    ProviderMessageId = job.MessageSid,
                    EventType        = VariantEventType.Reply,
                    ElapsedMs        = elapsedMs,
                    CorrelationId    = job.CorrelationId,
                    Metadata         = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        inbound_message_sid = job.MessageSid,
                        channel             = "whatsapp",
                        flow_id             = conversation.FlowId,
                    }),
                }, ct);

                _logger.LogDebug(
                    "[WAWorker] Evento reply registrado para variante. " +
                    "VariantId={VarId} OutboundMsgId={OutId} ConvId={ConvId}",
                    lastOutboundWithVariant.MessageVariantId.Value,
                    lastOutboundWithVariant.Id, conversation.Id);
            }

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

            // ── 7. Enrutar al agente conversacional ───────────────────────────
            var agent = sp.GetRequiredService<IConversationalAgent>();

            // P0: filtro TenantId obligatorio para garantizar aislamiento multi-tenant (ADR-001)
            var recentMessages = await db.Messages
                .Where(m =>
                    m.TenantId       == job.TenantId    &&
                    m.ConversationId == conversation.Id)
                .OrderByDescending(m => m.CreatedAt)
                .Take(15)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(ct);

            var discountRule = await db.RuleConfigs
                .Where(r => r.TenantId == job.TenantId &&
                             r.FlowId  == "global"      &&
                             r.RuleKey == "discount_max_pct" &&
                             r.IsActive)
                .FirstOrDefaultAsync(ct);
            var discountMaxPct = discountRule is not null
                && decimal.TryParse(discountRule.RuleValue,
                       System.Globalization.NumberStyles.Number,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var d) ? d : 0m;

            var tenantEntity = await db.Tenants.FindAsync([job.TenantId], ct);

            var agentCtx = new AgentContext
            {
                TenantId              = job.TenantId,
                PatientId             = patient.Id,
                ConversationId        = conversation.Id,
                CorrelationId         = job.CorrelationId,
                MessageSid            = job.MessageSid,
                InboundText           = job.Body ?? string.Empty,
                MediaUrl              = job.MediaUrl,
                PatientName           = patient.FullName,
                PatientPhone          = patient.Phone,
                RgpdConsent           = patient.RgpdConsent,
                ConversationStatus    = conversation.Status,
                AiContextJson         = conversation.AiContext,
                IsInsideSessionWindow = conversation.SessionExpiresAt.HasValue &&
                                        conversation.SessionExpiresAt.Value > DateTimeOffset.UtcNow,
                RecentMessages        = recentMessages,
                DiscountMaxPct        = discountMaxPct,
                ClinicName            = tenantEntity?.Name ?? "la clínica",
                LanguageCode          = "es",
            };

            var agentResult = await agent.HandleAsync(agentCtx, ct);

            // Actualizar AiContext y estado de conversación
            conversation.AiContext = agentResult.UpdatedAiContextJson;
            if (agentResult.Action == AgentAction.EscalateToHuman)
                conversation.Status = "waiting_human";
            else if (agentResult.Action == AgentAction.Resolve)
                conversation.Status = "resolved";
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[WAWorker] Agente completado. Action={Action} WasBlocked={Blocked} " +
                "ConvId={ConvId} PatientId={PatientId} MessageSid={Sid}",
                agentResult.Action, agentResult.WasBlocked,
                conversation.Id, patient.Id, job.MessageSid);

            // ── Enviar respuesta del agente si Action == SendMessage ──────────
            if (agentResult.Action == AgentAction.SendMessage &&
                !string.IsNullOrWhiteSpace(agentResult.ResponseText))
            {
                var sender = sp.GetRequiredService<ClinicBoost.Api.Features.Flow01.IOutboundMessageSender>();

                // Obtener el número de la clínica desde la entidad Tenant ya cargada
                var clinicPhone = tenantEntity?.WhatsAppNumber;

                if (!string.IsNullOrEmpty(clinicPhone))
                {
                    var sendReq = new ClinicBoost.Api.Features.Flow01.OutboundMessageRequest
                    {
                        ToPhone        = $"whatsapp:{job.CallerPhone}",
                        FromPhone      = $"whatsapp:{clinicPhone}",
                        Channel        = "whatsapp",
                        Body           = agentResult.ResponseText,
                        FlowId         = "flow_00",
                        TenantId       = job.TenantId,
                        PatientId      = patient.Id,
                        ConversationId = conversation.Id,
                        CorrelationId  = job.CorrelationId,
                    };

                    var sendResult = await sender.SendAsync(sendReq, ct);

                    if (sendResult.IsSuccess)
                    {
                        _logger.LogInformation(
                            "[WAWorker] Respuesta del agente enviada. " +
                            "TwilioSid={Sid} ConvId={ConvId} PatientId={PatientId}",
                            sendResult.TwilioSid, conversation.Id, patient.Id);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[WAWorker] Fallo al enviar respuesta del agente. " +
                            "ErrorCode={Code} ConvId={ConvId}",
                            sendResult.ErrorCode, conversation.Id);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[WAWorker] No se encontró WhatsAppNumber para el tenant. " +
                        "TenantId={TenantId}. Respuesta del agente no enviada.",
                        job.TenantId);
                }
            }

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
