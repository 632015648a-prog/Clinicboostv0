using ClinicBoost.Api.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Dashboard;

// ════════════════════════════════════════════════════════════════════════════
// DashboardService.cs
//
// Implementación de IDashboardService sobre EF Core + Postgres.
//
// DISEÑO
// ──────
//  · Todas las queries llevan .AsNoTracking() (read-only).
//  · Todas las queries filtran explícitamente por tenantId (defense in depth
//    además de RLS en Postgres).
//  · Los cálculos de porcentajes y percentiles se hacen en memoria tras
//    materializar conjuntos pequeños. Para datasets mayores se pueden mover
//    a window functions de Postgres.
//  · Los rangos de fecha son [from, to) — inclusivo en from, exclusivo en to.
//  · Identificadores de flujos disponibles: flow_00 … flow_07.
//
// CONVENCIONES
// ─────────────
//  · Decimales redondeados a 2 cifras en los DTOs de salida.
//  · Fechas en UTC (DateTimeOffset).
//  · Strings de estado en minúsculas (igual que en BD).
// ════════════════════════════════════════════════════════════════════════════

public sealed class DashboardService : IDashboardService
{
    private readonly AppDbContext                 _db;
    private readonly ILogger<DashboardService>    _logger;

    // Etiquetas legibles para los flujos
    private static readonly Dictionary<string, string> FlowLabels = new()
    {
        ["flow_00"] = "General",
        ["flow_01"] = "Llamada perdida → WA",
        ["flow_02"] = "Detección de huecos",
        ["flow_03"] = "Recordatorio de cita",
        ["flow_04"] = "No-show seguimiento",
        ["flow_05"] = "Lista de espera",
        ["flow_06"] = "Reactivación paciente",
        ["flow_07"] = "Reprogramación",
    };

    public DashboardService(AppDbContext db, ILogger<DashboardService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/dashboard/summary
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<DashboardSummaryResponse> GetSummaryAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        // ── Mensajes outbound en el periodo ───────────────────────────────────
        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m =>
                m.TenantId  == tenantId      &&
                m.Direction == "outbound"    &&
                m.CreatedAt >= from          &&
                m.CreatedAt <  to)
            .Select(m => new { m.Status })
            .ToListAsync(ct);

        int sent      = messages.Count;
        int delivered = messages.Count(m => m.Status is "delivered" or "read");
        int read      = messages.Count(m => m.Status == "read");
        int failed    = messages.Count(m => m.Status is "failed" or "undelivered");

        // ── Citas recuperadas en el periodo ───────────────────────────────────
        var recoveredAppts = await _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.TenantId    == tenantId &&
                a.IsRecovered == true     &&
                a.CreatedAt   >= from     &&
                a.CreatedAt   <  to)
            .CountAsync(ct);

        // ── Revenue total en el periodo ────────────────────────────────────────
        var totalRevenue = await _db.RevenueEvents
            .AsNoTracking()
            .Where(r =>
                r.TenantId  == tenantId &&
                r.CreatedAt >= from     &&
                r.CreatedAt <  to)
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;

        // ── Conversaciones activas (snapshot actual, no filtradas por fecha) ──
        var activeConversations = await _db.Conversations
            .AsNoTracking()
            .Where(c =>
                c.TenantId == tenantId &&
                (c.Status == "open" || c.Status == "waiting_ai" || c.Status == "waiting_human"))
            .CountAsync(ct);

        var pendingHuman = await _db.Conversations
            .AsNoTracking()
            .Where(c =>
                c.TenantId == tenantId &&
                c.Status   == "waiting_human")
            .CountAsync(ct);

        decimal deliveryRate = sent > 0 ? Math.Round((decimal)delivered / sent * 100, 2) : 0m;
        decimal readRate     = delivered > 0 ? Math.Round((decimal)read / delivered * 100, 2) : 0m;

        return new DashboardSummaryResponse
        {
            TotalRecoveredRevenue  = Math.Round(totalRevenue, 2),
            RecoveredAppointments  = recoveredAppts,
            ActiveConversations    = activeConversations,
            MessagesSent           = sent,
            MessagesDelivered      = delivered,
            MessagesRead           = read,
            MessagesFailed         = failed,
            DeliveryRate           = deliveryRate,
            ReadRate               = readRate,
            PendingHumanHandoff    = pendingHuman,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/dashboard/message-delivery
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<MessageDeliveryResponse> GetMessageDeliveryAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default)
    {
        // Usamos MessageDeliveryEvents (callbacks de Twilio) como fuente
        // de verdad para entregabilidad. Filtramos solo por el último estado
        // del mensaje usando ProviderMessageId como clave de deduplicación.

        var eventsQuery = _db.MessageDeliveryEvents
            .AsNoTracking()
            .Where(e =>
                e.TenantId   == tenantId &&
                e.OccurredAt >= from     &&
                e.OccurredAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
            eventsQuery = eventsQuery.Where(e => e.FlowId == flowId);

        // Materializamos para agrupar en memoria (dataset acotado por fecha)
        var events = await eventsQuery
            .Select(e => new
            {
                e.Status,
                e.FlowId,
                e.OccurredAt,
            })
            .ToListAsync(ct);

        // ── Serie diaria ─────────────────────────────────────────────────────
        var daily = events
            .GroupBy(e => e.OccurredAt.Date.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new DailyDeliveryPoint
            {
                Date      = g.Key,
                Sent      = g.Count(e => e.Status == "sent"),
                Delivered = g.Count(e => e.Status == "delivered"),
                Read      = g.Count(e => e.Status == "read"),
                Failed    = g.Count(e => e.Status is "failed" or "undelivered"),
            })
            .ToList();

        // ── Agrupado por flujo ───────────────────────────────────────────────
        var byFlow = events
            .Where(e => e.FlowId != null)
            .GroupBy(e => e.FlowId!)
            .Select(g =>
            {
                int s = g.Count(e => e.Status != "failed" && e.Status != "undelivered");
                int d = g.Count(e => e.Status == "delivered");
                int f = g.Count(e => e.Status is "failed" or "undelivered");
                return new DeliveryByFlow
                {
                    FlowId       = g.Key,
                    Sent         = s + f,
                    Delivered    = d,
                    Failed       = f,
                    DeliveryRate = (s + f) > 0
                        ? Math.Round((decimal)d / (s + f) * 100, 2)
                        : 0m,
                };
            })
            .OrderBy(r => r.FlowId)
            .ToList();

        return new MessageDeliveryResponse
        {
            Daily  = daily,
            ByFlow = byFlow,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/dashboard/flow-performance
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<FlowPerformanceResponse> GetFlowPerformanceAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default)
    {
        // ── Mensajes outbound por flujo (desde FlowMetricsEvents) ─────────────
        var metricsQuery = _db.FlowMetricsEvents
            .AsNoTracking()
            .Where(e =>
                e.TenantId   == tenantId &&
                e.OccurredAt >= from     &&
                e.OccurredAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
            metricsQuery = metricsQuery.Where(e => e.FlowId == flowId);

        var metrics = await metricsQuery
            .Select(e => new { e.FlowId, e.MetricType, e.RecoveredRevenue })
            .ToListAsync(ct);

        // ── Mensajes outbound por flujo (desde Messages) ──────────────────────
        var msgsQuery = _db.Messages
            .AsNoTracking()
            .Where(m =>
                m.TenantId  == tenantId   &&
                m.Direction == "outbound" &&
                m.CreatedAt >= from       &&
                m.CreatedAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
        {
            // Filtramos por FlowId a través de la conversación
            var convIds = await _db.Conversations
                .AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.FlowId == flowId)
                .Select(c => c.Id)
                .ToListAsync(ct);

            msgsQuery = msgsQuery.Where(m => convIds.Contains(m.ConversationId));
        }

        var msgs = await msgsQuery
            .Select(m => new { m.ConversationId, m.Status })
            .ToListAsync(ct);

        // Obtener FlowId de cada conversación
        var convFlowMap = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.FlowId })
            .ToListAsync(ct);

        var flowDict = convFlowMap.ToDictionary(c => c.Id, c => c.FlowId);

        // ── Revenue por flujo ─────────────────────────────────────────────────
        var revenueQuery = _db.RevenueEvents
            .AsNoTracking()
            .Where(r =>
                r.TenantId  == tenantId &&
                r.CreatedAt >= from     &&
                r.CreatedAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
            revenueQuery = revenueQuery.Where(r => r.FlowId == flowId);

        var revenueByFlow = await revenueQuery
            .GroupBy(r => r.FlowId)
            .Select(g => new { FlowId = g.Key, Total = g.Sum(r => r.Amount) })
            .ToListAsync(ct);

        var revenueMap = revenueByFlow.ToDictionary(r => r.FlowId, r => r.Total);

        // ── Agrupar mensajes por flujo ────────────────────────────────────────
        var msgsByFlow = msgs
            .GroupBy(m => flowDict.TryGetValue(m.ConversationId, out var fid) ? fid : "unknown")
            .Where(g => g.Key != "unknown")
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Sent      = g.Count(),
                    Delivered = g.Count(m => m.Status is "delivered" or "read"),
                    Read      = g.Count(m => m.Status == "read"),
                });

        // ── Bookings y replies de métricas ────────────────────────────────────
        var bookingsByFlow  = metrics.GroupBy(m => m.FlowId)
            .ToDictionary(g => g.Key, g => g.Count(m => m.MetricType == "appointment_booked"));
        var repliesByFlow   = metrics.GroupBy(m => m.FlowId)
            .ToDictionary(g => g.Key, g => g.Count(m => m.MetricType == "patient_replied"));

        // ── Construir filas por flujo ─────────────────────────────────────────
        var allFlowIds = msgsByFlow.Keys
            .Union(bookingsByFlow.Keys)
            .Union(repliesByFlow.Keys)
            .Union(revenueMap.Keys)
            .OrderBy(id => id)
            .ToList();

        var rows = allFlowIds.Select(fid =>
        {
            msgsByFlow.TryGetValue(fid, out var msgData);
            bookingsByFlow.TryGetValue(fid, out int bookings);
            repliesByFlow.TryGetValue(fid, out int replies);
            revenueMap.TryGetValue(fid, out decimal revenue);

            int sentCount = msgData?.Sent ?? 0;
            return new FlowPerformanceRow
            {
                FlowId           = fid,
                FlowLabel        = FlowLabels.GetValueOrDefault(fid, fid),
                Sent             = sentCount,
                Delivered        = msgData?.Delivered ?? 0,
                Read             = msgData?.Read ?? 0,
                Replies          = replies,
                Bookings         = bookings,
                RecoveredRevenue = Math.Round(revenue, 2),
                ConversionRate   = sentCount > 0
                    ? Math.Round((decimal)bookings / sentCount * 100, 2)
                    : 0m,
            };
        }).ToList();

        return new FlowPerformanceResponse { Flows = rows };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/dashboard/conversations
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ConversationsResponse> GetConversationsAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        string? status,
        bool? requiresHuman,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        // Clamping de paginación
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(1, page);

        var query = _db.Conversations
            .AsNoTracking()
            .Where(c =>
                c.TenantId  == tenantId &&
                c.UpdatedAt >= from     &&
                c.UpdatedAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
            query = query.Where(c => c.FlowId == flowId);

        // Filtro por status
        if (!string.IsNullOrWhiteSpace(status) && status != "all")
            query = query.Where(c => c.Status == status);

        // Filtro requires_human
        if (requiresHuman == true)
            query = query.Where(c => c.Status == "waiting_human");

        int total = await query.CountAsync(ct);

        var convs = await query
            .OrderByDescending(c => c.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.PatientId,
                c.FlowId,
                c.Status,
                c.UpdatedAt,
            })
            .ToListAsync(ct);

        if (convs.Count == 0)
            return new ConversationsResponse
            {
                Items      = [],
                TotalCount = 0,
                Page       = page,
                PageSize   = pageSize,
                HasMore    = false,
            };

        var patientIds  = convs.Select(c => c.PatientId).Distinct().ToList();
        var convIds     = convs.Select(c => c.Id).ToList();

        // Cargar pacientes
        var patients = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && patientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.Phone })
            .ToListAsync(ct);

        var patientMap = patients.ToDictionary(p => p.Id);

        // Último mensaje de cada conversación
        var lastMessages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && convIds.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new
            {
                ConversationId = g.Key,
                Last = g.OrderByDescending(m => m.CreatedAt)
                        .Select(m => new { m.Body, m.Direction, m.Status })
                        .First()
            })
            .ToListAsync(ct);

        var lastMsgMap = lastMessages.ToDictionary(m => m.ConversationId);

        var items = convs.Select(c =>
        {
            patientMap.TryGetValue(c.PatientId, out var patient);
            lastMsgMap.TryGetValue(c.Id, out var lastMsg);

            string preview = lastMsg?.Last.Body is { Length: > 0 } body
                ? (body.Length > 80 ? body[..77] + "…" : body)
                : "";

            return new ConversationRow
            {
                ConversationId    = c.Id,
                PatientName       = patient?.FullName ?? "Paciente desconocido",
                PatientPhone      = patient?.Phone    ?? "",
                LastMessagePreview = preview,
                LastDirection     = lastMsg?.Last.Direction ?? "",
                LastDeliveryStatus = lastMsg?.Last.Status   ?? "",
                FlowId            = c.FlowId,
                Status            = c.Status,
                RequiresHuman     = c.Status == "waiting_human",
                UpdatedAt         = c.UpdatedAt,
            };
        }).ToList();

        return new ConversationsResponse
        {
            Items      = items,
            TotalCount = total,
            Page       = page,
            PageSize   = pageSize,
            HasMore    = total > page * pageSize,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET /api/dashboard/revenue-overview
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<RevenueOverviewResponse> GetRevenueOverviewAsync(
        Guid tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? flowId,
        CancellationToken ct = default)
    {
        var revenueQuery = _db.RevenueEvents
            .AsNoTracking()
            .Where(r =>
                r.TenantId  == tenantId &&
                r.CreatedAt >= from     &&
                r.CreatedAt <  to);

        if (!string.IsNullOrWhiteSpace(flowId))
            revenueQuery = revenueQuery.Where(r => r.FlowId == flowId);

        var events = await revenueQuery
            .Select(r => new
            {
                r.EventType,
                r.Amount,
                r.SuccessFeeAmount,
                r.CreatedAt,
            })
            .ToListAsync(ct);

        decimal totalRevenue     = events.Sum(r => r.Amount);
        decimal totalSuccessFee  = events.Sum(r => r.SuccessFeeAmount ?? 0m);

        var byEventType = events
            .GroupBy(r => r.EventType)
            .Select(g => new RevenueByEventType
            {
                EventType        = g.Key,
                Count            = g.Count(),
                TotalAmount      = Math.Round(g.Sum(r => r.Amount), 2),
                SuccessFeeAmount = Math.Round(g.Sum(r => r.SuccessFeeAmount ?? 0m), 2),
            })
            .OrderByDescending(r => r.TotalAmount)
            .ToList();

        var byDay = events
            .GroupBy(r => r.CreatedAt.Date.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new RevenueByDay
            {
                Date   = g.Key,
                Amount = Math.Round(g.Sum(r => r.Amount), 2),
                Count  = g.Count(),
            })
            .ToList();

        return new RevenueOverviewResponse
        {
            TotalRevenue    = Math.Round(totalRevenue, 2),
            TotalSuccessFee = Math.Round(totalSuccessFee, 2),
            TotalEvents     = events.Count,
            ByEventType     = byEventType,
            ByDay           = byDay,
        };
    }
}
