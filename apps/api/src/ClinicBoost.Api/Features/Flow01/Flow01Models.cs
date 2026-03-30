namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// Flow01Models
//
// DTOs, eventos y resultados del flujo end-to-end:
//   Llamada perdida → Mensaje WhatsApp saliente → Reserva conversacional.
//
// DENOMINACIÓN DE FLOWS
// ──────────────────────
//   flow_01  = llamada perdida → WhatsApp recovery
//              (antes llamado flow_00 en el worker; renombrado al integrar
//               el envío real y las métricas de conversión)
//
// SEPARACIÓN DE RESPONSABILIDADES
// ─────────────────────────────────
//   · Flow01Orchestrator — coordina los pasos y captura métricas.
//   · IOutboundMessageSender — abstrae el canal (Twilio / stub / test).
//   · FlowMetricsService — persiste y consulta métricas KPI.
//   · FlowMetricsEvent — entidad inmutable que registra cada medición.
// ════════════════════════════════════════════════════════════════════════════

// ── Resultado del orquestador ────────────────────────────────────────────────

/// <summary>
/// Resultado de la ejecución de Flow01 para un job concreto.
/// </summary>
public sealed record Flow01Result
{
    public bool    IsSuccess    { get; init; }
    public string  FlowStep     { get; init; } = string.Empty;  // último paso alcanzado
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// ID del mensaje outbound creado (o null si no se llegó a enviar).
    /// </summary>
    public Guid?   OutboundMessageId { get; init; }

    /// <summary>
    /// SID de Twilio del mensaje saliente (null si Twilio falló o no se envió).
    /// </summary>
    public string? TwilioMessageSid { get; init; }

    /// <summary>
    /// ID del paciente resuelto o creado durante el flujo.
    /// </summary>
    public Guid PatientId { get; init; }

    /// <summary>
    /// Tiempo desde la llamada perdida hasta el envío del WhatsApp (ms).
    /// Null si no se llegó a enviar.
    /// </summary>
    public long? ResponseTimeMs { get; init; }

    public static Flow01Result Success(
        Guid    patientId,
        Guid?   messageId,
        string? twilioSid,
        long?   responseTimeMs,
        string  step = "completed")
        => new()
        {
            IsSuccess         = true,
            PatientId         = patientId,
            OutboundMessageId = messageId,
            TwilioMessageSid  = twilioSid,
            ResponseTimeMs    = responseTimeMs,
            FlowStep          = step,
        };

    public static Flow01Result Skipped(Guid patientId, string reason)
        => new()
        {
            IsSuccess   = true,   // no es un error, es un skip legítimo
            PatientId   = patientId,
            FlowStep    = "skipped",
            ErrorMessage = reason,
        };

    public static Flow01Result Failure(Guid patientId, string step, string error)
        => new()
        {
            IsSuccess    = false,
            PatientId    = patientId,
            FlowStep     = step,
            ErrorMessage = error,
        };
}

// ── Opciones de envío saliente ────────────────────────────────────────────────

/// <summary>
/// Solicitud de envío de un mensaje outbound a través de un canal.
/// </summary>
public sealed record OutboundMessageRequest
{
    /// <summary>Número de destino en formato E.164 (paciente).</summary>
    public required string ToPhone   { get; init; }

    /// <summary>Número de origen en formato E.164 (clínica, prefijo whatsapp:).</summary>
    public required string FromPhone { get; init; }

    /// <summary>Canal de envío: "whatsapp" | "sms".</summary>
    public required string Channel   { get; init; }

    /// <summary>Texto plano del mensaje (mutuamente excluyente con TemplateSid).</summary>
    public string? Body              { get; init; }

    /// <summary>SID de la Content Template aprobada de Twilio (para mensajes de plantilla).</summary>
    public string? TemplateSid       { get; init; }

    /// <summary>Variables de la plantilla (JSON). Solo se usa si TemplateSid != null.</summary>
    public string? TemplateVars      { get; init; }

    /// <summary>FlowId para trazabilidad en la tabla messages.</summary>
    public required string FlowId    { get; init; }

    /// <summary>TenantId para RLS y registro en BD.</summary>
    public required Guid   TenantId  { get; init; }

    /// <summary>PatientId para asociar el mensaje a la conversación.</summary>
    public required Guid   PatientId { get; init; }

    /// <summary>ConversationId si ya existe; null para crear una nueva.</summary>
    public Guid? ConversationId      { get; init; }

    /// <summary>ID de correlación para trazabilidad end-to-end.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>
/// Resultado del envío de un mensaje outbound.
/// </summary>
public sealed record OutboundSendResult
{
    public bool    IsSuccess  { get; init; }
    public Guid    MessageId  { get; init; }   // ID en tabla messages (siempre creado)
    public string? TwilioSid  { get; init; }   // null si Twilio falló
    public string? ErrorCode  { get; init; }
    public string? ErrorMessage { get; init; }
    public string  Status     { get; init; } = "pending";

    public static OutboundSendResult Success(Guid messageId, string twilioSid)
        => new() { IsSuccess = true, MessageId = messageId,
                   TwilioSid = twilioSid, Status = "sent" };

    public static OutboundSendResult TwilioFailure(
        Guid messageId, string errorCode, string errorMessage)
        => new() { IsSuccess = false, MessageId = messageId,
                   ErrorCode = errorCode, ErrorMessage = errorMessage,
                   Status = "failed" };
}

// ── FlowMetricsEvent (entidad inmutable) ──────────────────────────────────────

/// <summary>
/// Evento de métricas inmutable (INSERT-only) que registra KPIs del flujo.
///
/// Una fila = una medición de un paso del flujo.
/// Permite calcular:
///   · Tiempo de respuesta (llamada → WhatsApp enviado)
///   · Tasa de conversión (WA enviado → cita confirmada)
///   · Recovered revenue atribuido al flujo
/// </summary>
public sealed class FlowMetricsEvent
{
    public Guid   Id           { get; init; } = Guid.NewGuid();
    public Guid   TenantId     { get; init; }
    public Guid?  PatientId    { get; init; }
    public Guid?  AppointmentId { get; init; }

    /// <summary>Identificador del flujo: "flow_01".</summary>
    public required string FlowId          { get; init; }

    /// <summary>
    /// Tipo de métrica:
    ///   missed_call_received   — llamada perdida detectada
    ///   outbound_sent          — WhatsApp de recovery enviado
    ///   outbound_failed        — envío fallido (incluye ErrorCode)
    ///   patient_replied        — respuesta del paciente recibida
    ///   appointment_booked     — cita reservada conversacionalmente
    ///   appointment_cancelled  — cita cancelada
    ///   flow_skipped           — flujo omitido (sin consentimiento, etc.)
    /// </summary>
    public required string MetricType      { get; init; }

    /// <summary>
    /// Tiempo en milisegundos entre el evento trigger y este paso.
    /// Para outbound_sent: ms desde la llamada perdida hasta el envío.
    /// Para appointment_booked: ms desde el envío del WA hasta la reserva.
    /// </summary>
    public long?   DurationMs    { get; init; }

    /// <summary>Revenue recuperado en este evento (solo para appointment_booked).</summary>
    public decimal? RecoveredRevenue { get; init; }
    public string   Currency    { get; init; } = "EUR";

    /// <summary>SID de Twilio del mensaje saliente (para correlación).</summary>
    public string? TwilioMessageSid  { get; init; }

    /// <summary>Código de error de Twilio (para outbound_failed).</summary>
    public string? ErrorCode         { get; init; }

    /// <summary>ID de correlación end-to-end.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Metadatos adicionales en JSON (canal, plantilla, etc.).</summary>
    public string  Metadata     { get; init; } = "{}";

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

// ── DTOs de respuesta del endpoint de métricas ────────────────────────────────

/// <summary>
/// KPIs agregados de Flow01 para un rango de fechas.
/// </summary>
public sealed record Flow01MetricsSummary
{
    public DateTimeOffset From          { get; init; }
    public DateTimeOffset To            { get; init; }

    public int   MissedCallsReceived   { get; init; }
    public int   OutboundSent          { get; init; }
    public int   OutboundFailed        { get; init; }
    public int   PatientReplies        { get; init; }
    public int   AppointmentsBooked    { get; init; }

    /// <summary>Tasa de conversión: bookings / outbound_sent (0-1).</summary>
    public double ConversionRate        { get; init; }

    /// <summary>Tiempo de respuesta promedio (llamada → WA enviado) en ms.</summary>
    public double AvgResponseTimeMs     { get; init; }

    /// <summary>Tiempo de respuesta p95 en ms.</summary>
    public double P95ResponseTimeMs     { get; init; }

    /// <summary>Revenue total recuperado atribuido a Flow01.</summary>
    public decimal TotalRecoveredRevenue { get; init; }
    public string  Currency             { get; init; } = "EUR";
}
