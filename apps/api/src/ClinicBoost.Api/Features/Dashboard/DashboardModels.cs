namespace ClinicBoost.Api.Features.Dashboard;

// ════════════════════════════════════════════════════════════════════════════
// DashboardModels.cs
//
// DTOs de respuesta para los endpoints del Dashboard MVP.
//
// DISEÑO
// ──────
//  · Solo DTOs de respuesta (read-only). No hay DTOs de request (todo es GET).
//  · Todos los datos están previamente filtrados por tenant_id en el servicio.
//  · Decimales redondeados a 2 cifras antes de serializar.
//  · Las fechas se devuelven en ISO 8601 UTC.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// GET /api/dashboard/summary
/// KPIs globales del tenant en el rango solicitado.
/// </summary>
public sealed record DashboardSummaryResponse
{
    /// <summary>Ingresos recuperados totales (EUR) atribuidos a ClinicBoost.</summary>
    public decimal TotalRecoveredRevenue { get; init; }

    /// <summary>Citas recuperadas (appointments.is_recovered = true).</summary>
    public int RecoveredAppointments { get; init; }

    /// <summary>Conversaciones activas (status = open | waiting_ai | waiting_human).</summary>
    public int ActiveConversations { get; init; }

    /// <summary>Mensajes outbound enviados en el periodo.</summary>
    public int MessagesSent { get; init; }

    /// <summary>Mensajes con status = delivered.</summary>
    public int MessagesDelivered { get; init; }

    /// <summary>Mensajes con status = read.</summary>
    public int MessagesRead { get; init; }

    /// <summary>Mensajes con status = failed.</summary>
    public int MessagesFailed { get; init; }

    /// <summary>Tasa de entrega (0-100).</summary>
    public decimal DeliveryRate { get; init; }

    /// <summary>Tasa de lectura sobre entregados (0-100).</summary>
    public decimal ReadRate { get; init; }

    /// <summary>Conversaciones pendientes de intervención humana.</summary>
    public int PendingHumanHandoff { get; init; }
}

/// <summary>
/// GET /api/dashboard/message-delivery
/// Entregabilidad diaria de mensajes para el gráfico de barras.
/// </summary>
public sealed record MessageDeliveryResponse
{
    public IReadOnlyList<DailyDeliveryPoint> Daily { get; init; } = [];
    public IReadOnlyList<DeliveryByFlow>     ByFlow { get; init; } = [];
}

public sealed record DailyDeliveryPoint
{
    public string Date { get; init; } = "";     // "YYYY-MM-DD"
    public int    Sent { get; init; }
    public int    Delivered { get; init; }
    public int    Read { get; init; }
    public int    Failed { get; init; }
}

public sealed record DeliveryByFlow
{
    public string  FlowId { get; init; } = "";
    public int     Sent { get; init; }
    public int     Delivered { get; init; }
    public int     Failed { get; init; }
    public decimal DeliveryRate { get; init; }
}

/// <summary>
/// GET /api/dashboard/flow-performance
/// Rendimiento por flujo de automatización.
/// </summary>
public sealed record FlowPerformanceResponse
{
    public IReadOnlyList<FlowPerformanceRow> Flows { get; init; } = [];
}

public sealed record FlowPerformanceRow
{
    public string  FlowId { get; init; } = "";
    public string  FlowLabel { get; init; } = "";
    public int     Sent { get; init; }
    public int     Delivered { get; init; }
    public int     Read { get; init; }
    public int     Replies { get; init; }
    public int     Bookings { get; init; }
    public decimal RecoveredRevenue { get; init; }
    public decimal ConversionRate { get; init; }   // bookings / sent (0-100)
}

/// <summary>
/// GET /api/dashboard/conversations  (paginado)
/// Lista de conversaciones recientes con estado de entrega y handoff.
/// </summary>
public sealed record ConversationsResponse
{
    public IReadOnlyList<ConversationRow> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public bool HasMore { get; init; }
}

public sealed record ConversationRow
{
    public Guid    ConversationId { get; init; }
    public string  PatientName { get; init; } = "";
    public string  PatientPhone { get; init; } = "";
    public string? LastMessagePreview { get; init; }
    public string  LastDirection { get; init; } = "";       // inbound | outbound
    public string  LastDeliveryStatus { get; init; } = "";  // sent | delivered | read | failed | received
    public string  FlowId { get; init; } = "";
    public string  Status { get; init; } = "";              // open | waiting_ai | waiting_human | resolved
    public bool    RequiresHuman { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// GET /api/dashboard/revenue-overview
/// Visión económica del periodo.
/// </summary>
public sealed record RevenueOverviewResponse
{
    public decimal TotalRevenue { get; init; }
    public decimal TotalSuccessFee { get; init; }
    public int     TotalEvents { get; init; }
    public IReadOnlyList<RevenueByEventType> ByEventType { get; init; } = [];
    public IReadOnlyList<RevenueByDay>       ByDay { get; init; } = [];
}

public sealed record RevenueByEventType
{
    public string  EventType { get; init; } = "";
    public int     Count { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SuccessFeeAmount { get; init; }
}

public sealed record RevenueByDay
{
    public string  Date { get; init; } = "";    // "YYYY-MM-DD"
    public decimal Amount { get; init; }
    public int     Count { get; init; }
}

/// <summary>
/// Query parameters comunes a todos los endpoints del dashboard.
/// </summary>
public sealed record DashboardQueryParams
{
    public string? DateFrom { get; init; }   // ISO date "YYYY-MM-DD"
    public string? DateTo   { get; init; }   // ISO date "YYYY-MM-DD"
    public string? FlowId   { get; init; }   // optional flow filter
}

/// <summary>
/// Query params para /conversations (añade paginación).
/// </summary>
public sealed record ConversationsQueryParams
{
    public string? DateFrom       { get; init; }
    public string? DateTo         { get; init; }
    public string? FlowId         { get; init; }
    public string? Status         { get; init; }   // open | waiting_human | resolved | all
    public bool?   RequiresHuman  { get; init; }
    public int     Page           { get; init; } = 1;
    public int     PageSize       { get; init; } = 20;
}
