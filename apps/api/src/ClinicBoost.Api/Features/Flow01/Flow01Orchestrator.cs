using System.Text.Json;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Revenue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeZoneConverter;

namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// Flow01Orchestrator
//
// Orquesta el flujo end-to-end:
//   Llamada perdida → Mensaje WhatsApp de recovery → Reserva conversacional
//
// SECUENCIA
// ─────────
//  1. Verificar idempotencia (skip si ya procesado con mismo CallSid).
//  2. Resolver o crear paciente por número de teléfono.
//  3. Verificar consentimiento RGPD → skipped si no hay consentimiento.
//  4. Construir OutboundMessageRequest con plantilla aprobada flow_01.
//  5. Enviar WhatsApp via IOutboundMessageSender.
//  6. Registrar métricas:
//     · missed_call_received  → siempre
//     · outbound_sent         → si el envío tiene éxito
//     · outbound_failed       → si Twilio falla
//  7. Si la reserva ocurrió en la misma sesión (AppointmentId en conversación):
//     · Registrar appointment_booked + revenue telemetría (solo en backend).
//
// GARANTÍAS
// ─────────
//  · Idempotente: IIdempotencyService previene doble procesamiento del mismo CallSid.
//  · Sin lógica económica en prompts ni frontend: toda la telemetría de revenue
//    se calcula y persiste en este orquestador.
//  · Timezone: nunca AddHours hardcoded; TZConvert.GetTimeZoneInfo del tenant.
//  · Errores de Twilio: no lanzan excepción; IsSuccess=false en el resultado.
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext, IOutboundMessageSender, IFlowMetricsService).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Orquestador del flujo flow_01: llamada perdida → WA saliente → reserva.
/// </summary>
public sealed class Flow01Orchestrator
{
    // Nombre de la Content Template aprobada en Twilio para flow_01
    // Configurable desde ICalOptions; aquí como constante por defecto.
    private const string DefaultTemplateSid = "HXmissed_call_recovery_v1";

    private readonly AppDbContext                _db;
    private readonly IOutboundMessageSender      _sender;
    private readonly IFlowMetricsService         _metrics;
    private readonly IIdempotencyService         _idempotency;
    private readonly IVariantTrackingService     _variantTracking;
    private readonly Flow01Options               _opts;
    private readonly ILogger<Flow01Orchestrator> _logger;

    public Flow01Orchestrator(
        AppDbContext                db,
        IOutboundMessageSender      sender,
        IFlowMetricsService         metrics,
        IIdempotencyService         idempotency,
        IVariantTrackingService     variantTracking,
        IOptions<Flow01Options>     opts,
        ILogger<Flow01Orchestrator> logger)
    {
        _db              = db;
        _sender          = sender;
        _metrics         = metrics;
        _idempotency     = idempotency;
        _variantTracking = variantTracking;
        _opts            = opts.Value;
        _logger          = logger;
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ejecuta el flujo flow_01 para una llamada perdida.
    /// </summary>
    /// <param name="tenantId">Tenant al que pertenece la llamada.</param>
    /// <param name="callSid">SID de la llamada en Twilio (idempotencia).</param>
    /// <param name="callerPhone">Número del paciente en formato E.164.</param>
    /// <param name="clinicPhone">Número de la clínica (para el From del WA).</param>
    /// <param name="callReceivedAt">Timestamp UTC en que llegó la llamada perdida.</param>
    /// <param name="correlationId">ID de correlación end-to-end.</param>
    /// <param name="ct">Token de cancelación.</param>
    public async Task<Flow01Result> ExecuteAsync(
        Guid              tenantId,
        string            callSid,
        string            callerPhone,
        string            clinicPhone,
        DateTimeOffset    callReceivedAt,
        string            correlationId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Flow01] Iniciando. CallSid={CallSid} Caller={Caller} TenantId={TenantId}",
            callSid, callerPhone, tenantId);

        // ── 1. Idempotencia ───────────────────────────────────────────────────
        var idempResult = await _idempotency.TryProcessAsync(
            eventType: "flow_01.missed_call",
            eventId:   callSid,
            tenantId:  tenantId,
            payload:   new { callSid, callerPhone, clinicPhone },
            ct:        ct);

        if (!idempResult.ShouldProcess)
        {
            _logger.LogInformation(
                "[Flow01] Evento ya procesado. CallSid={CallSid} " +
                "FirstProcessedAt={First}",
                callSid, idempResult.FirstProcessedAt);

            // Devolvemos success sin re-procesar
            return Flow01Result.Skipped(Guid.Empty, "Evento ya procesado (idempotencia).");
        }

        // ── 1b. GAP-03: Ventana máxima de procesamiento (MaxDelayMinutes) ────────
        // Si el job llegó con demasiado retraso (p. ej. la cola estaba bloqueada),
        // enviar el WA ahora sería confuso para el paciente.
        // Se omite el envío y se registra la métrica con motivo "max_delay_exceeded".
        var elapsedSinceCall = DateTimeOffset.UtcNow - callReceivedAt;
        if (elapsedSinceCall.TotalMinutes > _opts.MaxDelayMinutes)
        {
            _logger.LogWarning(
                "[Flow01] Ventana máxima superada. CallSid={CallSid} " +
                "Elapsed={Elapsed:F1} min MaxDelay={Max} min TenantId={TenantId}. " +
                "Se omite el envío.",
                callSid, elapsedSinceCall.TotalMinutes, _opts.MaxDelayMinutes, tenantId);

            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId      = tenantId,
                FlowId        = "flow_01",
                MetricType    = "flow_skipped",
                CorrelationId = correlationId,
                Metadata      = JsonSerializer.Serialize(new
                {
                    reason             = "max_delay_exceeded",
                    elapsed_minutes    = Math.Round(elapsedSinceCall.TotalMinutes, 1),
                    max_delay_minutes  = _opts.MaxDelayMinutes,
                    call_sid           = callSid,
                }),
            }, ct);

            return Flow01Result.Skipped(Guid.Empty,
                $"Ventana máxima superada: {elapsedSinceCall.TotalMinutes:F1} min > {_opts.MaxDelayMinutes} min permitidos.");
        }

        // ── 2. Registrar métrica: llamada perdida recibida ────────────────────
        await _metrics.RecordAsync(new FlowMetricsEvent
        {
            TenantId      = tenantId,
            FlowId        = "flow_01",
            MetricType    = "missed_call_received",
            CorrelationId = correlationId,
            Metadata      = JsonSerializer.Serialize(new
            {
                call_sid    = callSid,
                caller_phone = callerPhone,
                clinic_phone = clinicPhone,
            }),
        }, ct);

        // ── 3. Resolver o crear paciente ──────────────────────────────────────
        var patient = await ResolveOrCreatePatientAsync(
            tenantId, callerPhone, ct);

        // ── 3b. Cooldown: evitar spam si ya recibió un WA reciente ────────────
        // P1: si existe un outbound de flow_01 enviado en los últimos N minutos
        // al mismo paciente, se omite el envío para prevenir spam y cumplir RGPD.
        // N-P0-01 fix: el cutoff se calcula desde UtcNow, no desde callReceivedAt.
        // callReceivedAt es el instante de la llamada (en el pasado); restar minutos
        // sobre él producía una ventana anclada al pasado que nunca se activaba.
        // N-P1-05: leer cooldown_minutes de RuleConfig del tenant para flexibilidad multi-tenant.
        var cooldownRule = await _db.RuleConfigs.AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "flow_01" &&
                r.RuleKey  == "cooldown_minutes" &&
                r.IsActive, ct);
        int cooldownMinutes = cooldownRule is not null
            && int.TryParse(cooldownRule.RuleValue,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var cm)
            && cm > 0 ? cm : 60;

        var cooldownCutoff = DateTimeOffset.UtcNow.AddMinutes(-cooldownMinutes);

        // JOIN Message → Conversation para filtrar por FlowId (Message no tiene FlowId propio)
        var recentOutbound = await (
            from m in _db.Messages
            join c in _db.Conversations on m.ConversationId equals c.Id
            where m.TenantId  == tenantId       &&
                  c.TenantId  == tenantId        &&
                  c.PatientId == patient.Id      &&
                  c.FlowId    == "flow_01"       &&
                  m.Direction == "outbound"      &&
                  m.CreatedAt >= cooldownCutoff  &&
                  m.Status    != "failed"
            select m.Id
        ).AnyAsync(ct);

        if (recentOutbound)
        {
            _logger.LogInformation(
                "[Flow01] Cooldown activo: outbound reciente encontrado. " +
                "PatientId={PatientId} CooldownMin={Min} TenantId={TenantId}. " +
                "Se omite el envío para evitar spam.",
                patient.Id, cooldownMinutes, tenantId);

            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId      = tenantId,
                PatientId     = patient.Id,
                FlowId        = "flow_01",
                MetricType    = "flow_skipped",
                CorrelationId = correlationId,
                Metadata      = JsonSerializer.Serialize(new
                {
                    reason           = "cooldown_active",
                    cooldown_minutes = cooldownMinutes,
                }),
            }, ct);

            return Flow01Result.Skipped(patient.Id,
                $"Cooldown activo: mensaje reciente enviado en los últimos {cooldownMinutes} min.");
        }

        // ── 4. Verificar consentimiento RGPD ──────────────────────────────────
        if (!patient.RgpdConsent)
        {
            _logger.LogInformation(
                "[Flow01] Sin consentimiento RGPD. PatientId={PatientId} TenantId={TenantId}",
                patient.Id, tenantId);

            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId      = tenantId,
                PatientId     = patient.Id,
                FlowId        = "flow_01",
                MetricType    = "flow_skipped",
                CorrelationId = correlationId,
                Metadata      = JsonSerializer.Serialize(new { reason = "no_rgpd_consent" }),
            }, ct);

            return Flow01Result.Skipped(patient.Id, "Paciente sin consentimiento RGPD.");
        }

        // ── 5. Obtener plantilla de la clínica ────────────────────────────────
        var (templateSid, templateVars) = await BuildTemplateAsync(
            tenantId, callerPhone, patient.FullName, ct);

        // ── 5b. Seleccionar variante A/B activa ───────────────────────────────
        Guid? selectedVariantId = null;
        if (!string.IsNullOrEmpty(templateSid))
        {
            var variant = await _variantTracking.SelectVariantAsync(
                tenantId, "flow_01", templateSid, ct);

            if (variant is not null)
            {
                selectedVariantId = variant.Id;
                // Usar TemplateVars de la variante si las tiene definidas
                if (!string.IsNullOrEmpty(variant.TemplateVars))
                    templateVars = variant.TemplateVars;

                _logger.LogDebug(
                    "[Flow01] Variante A/B seleccionada: {Key} (VariantId={VarId}) TenantId={TenantId}",
                    variant.VariantKey, variant.Id, tenantId);
            }
        }

        // ── 6. Enviar WhatsApp de recovery ────────────────────────────────────
        var sendRequest = new OutboundMessageRequest
        {
            ToPhone          = $"whatsapp:{callerPhone}",
            FromPhone        = $"whatsapp:{clinicPhone}",
            Channel          = "whatsapp",
            TemplateSid      = templateSid,
            TemplateVars     = templateVars,
            Body             = string.IsNullOrEmpty(templateSid)
                                   ? BuildFallbackBody(patient.FullName)
                                   : null,
            FlowId           = "flow_01",
            TenantId         = tenantId,
            PatientId        = patient.Id,
            CorrelationId    = correlationId,
            MessageVariantId = selectedVariantId,
        };

        var now = DateTimeOffset.UtcNow;
        var sendResult = await _sender.SendAsync(sendRequest, ct);
        var responseTimeMs = (long)(now - callReceivedAt).TotalMilliseconds;

        if (sendResult.IsSuccess)
        {
            // ── 7. Métrica: outbound_sent con tiempo de respuesta ─────────────
            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId        = tenantId,
                PatientId       = patient.Id,
                FlowId          = "flow_01",
                MetricType      = "outbound_sent",
                DurationMs      = responseTimeMs,
                TwilioMessageSid = sendResult.TwilioSid,
                CorrelationId   = correlationId,
                Metadata        = JsonSerializer.Serialize(new
                {
                    message_id  = sendResult.MessageId,
                    twilio_sid  = sendResult.TwilioSid,
                    channel     = "whatsapp",
                    template_sid = templateSid,
                }),
            }, ct);

            _logger.LogInformation(
                "[Flow01] WhatsApp enviado. PatientId={PatientId} TwilioSid={Sid} " +
                "ResponseTimeMs={Ms} TenantId={TenantId}",
                patient.Id, sendResult.TwilioSid, responseTimeMs, tenantId);

            return Flow01Result.Success(
                patient.Id,
                sendResult.MessageId,
                sendResult.TwilioSid,
                responseTimeMs);
        }
        else
        {
            // ── 8. Métrica: outbound_failed ───────────────────────────────────
            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId      = tenantId,
                PatientId     = patient.Id,
                FlowId        = "flow_01",
                MetricType    = "outbound_failed",
                DurationMs    = responseTimeMs,
                ErrorCode     = sendResult.ErrorCode,
                CorrelationId = correlationId,
                Metadata      = JsonSerializer.Serialize(new
                {
                    error_code    = sendResult.ErrorCode,
                    error_message = sendResult.ErrorMessage,
                    message_id    = sendResult.MessageId,
                }),
            }, ct);

            _logger.LogWarning(
                "[Flow01] Envío fallido. PatientId={PatientId} ErrorCode={Code} " +
                "Error={Error} TenantId={TenantId}",
                patient.Id, sendResult.ErrorCode, sendResult.ErrorMessage, tenantId);

            return Flow01Result.Failure(
                patient.Id, "outbound_send",
                $"[{sendResult.ErrorCode}] {sendResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Registra la conversión de una cita reservada a través de Flow01.
    /// Llamado desde WhatsAppInboundWorker cuando el agente confirma una reserva
    /// y la fuente de la cita es WhatsApp (Flow01).
    /// </summary>
    public async Task RecordAppointmentBookedAsync(
        Guid              tenantId,
        Guid              patientId,
        Guid              appointmentId,
        DateTimeOffset    outboundSentAt,
        decimal?          revenue,
        string            correlationId,
        Guid?             messageVariantId = null,
        Guid?             messageId        = null,
        Guid?             conversationId   = null,
        CancellationToken ct = default)
    {
        var durationMs = (long)(DateTimeOffset.UtcNow - outboundSentAt).TotalMilliseconds;

        await _metrics.RecordAsync(new FlowMetricsEvent
        {
            TenantId         = tenantId,
            PatientId        = patientId,
            AppointmentId    = appointmentId,
            FlowId           = "flow_01",
            MetricType       = "appointment_booked",
            DurationMs       = durationMs,
            RecoveredRevenue = revenue,
            CorrelationId    = correlationId,
            Metadata         = JsonSerializer.Serialize(new
            {
                appointment_id     = appointmentId,
                revenue_eur        = revenue,
                duration_ms        = durationMs,
                message_variant_id = messageVariantId,
            }),
        }, ct);

        // RevenueEvent (lógica económica SOLO en backend — never en prompts ni frontend)
        if (revenue.HasValue && revenue.Value > 0)
        {
            // N-P0-03 fix: leer success_fee_pct de RuleConfig del tenant en lugar
            // de usar el magic number 0.15m. Mismo patrón que AppointmentService.
            var successFeePct = await GetSuccessFeePctAsync(tenantId, ct);

            var revEvent = new RevenueEvent
            {
                TenantId             = tenantId,
                AppointmentId        = appointmentId,
                PatientId            = patientId,
                EventType            = "missed_call_converted",
                FlowId               = "flow_01",
                Amount               = revenue.Value,
                Currency             = "EUR",
                IsSuccessFeeEligible = true,
                SuccessFeeAmount     = Math.Round(revenue.Value * successFeePct, 2),
                AttributionData      = JsonSerializer.Serialize(new
                {
                    flow               = "flow_01",
                    channel            = "whatsapp",
                    source             = "missed_call",
                    correlation        = correlationId,
                    message_variant_id = messageVariantId,
                }),
            };
            _db.RevenueEvents.Add(revEvent);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[Flow01] Revenue registrado. AppointmentId={AptId} Amount={Amt}EUR " +
                "SuccessFee={Fee}EUR FeePct={Pct:P2} VariantId={VarId} TenantId={TenantId}",
                appointmentId, revenue.Value, revEvent.SuccessFeeAmount,
                successFeePct, messageVariantId, tenantId);
        }

        // ── Registrar evento booked en funnel de variante ─────────────────────
        if (messageVariantId.HasValue)
        {
            await _variantTracking.RecordEventAsync(new Domain.Variants.VariantConversionEvent
            {
                TenantId         = tenantId,
                MessageVariantId = messageVariantId.Value,
                MessageId        = messageId,
                ConversationId   = conversationId,
                AppointmentId    = appointmentId,
                EventType        = Domain.Variants.VariantEventType.Booked,
                ElapsedMs        = durationMs,
                RecoveredRevenue = revenue,
                CorrelationId    = correlationId,
                Metadata         = JsonSerializer.Serialize(new
                {
                    appointment_id = appointmentId,
                    revenue_eur    = revenue,
                    flow_id        = "flow_01",
                }),
            }, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Patient> ResolveOrCreatePatientAsync(
        Guid tenantId, string phone, CancellationToken ct)
    {
        var existing = await _db.Patients
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId &&
                     p.Phone    == phone     &&
                     p.Status   != PatientStatus.Blocked,
                ct);

        if (existing is not null)
            return existing;

        var newPatient = new Patient
        {
            TenantId    = tenantId,
            FullName    = $"Nuevo paciente ({phone})",
            Phone       = phone,
            Status      = PatientStatus.Active,
            RgpdConsent = false,
        };
        _db.Patients.Add(newPatient);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[Flow01] Nuevo paciente creado. PatientId={Id} Phone={Phone} TenantId={TenantId}",
            newPatient.Id, phone, tenantId);

        return newPatient;
    }

    private async Task<(string? TemplateSid, string? TemplateVars)> BuildTemplateAsync(
        Guid              tenantId,
        string            callerPhone,
        string            patientName,
        CancellationToken ct)
    {
        // Buscar SID de plantilla en RuleConfig del tenant
        var rule = await _db.RuleConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "flow_01" &&
                r.RuleKey  == "template_sid" &&
                r.IsActive,
                ct);

        var templateSid = rule?.RuleValue ?? _opts.DefaultTemplateSid;

        if (string.IsNullOrEmpty(templateSid))
            return (null, null); // fallback a texto libre

        // Variables de la plantilla (nombre del paciente para personalización)
        var vars = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["1"] = patientName.Split(' ')[0], // primer nombre
        });

        return (templateSid, vars);
    }

    private static string BuildFallbackBody(string patientName)
    {
        var firstName = patientName.Split(' ')[0];
        return $"Hola {firstName}, hemos visto que intentaste llamarnos y no pudimos atenderte. " +
               "¿En qué podemos ayudarte? Puedes escribirnos aquí y te atendemos en breve.";
    }

    /// <summary>
    /// Lee el porcentaje de success fee del tenant desde RuleConfig (global/success_fee_pct).
    /// Devuelve el valor como decimal [0,1]. Default: 0.15 si no está configurado.
    /// N-P0-03: mismo patrón que AppointmentService.GetSuccessFeePctAsync.
    /// </summary>
    private async Task<decimal> GetSuccessFeePctAsync(Guid tenantId, CancellationToken ct)
    {
        const decimal DefaultFee = 0.15m;

        var rule = await _db.RuleConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "global" &&
                r.RuleKey  == "success_fee_pct" &&
                r.IsActive, ct);

        if (rule is null)
            return DefaultFee;

        return decimal.TryParse(rule.RuleValue,
                   System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var pct) && pct > 0
            ? pct / 100m
            : DefaultFee;
    }
}

// ── Opciones de configuración de Flow01 ──────────────────────────────────────

/// <summary>
/// Opciones de configuración de Flow01, enlazadas a la sección "Flow01Options".
/// </summary>
public sealed class Flow01Options
{
    public const string SectionName = "Flow01Options";

    /// <summary>SID de la Content Template por defecto para el mensaje de recovery.</summary>
    public string? DefaultTemplateSid { get; set; }

    /// <summary>
    /// Ventana máxima de tiempo (minutos) desde la llamada hasta intentar el envío.
    /// Si el job se retrasa más de esta ventana, el envío se omite.
    /// Por defecto: 60 minutos.
    /// </summary>
    public int MaxDelayMinutes { get; set; } = 60;
}
