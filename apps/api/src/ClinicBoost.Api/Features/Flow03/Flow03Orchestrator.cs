using System.Text.Json;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Appointments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TimeZoneConverter;

namespace ClinicBoost.Api.Features.Flow03;

// ════════════════════════════════════════════════════════════════════════════
// Flow03Orchestrator
//
// Orquesta el flujo end-to-end de recordatorios de cita:
//   Cita próxima detectada → Mensaje WhatsApp de recordatorio
//
// SECUENCIA
// ─────────
//  1. Cargar cita y verificar pertenencia al tenant + estado Scheduled.
//  2. Guard de dominio: ReminderSentAt ya establecido → skip (idempotencia fuerte).
//  3. Idempotency service: eventId = appointmentId (segunda barrera).
//  4. Cargar tenant (TimeZone + WhatsAppNumber).
//  5. Check de ventana por tenant: reminder_hours_before desde RuleConfig.
//     Si aún es pronto, skip con razón "not_yet_in_window".
//  6. Cargar paciente + RGPD consent.
//  7. Cooldown: evitar spam si ya hay un outbound flow_03 reciente.
//  8. Construir OutboundMessageRequest (plantilla + variables localizadas).
//  9. Enviar via IOutboundMessageSender (IConversationService crea la conversación).
// 10. On success: appointment.ReminderSentAt = UtcNow. SaveChangesAsync.
// 11. Métricas: reminder_sent | reminder_failed | flow_skipped.
//
// GARANTÍAS
// ─────────
//  · Doble idempotencia: ReminderSentAt (dominio) + IIdempotencyService.
//  · Timezone: nunca AddHours hardcoded; TZConvert.GetTimeZoneInfo del tenant.
//  · Sin lógica económica directa (los bookings de flow_03 se atribuyen
//    cuando el agente confirma la cita desde la conversación).
//  · Errores de Twilio: no lanzan excepción; IsSuccess=false en el resultado.
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext, IOutboundMessageSender, IFlowMetricsService).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Orquestador de flow_03: recordatorio de cita próxima vía WhatsApp.
/// </summary>
public sealed class Flow03Orchestrator
{
    private readonly AppDbContext                _db;
    private readonly IOutboundMessageSender      _sender;
    private readonly IFlowMetricsService         _metrics;
    private readonly IIdempotencyService         _idempotency;
    private readonly Flow03Options               _opts;
    private readonly ILogger<Flow03Orchestrator> _logger;

    public Flow03Orchestrator(
        AppDbContext                db,
        IOutboundMessageSender      sender,
        IFlowMetricsService         metrics,
        IIdempotencyService         idempotency,
        IOptions<Flow03Options>     opts,
        ILogger<Flow03Orchestrator> logger)
    {
        _db          = db;
        _sender      = sender;
        _metrics     = metrics;
        _idempotency = idempotency;
        _opts        = opts.Value;
        _logger      = logger;
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ejecuta el flujo flow_03 para una cita próxima.
    /// </summary>
    /// <param name="tenantId">Tenant propietario de la cita.</param>
    /// <param name="appointmentId">ID de la cita a recordar.</param>
    /// <param name="correlationId">ID de correlación end-to-end.</param>
    public async Task<Flow03Result> ExecuteAsync(
        Guid              tenantId,
        Guid              appointmentId,
        string            correlationId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Flow03] Iniciando. AppointmentId={AptId} TenantId={TenantId}",
            appointmentId, tenantId);

        // ── 1. Cargar cita con tracking (actualizaremos ReminderSentAt) ───────
        var appointment = await _db.Appointments
            .Where(a => a.TenantId == tenantId && a.Id == appointmentId)
            .FirstOrDefaultAsync(ct);

        if (appointment is null)
        {
            _logger.LogWarning(
                "[Flow03] Cita no encontrada. AppointmentId={AptId} TenantId={TenantId}",
                appointmentId, tenantId);

            return Flow03Result.Skipped(appointmentId, Guid.Empty, "appointment_not_found");
        }

        if (appointment.Status != AppointmentStatus.Scheduled)
        {
            _logger.LogInformation(
                "[Flow03] Cita no está Scheduled (status={Status}). Skip. AppointmentId={AptId}",
                appointment.Status, appointmentId);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId,
                $"appointment_status_{appointment.Status.ToString().ToLowerInvariant()}");
        }

        // ── 2. Guard de dominio: ReminderSentAt (barrera fuerte) ─────────────
        if (appointment.ReminderSentAt.HasValue)
        {
            _logger.LogInformation(
                "[Flow03] Recordatorio ya enviado en {SentAt}. Skip. AppointmentId={AptId}",
                appointment.ReminderSentAt, appointmentId);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId, "already_reminded");
        }

        // ── 3. Idempotency service (segunda barrera) ──────────────────────────
        var idempResult = await _idempotency.TryProcessAsync(
            eventType: "flow_03.appointment_reminder",
            eventId:   appointmentId.ToString(),
            tenantId:  tenantId,
            payload:   new { appointmentId, correlationId },
            ct:        ct);

        if (!idempResult.ShouldProcess)
        {
            _logger.LogInformation(
                "[Flow03] Evento ya procesado (idempotencia). AppointmentId={AptId}",
                appointmentId);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId,
                "Evento ya procesado (idempotencia).");
        }

        // ── 4. Cargar tenant ──────────────────────────────────────────────────
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);

        if (tenant is null || !tenant.IsActive)
        {
            _logger.LogWarning(
                "[Flow03] Tenant inactivo o no encontrado. TenantId={TenantId}", tenantId);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId, "tenant_not_active");
        }

        // ── 5. Verificar ventana de recordatorio por tenant ───────────────────
        var hoursBeforeReminder = await GetReminderHoursAsync(tenantId, ct);
        var now                 = DateTimeOffset.UtcNow;
        var reminderTarget      = appointment.StartsAtUtc - TimeSpan.FromHours(hoursBeforeReminder);
        var tolerance           = TimeSpan.FromMinutes(_opts.PollIntervalMinutes * 2 + 5);

        if (reminderTarget > now + tolerance)
        {
            // Aún no es el momento para este tenant — el worker volverá pronto
            _logger.LogDebug(
                "[Flow03] Todavía pronto para el recordatorio. AppointmentId={AptId} " +
                "ReminderTarget={Target} Now={Now} HoursBefore={H}",
                appointmentId, reminderTarget, now, hoursBeforeReminder);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId, "not_yet_in_window");
        }

        if (appointment.StartsAtUtc < now)
        {
            // Cita ya comenzó — demasiado tarde para recordatorio
            _logger.LogInformation(
                "[Flow03] Cita ya comenzó. No se envía recordatorio. AppointmentId={AptId}",
                appointmentId);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId, "appointment_already_started");
        }

        // ── 6. Cargar paciente + RGPD ─────────────────────────────────────────
        var patient = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Id == appointment.PatientId)
            .FirstOrDefaultAsync(ct);

        if (patient is null)
        {
            _logger.LogWarning(
                "[Flow03] Paciente no encontrado. PatientId={PtId} AppointmentId={AptId}",
                appointment.PatientId, appointmentId);

            await RecordSkipMetricAsync(tenantId, appointment.PatientId, appointmentId,
                correlationId, "patient_not_found", ct);

            return Flow03Result.Skipped(appointmentId, appointment.PatientId, "patient_not_found");
        }

        if (!patient.RgpdConsent)
        {
            _logger.LogInformation(
                "[Flow03] Sin consentimiento RGPD. PatientId={PtId} AppointmentId={AptId}",
                patient.Id, appointmentId);

            await RecordSkipMetricAsync(tenantId, patient.Id, appointmentId,
                correlationId, "no_rgpd_consent", ct);

            return Flow03Result.Skipped(appointmentId, patient.Id, "no_rgpd_consent");
        }

        // ── 7. Cooldown: evitar spam ──────────────────────────────────────────
        var cooldownMinutes = await GetCooldownMinutesAsync(tenantId, ct);
        var cooldownCutoff  = now.AddMinutes(-cooldownMinutes);

        var recentOutbound = await (
            from m in _db.Messages
            join c in _db.Conversations on m.ConversationId equals c.Id
            where m.TenantId  == tenantId       &&
                  c.TenantId  == tenantId        &&
                  c.PatientId == patient.Id      &&
                  c.FlowId    == "flow_03"       &&
                  m.Direction == "outbound"      &&
                  m.CreatedAt >= cooldownCutoff  &&
                  m.Status    != "failed"
            select m.Id
        ).AnyAsync(ct);

        if (recentOutbound)
        {
            _logger.LogInformation(
                "[Flow03] Cooldown activo. PatientId={PtId} CooldownMin={Min}",
                patient.Id, cooldownMinutes);

            await RecordSkipMetricAsync(tenantId, patient.Id, appointmentId,
                correlationId, "cooldown_active", ct);

            return Flow03Result.Skipped(appointmentId, patient.Id, "cooldown_active");
        }

        // ── 8. Construir mensaje ──────────────────────────────────────────────
        var templateSid = await GetTemplateSidAsync(tenantId, ct);
        var (resolvedTemplateSid, templateVars, fallbackBody) =
            BuildTemplate(tenant, patient.FullName, appointment, templateSid);

        // ── 9. Enviar WhatsApp de recordatorio ────────────────────────────────
        var sendRequest = new OutboundMessageRequest
        {
            ToPhone       = $"whatsapp:{patient.Phone}",
            FromPhone     = $"whatsapp:{tenant.WhatsAppNumber}",
            Channel       = "whatsapp",
            TemplateSid   = resolvedTemplateSid,
            TemplateVars  = templateVars,
            Body          = string.IsNullOrEmpty(resolvedTemplateSid) ? fallbackBody : null,
            FlowId        = "flow_03",
            TenantId      = tenantId,
            PatientId     = patient.Id,
            CorrelationId = correlationId,
        };

        var sendResult = await _sender.SendAsync(sendRequest, ct);

        if (sendResult.IsSuccess)
        {
            // ── 10. Marcar ReminderSentAt en la cita ──────────────────────────
            appointment.ReminderSentAt = now;
            await _db.SaveChangesAsync(ct);

            // ── 11a. Métrica: reminder_sent ───────────────────────────────────
            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId         = tenantId,
                PatientId        = patient.Id,
                AppointmentId    = appointmentId,
                FlowId           = "flow_03",
                MetricType       = "reminder_sent",
                TwilioMessageSid = sendResult.TwilioSid,
                CorrelationId    = correlationId,
                Metadata         = JsonSerializer.Serialize(new
                {
                    message_id         = sendResult.MessageId,
                    twilio_sid         = sendResult.TwilioSid,
                    appointment_utc    = appointment.StartsAtUtc,
                    hours_before       = hoursBeforeReminder,
                    template_sid       = resolvedTemplateSid,
                }),
            }, ct);

            _logger.LogInformation(
                "[Flow03] Recordatorio enviado. PatientId={PtId} AppointmentId={AptId} " +
                "TwilioSid={Sid} TenantId={TenantId}",
                patient.Id, appointmentId, sendResult.TwilioSid, tenantId);

            return Flow03Result.Success(
                appointmentId, patient.Id, sendResult.MessageId, sendResult.TwilioSid);
        }
        else
        {
            // ── 11b. Métrica: reminder_failed ─────────────────────────────────
            await _metrics.RecordAsync(new FlowMetricsEvent
            {
                TenantId      = tenantId,
                PatientId     = patient.Id,
                AppointmentId = appointmentId,
                FlowId        = "flow_03",
                MetricType    = "reminder_failed",
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
                "[Flow03] Envío fallido. PatientId={PtId} AppointmentId={AptId} " +
                "ErrorCode={Code} Error={Error} TenantId={TenantId}",
                patient.Id, appointmentId, sendResult.ErrorCode,
                sendResult.ErrorMessage, tenantId);

            return Flow03Result.Failure(
                appointmentId, patient.Id, "outbound_send",
                $"[{sendResult.ErrorCode}] {sendResult.ErrorMessage}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lee template_sid desde RuleConfig del tenant (flow_03).
    /// Fallback: Flow03Options.DefaultTemplateSid.
    /// </summary>
    private async Task<string?> GetTemplateSidAsync(Guid tenantId, CancellationToken ct)
    {
        var rule = await _db.RuleConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "flow_03" &&
                r.RuleKey  == "template_sid" &&
                r.IsActive, ct);

        return rule?.RuleValue is { Length: > 0 } v ? v : _opts.DefaultTemplateSid;
    }

    /// <summary>
    /// Lee reminder_hours_before desde RuleConfig del tenant.
    /// Default: Flow03Options.DefaultReminderHoursBeforeAppointment (24h).
    /// </summary>
    private async Task<int> GetReminderHoursAsync(Guid tenantId, CancellationToken ct)
    {
        var rule = await _db.RuleConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "flow_03" &&
                r.RuleKey  == "reminder_hours_before" &&
                r.IsActive, ct);

        return rule is not null
            && int.TryParse(rule.RuleValue,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var h)
            && h > 0 ? h : _opts.DefaultReminderHoursBeforeAppointment;
    }

    /// <summary>
    /// Lee cooldown_minutes desde RuleConfig del tenant (flow_03).
    /// Default: 720 minutos (12 horas) para evitar doble envío.
    /// </summary>
    private async Task<int> GetCooldownMinutesAsync(Guid tenantId, CancellationToken ct)
    {
        var rule = await _db.RuleConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "flow_03" &&
                r.RuleKey  == "cooldown_minutes" &&
                r.IsActive, ct);

        return rule is not null
            && int.TryParse(rule.RuleValue,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var m)
            && m >= 0 ? m : 720;
    }

    /// <summary>
    /// Construye el template SID, variables y texto de fallback para el recordatorio.
    /// Las variables de tiempo se expresan en la timezone del tenant.
    /// </summary>
    private static (string? TemplateSid, string? TemplateVars, string FallbackBody) BuildTemplate(
        Domain.Tenants.Tenant tenant,
        string                patientFullName,
        Appointment           appointment,
        string?               templateSid)
    {
        // Convertir horario a timezone local del tenant
        var tzInfo     = GetTzInfoSafe(tenant.TimeZone);
        var localStart = tzInfo is not null
            ? TimeZoneInfo.ConvertTimeFromUtc(appointment.StartsAtUtc.UtcDateTime, tzInfo)
            : appointment.StartsAtUtc.LocalDateTime;

        var firstName  = patientFullName.Split(' ')[0];
        var dateStr    = localStart.ToString("dddd d 'de' MMMM",
                             new System.Globalization.CultureInfo("es-ES"));
        var timeStr    = localStart.ToString("HH:mm");
        var clinicName = tenant.Name;

        // Fallback body cuando no hay template aprobado en Twilio (staging)
        var fallbackBody =
            $"Hola {firstName}, te recordamos tu cita en {clinicName} " +
            $"el {dateStr} a las {timeStr}. " +
            "Si necesitas cambiar o cancelar tu cita, escríbenos aquí y te ayudamos.";

        // Template vars: JSON estándar Twilio (claves "1", "2", …)
        var vars = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["1"] = firstName,
            ["2"] = dateStr,
            ["3"] = timeStr,
            ["4"] = clinicName,
        });

        return (templateSid, vars, fallbackBody);
    }

    private static TimeZoneInfo? GetTzInfoSafe(string ianaTimezone)
    {
        try   { return TZConvert.GetTimeZoneInfo(ianaTimezone); }
        catch { return null; }
    }

    private async Task RecordSkipMetricAsync(
        Guid              tenantId,
        Guid              patientId,
        Guid              appointmentId,
        string            correlationId,
        string            reason,
        CancellationToken ct)
    {
        await _metrics.RecordAsync(new FlowMetricsEvent
        {
            TenantId      = tenantId,
            PatientId     = patientId,
            AppointmentId = appointmentId,
            FlowId        = "flow_03",
            MetricType    = "flow_skipped",
            CorrelationId = correlationId,
            Metadata      = JsonSerializer.Serialize(new { reason }),
        }, ct);
    }
}

// ── Opciones de configuración de Flow03 ──────────────────────────────────────

/// <summary>
/// Opciones de configuración de Flow03, enlazadas a la sección "Flow03Options".
/// </summary>
public sealed class Flow03Options
{
    public const string SectionName = "Flow03Options";

    /// <summary>
    /// Horas de antelación por defecto para enviar el recordatorio.
    /// Se puede sobreescribir por tenant vía RuleConfig flow_03/reminder_hours_before.
    /// Por defecto: 24 horas.
    /// </summary>
    public int DefaultReminderHoursBeforeAppointment { get; set; } = 24;

    /// <summary>
    /// Intervalo de polling del worker (minutos). Default: 15 min.
    /// Se usa también como tolerancia de ventana en el orchestrator.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// SID de la Content Template por defecto para el recordatorio.
    /// null → texto libre (FallbackBody) — útil en staging sin templates aprobados.
    /// Se puede sobreescribir por tenant vía RuleConfig flow_03/template_sid.
    /// </summary>
    public string? DefaultTemplateSid { get; set; }

    /// <summary>
    /// Horizonte máximo hacia adelante (horas) para el query del worker.
    /// Cubre cualquier configuración de reminder_hours_before de hasta N horas.
    /// Default: 48 horas.
    /// </summary>
    public int MaxHoursAheadToQuery { get; set; } = 48;
}

// ── Resultado de ejecución ────────────────────────────────────────────────────

public sealed record Flow03Result
{
    public bool   IsSuccess     { get; init; }
    public string FlowStep      { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public Guid   AppointmentId { get; init; }
    public Guid   PatientId     { get; init; }
    public Guid?  OutboundMessageId { get; init; }
    public string? TwilioMessageSid { get; init; }

    public static Flow03Result Success(
        Guid appointmentId, Guid patientId, Guid? messageId, string? sid) => new()
    {
        IsSuccess        = true,
        FlowStep         = "reminder_sent",
        AppointmentId    = appointmentId,
        PatientId        = patientId,
        OutboundMessageId = messageId,
        TwilioMessageSid  = sid,
    };

    public static Flow03Result Skipped(
        Guid appointmentId, Guid patientId, string reason) => new()
    {
        IsSuccess     = true,
        FlowStep      = "skipped",
        AppointmentId = appointmentId,
        PatientId     = patientId,
        ErrorMessage  = reason,
    };

    public static Flow03Result Failure(
        Guid appointmentId, Guid patientId, string step, string error) => new()
    {
        IsSuccess     = false,
        FlowStep      = step,
        AppointmentId = appointmentId,
        PatientId     = patientId,
        ErrorMessage  = error,
    };
}
