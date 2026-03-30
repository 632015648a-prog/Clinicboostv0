using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Patients;

namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// AgentModels.cs
//
// Modelos de dominio del agente conversacional.
//
// DISEÑO
// ──────
// Todos los records son inmutables (init-only). El agente nunca muta estado
// durante su ciclo de vida; produce un AgentResult que el worker persiste.
//
// HARD LIMITS (aplicados por HardLimitGuard ANTES de enviar la respuesta)
// ────────────────────────────────────────────────────────────────────────
//   1. NUNCA confirmar reserva sin pasar por el endpoint de backend.
//   2. NUNCA proponer descuento superior a RuleConfig global/discount_max_pct.
//   3. SIEMPRE derivar a humano cuando falte contexto crítico.
//   4. NUNCA enviar datos PII de un paciente a otro.
//   5. NUNCA responder fuera de la ventana de sesión WhatsApp (24 h) con
//      texto libre — usar plantilla aprobada.
// ════════════════════════════════════════════════════════════════════════════

// ── Intenciones ──────────────────────────────────────────────────────────────

/// <summary>
/// Intención detectada en el mensaje del paciente.
/// Usada para seleccionar el prompt de sistema y las tools disponibles.
/// </summary>
public enum Intent
{
    /// <summary>Solicitud de nueva cita o cambio de horario.</summary>
    BookAppointment      = 1,

    /// <summary>Solicitud de cancelación de una cita existente.</summary>
    CancelAppointment    = 2,

    /// <summary>Consulta sobre próximas citas o historial.</summary>
    QueryAppointment     = 3,

    /// <summary>Pregunta general sobre servicios, precios o ubicación.</summary>
    GeneralInquiry       = 4,

    /// <summary>
    /// Queja o insatisfacción con el servicio.
    /// Hard limit: siempre derivar a humano.
    /// </summary>
    Complaint            = 5,

    /// <summary>
    /// Solicitud de descuento o promoción.
    /// Hard limit: no superar discount_max_pct de RuleConfig.
    /// </summary>
    DiscountRequest      = 6,

    /// <summary>
    /// El paciente quiere hablar con una persona real.
    /// Hard limit: derivar siempre, sin intentar retener.
    /// </summary>
    EscalateToHuman      = 7,

    /// <summary>
    /// Respuesta a un recordatorio o confirmación de cita (Flow 03).
    /// </summary>
    AppointmentConfirm   = 8,

    /// <summary>No se puede determinar la intención con confianza suficiente.</summary>
    Unknown              = 99,
}

/// <summary>
/// Resultado de la clasificación de intención.
/// </summary>
public sealed record IntentClassification
{
    /// <summary>Intención primaria detectada.</summary>
    public required Intent   Intent     { get; init; }

    /// <summary>Confianza entre 0.0 y 1.0.</summary>
    public required double   Confidence { get; init; }

    /// <summary>
    /// Breve justificación de la clasificación (1 frase).
    /// Usada para logs y observabilidad.
    /// </summary>
    public required string   Reasoning  { get; init; }

    /// <summary>
    /// Si la confianza es inferior a este umbral, el agente deriva a humano.
    /// </summary>
    public static double MinConfidenceThreshold => 0.65;

    public bool IsLowConfidence => Confidence < MinConfidenceThreshold;
}

// ── Contexto del agente ───────────────────────────────────────────────────────

/// <summary>
/// Snapshot completo del contexto necesario para que el agente tome decisiones.
/// Se construye en el worker antes de llamar a IConversationalAgent.
/// Es inmutable: el agente lo lee pero nunca lo modifica.
/// </summary>
public sealed record AgentContext
{
    // ── Identidad ──────────────────────────────────────────────────────────
    public required Guid   TenantId       { get; init; }
    public required Guid   PatientId      { get; init; }
    public required Guid   ConversationId { get; init; }
    public required string CorrelationId  { get; init; }

    // ── Mensaje entrante ───────────────────────────────────────────────────
    public required string MessageSid     { get; init; }
    public required string InboundText    { get; init; }
    public          string? MediaUrl      { get; init; }

    // ── Estado del paciente ────────────────────────────────────────────────
    public required string PatientName    { get; init; }
    public required string PatientPhone   { get; init; }
    public          bool   RgpdConsent    { get; init; }

    // ── Estado de la conversación ──────────────────────────────────────────
    public required string ConversationStatus  { get; init; }  // open|waiting_ai|…
    public required string AiContextJson       { get; init; }  // JSON del turno anterior
    public required bool   IsInsideSessionWindow { get; init; } // ventana 24 h WA

    // ── Historial reciente (últimos N mensajes) ────────────────────────────
    public required IReadOnlyList<Message> RecentMessages { get; init; }

    // ── Configuración del tenant ───────────────────────────────────────────
    /// <summary>Porcentaje máximo de descuento permitido. Default 0 (sin descuentos).</summary>
    public required decimal DiscountMaxPct  { get; init; }

    /// <summary>Nombre de la clínica para personalizar respuestas.</summary>
    public required string  ClinicName      { get; init; }

    /// <summary>Idioma preferido de la clínica (es | en | ca | …).</summary>
    public required string  LanguageCode    { get; init; }
}

// ── Resultado del agente ─────────────────────────────────────────────────────

/// <summary>
/// Acción que el agente decide tomar tras procesar el mensaje.
/// El worker persiste el turno y ejecuta la acción.
/// </summary>
public enum AgentAction
{
    /// <summary>Enviar un mensaje de texto al paciente.</summary>
    SendMessage        = 1,

    /// <summary>
    /// Proponer una cita (NO confirmar). El backend ejecuta la reserva real.
    /// </summary>
    ProposeAppointment = 2,

    /// <summary>Derivar la conversación a un agente humano.</summary>
    EscalateToHuman    = 3,

    /// <summary>
    /// La conversación ha terminado satisfactoriamente.
    /// El worker marcará Conversation.Status = "resolved".
    /// </summary>
    Resolve            = 4,

    /// <summary>No hay acción (mensaje de media sin texto, spam, etc.).</summary>
    NoAction           = 5,
}

/// <summary>
/// Resultado completo que el agente devuelve al worker.
/// El worker es responsable de persistir el turno y ejecutar la acción.
/// </summary>
public sealed record AgentResult
{
    /// <summary>Acción decidida por el agente.</summary>
    public required AgentAction      Action          { get; init; }

    /// <summary>Texto de la respuesta al paciente. Null en EscalateToHuman/NoAction.</summary>
    public          string?          ResponseText    { get; init; }

    /// <summary>Intención clasificada en este turno.</summary>
    public required IntentClassification Intent      { get; init; }

    /// <summary>
    /// Datos de la cita propuesta. Solo cuando Action = ProposeAppointment.
    /// El backend los usa para llamar al calendario.
    /// </summary>
    public          AppointmentProposal? Proposal    { get; init; }

    /// <summary>Motivo de derivación a humano. Solo cuando Action = EscalateToHuman.</summary>
    public          string?          EscalationReason { get; init; }

    /// <summary>
    /// JSON actualizado del AiContext de la conversación.
    /// El worker persiste este valor en Conversation.AiContext.
    /// </summary>
    public required string           UpdatedAiContextJson { get; init; }

    // ── Trazabilidad IA ───────────────────────────────────────────────────
    public required string  ModelUsed         { get; init; }
    public required int     PromptTokens      { get; init; }
    public required int     CompletionTokens  { get; init; }

    /// <summary>
    /// Turno rechazado por HardLimitGuard.
    /// True indica que la respuesta original de la IA fue bloqueada
    /// y se sustituyó por una derivación a humano.
    /// </summary>
    public          bool    WasBlocked        { get; init; }

    /// <summary>Razón del bloqueo si WasBlocked = true.</summary>
    public          string? BlockReason       { get; init; }
}

/// <summary>
/// Propuesta de cita generada por el agente.
/// NUNCA se confirma aquí — el backend ejecuta la reserva.
/// </summary>
public sealed record AppointmentProposal
{
    public required string        TherapistName  { get; init; }
    public required DateTimeOffset StartsAtUtc   { get; init; }
    public required DateTimeOffset EndsAtUtc     { get; init; }
    public          string?        Notes         { get; init; }
}

// ── Turno persistido ─────────────────────────────────────────────────────────

/// <summary>
/// Registro inmutable de un turno del agente conversacional.
/// INSERT-only. Una fila por mensaje inbound procesado por la IA.
/// </summary>
public sealed class AgentTurn
{
    public Guid   Id             { get; init; } = Guid.NewGuid();
    public Guid   TenantId       { get; init; }
    public Guid   ConversationId { get; init; }
    public Guid   MessageId      { get; init; }  // FK → messages.id (el inbound)

    // Clasificación
    public required string IntentName       { get; init; }
    public          double IntentConfidence { get; init; }

    // Resultado
    public required string ActionName       { get; init; }
    public          string? ResponseText    { get; init; }
    public          string? EscalationReason{ get; init; }
    public          bool    WasBlocked      { get; init; }
    public          string? BlockReason     { get; init; }

    // Trazabilidad IA
    public required string ModelUsed        { get; init; }
    public          int    PromptTokens     { get; init; }
    public          int    CompletionTokens { get; init; }

    // Correlación
    public required string CorrelationId    { get; init; }
    public          DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
