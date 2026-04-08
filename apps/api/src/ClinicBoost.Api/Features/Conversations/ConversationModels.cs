namespace ClinicBoost.Api.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ConversationModels.cs
//
// DTOs para la Inbox operacional de ClinicBoost.
//
// ENDPOINTS
// ─────────
//   GET    /api/conversations                  — lista paginada con filtros
//   GET    /api/conversations/{id}/messages    — mensajes de una conversación
//   PATCH  /api/conversations/{id}/status      — cambio de estado operacional
//
// SEGURIDAD
// ─────────
//  · TenantId siempre de ITenantContext (JWT). Nunca del body ni query.
//  · PATCH valida que la conversación pertenezca al tenant antes de mutar.
// ════════════════════════════════════════════════════════════════════════════

// ── Query params ─────────────────────────────────────────────────────────────

/// <summary>
/// Parámetros de filtro para GET /api/conversations.
/// </summary>
public sealed record InboxQueryParams
{
    /// <summary>Filtrar por estado: open | waiting_human | resolved | all (default).</summary>
    public string? Status       { get; init; }

    /// <summary>Filtrar por flujo: flow_00 … flow_07.</summary>
    public string? FlowId       { get; init; }

    /// <summary>Solo conversaciones que requieren intervención humana.</summary>
    public bool?   RequiresHuman { get; init; }

    /// <summary>Búsqueda libre por nombre o teléfono del paciente.</summary>
    public string? Search       { get; init; }

    public int     Page     { get; init; } = 1;
    public int     PageSize { get; init; } = 20;
}

// ── Respuestas ────────────────────────────────────────────────────────────────

/// <summary>
/// GET /api/conversations  — lista paginada de conversaciones.
/// </summary>
public sealed record InboxListResponse
{
    public IReadOnlyList<InboxConversationItem> Items { get; init; } = [];
    public int  TotalCount  { get; init; }
    public int  Page        { get; init; }
    public int  PageSize    { get; init; }
    public bool HasMore     { get; init; }

    /// <summary>Contador de waiting_human para el badge del header.</summary>
    public int  WaitingHumanCount { get; init; }
}

/// <summary>Fila de conversación en la lista de la Inbox.</summary>
public sealed record InboxConversationItem
{
    public Guid    ConversationId      { get; init; }
    public string  PatientName         { get; init; } = "";
    public string  PatientPhone        { get; init; } = "";
    public string  FlowId              { get; init; } = "";
    public string  Status              { get; init; } = "";          // open | waiting_ai | waiting_human | resolved
    public bool    RequiresHuman       { get; init; }
    public string? LastMessagePreview  { get; init; }
    public string  LastDirection       { get; init; } = "";          // inbound | outbound
    public string  LastDeliveryStatus  { get; init; } = "";          // sent | delivered | read | failed | received
    public int     MessageCount        { get; init; }
    public DateTimeOffset LastMessageAt { get; init; }
    public DateTimeOffset UpdatedAt    { get; init; }
    public DateTimeOffset CreatedAt    { get; init; }
}

/// <summary>
/// GET /api/conversations/{id}/messages — historial de mensajes de la conversación.
/// </summary>
public sealed record ConversationDetailResponse
{
    public Guid    ConversationId { get; init; }
    public string  PatientName    { get; init; } = "";
    public string  PatientPhone   { get; init; } = "";
    public string  FlowId         { get; init; } = "";
    public string  Status         { get; init; } = "";
    public bool    RequiresHuman  { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<InboxMessageItem> Messages { get; init; } = [];
}

/// <summary>Un mensaje dentro del detalle de conversación.</summary>
public sealed record InboxMessageItem
{
    public Guid    MessageId          { get; init; }
    public string  Direction          { get; init; } = "";           // inbound | outbound
    public string? Body               { get; init; }
    public string  Status             { get; init; } = "";
    public bool    GeneratedByAi      { get; init; }
    public string? AiModel            { get; init; }
    public string? TemplateId         { get; init; }
    public string? MediaUrl           { get; init; }
    public string? MediaType          { get; init; }
    public DateTimeOffset? SentAt     { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
    public DateTimeOffset? ReadAt     { get; init; }
    public DateTimeOffset  CreatedAt  { get; init; }
}

// ── Mutation ──────────────────────────────────────────────────────────────────

/// <summary>
/// Body de PATCH /api/conversations/{id}/status.
/// </summary>
public sealed record PatchConversationStatusRequest
{
    /// <summary>
    /// Nuevo estado. Valores permitidos:
    ///   "waiting_human"  — escala a agente humano (pausa la IA)
    ///   "open"           — reactiva la automatización
    ///   "resolved"       — cierra la conversación
    /// </summary>
    public string Status { get; init; } = "";

    /// <summary>Nota interna opcional sobre el motivo del cambio.</summary>
    public string? Note { get; init; }
}

/// <summary>Respuesta de PATCH /api/conversations/{id}/status.</summary>
public sealed record PatchConversationStatusResponse
{
    public Guid   ConversationId { get; init; }
    public string NewStatus      { get; init; } = "";
    public string PreviousStatus { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
}

// ── Pending handoff (widget de polling del dashboard) ─────────────────────────

/// <summary>
/// GET /api/conversations/pending-handoff
/// Resumen ligero de conversaciones en estado waiting_human.
/// Diseñado para polling corto (30 s) desde el dashboard.
/// </summary>
public sealed record PendingHandoffResponse
{
    /// <summary>Total de conversaciones actualmente en waiting_human.</summary>
    public int Count { get; init; }

    /// <summary>Las 10 más antiguas (mayor urgencia primero).</summary>
    public IReadOnlyList<PendingHandoffItem> Items { get; init; } = [];
}

/// <summary>Fila resumen para el widget de intervención humana del dashboard.</summary>
public sealed record PendingHandoffItem
{
    public Guid   ConversationId { get; init; }
    public string PatientName    { get; init; } = "";
    public string PatientPhone   { get; init; } = "";
    public string FlowId         { get; init; } = "";

    /// <summary>Minutos desde el último cambio de estado (aproximación de tiempo de espera).</summary>
    public int WaitingMinutes { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
