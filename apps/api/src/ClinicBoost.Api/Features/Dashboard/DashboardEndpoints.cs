using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace ClinicBoost.Api.Features.Dashboard;

// ════════════════════════════════════════════════════════════════════════════
// DashboardEndpoints.cs
//
// Endpoints del Dashboard MVP (Vertical Slice).
//
// RUTAS
// ─────
//   GET /api/dashboard/summary              — KPIs globales del tenant
//   GET /api/dashboard/message-delivery     — entregabilidad diaria y por flujo
//   GET /api/dashboard/flow-performance     — rendimiento por flujo de automatización
//   GET /api/dashboard/conversations        — lista de conversaciones (paginada)
//   GET /api/dashboard/revenue-overview     — visión económica del periodo
//
// FILTROS COMUNES
// ───────────────
//   date_from  — ISO date "YYYY-MM-DD" (default: hoy - 30 días)
//   date_to    — ISO date "YYYY-MM-DD" (default: mañana — rango abierto)
//   flow_id    — filtro opcional por flujo (flow_00 … flow_07)
//
// SEGURIDAD
// ─────────
//  · Todos los endpoints requieren JWT válido (RequireAuthorization).
//  · TenantId se extrae de ITenantContext (claim JWT). NUNCA del body o query.
//  · Cada query en DashboardService filtra explícitamente por tenantId.
// ════════════════════════════════════════════════════════════════════════════

public static class DashboardEndpoints
{
    private const int DefaultDays = 30;

    public static IEndpointRouteBuilder MapDashboardEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard")
            .RequireAuthorization()
            .WithTags("Dashboard");

        // GET /api/dashboard/summary
        group.MapGet("/summary", GetSummaryAsync)
            .WithSummary("KPIs globales del tenant en el periodo seleccionado.");

        // GET /api/dashboard/message-delivery
        group.MapGet("/message-delivery", GetMessageDeliveryAsync)
            .WithSummary("Serie diaria y agrupación por flujo de entregabilidad de mensajes.");

        // GET /api/dashboard/flow-performance
        group.MapGet("/flow-performance", GetFlowPerformanceAsync)
            .WithSummary("Rendimiento de cada flujo de automatización (enviados, entregados, bookings, revenue).");

        // GET /api/dashboard/conversations
        group.MapGet("/conversations", GetConversationsAsync)
            .WithSummary("Lista paginada de conversaciones con estado de entrega y handoff humano.");

        // GET /api/dashboard/revenue-overview
        group.MapGet("/revenue-overview", GetRevenueOverviewAsync)
            .WithSummary("Visión económica del periodo: revenue recuperado y fees.");

        return app;
    }

    // ── GET /api/dashboard/summary ─────────────────────────────────────────

    private static async Task<IResult> GetSummaryAsync(
        [AsParameters] DashboardQueryParams  q,
        IDashboardService                    service,
        ITenantContext                        tenantCtx,
        CancellationToken                    ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var (from, to) = ParseDateRange(q.DateFrom, q.DateTo);

        var result = await service.GetSummaryAsync(tenantId, from, to, ct);
        return Results.Ok(result);
    }

    // ── GET /api/dashboard/message-delivery ────────────────────────────────

    private static async Task<IResult> GetMessageDeliveryAsync(
        [AsParameters] DashboardQueryParams  q,
        IDashboardService                    service,
        ITenantContext                        tenantCtx,
        CancellationToken                    ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var (from, to) = ParseDateRange(q.DateFrom, q.DateTo);

        var result = await service.GetMessageDeliveryAsync(tenantId, from, to, q.FlowId, ct);
        return Results.Ok(result);
    }

    // ── GET /api/dashboard/flow-performance ────────────────────────────────

    private static async Task<IResult> GetFlowPerformanceAsync(
        [AsParameters] DashboardQueryParams  q,
        IDashboardService                    service,
        ITenantContext                        tenantCtx,
        CancellationToken                    ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var (from, to) = ParseDateRange(q.DateFrom, q.DateTo);

        var result = await service.GetFlowPerformanceAsync(tenantId, from, to, q.FlowId, ct);
        return Results.Ok(result);
    }

    // ── GET /api/dashboard/conversations ───────────────────────────────────

    private static async Task<IResult> GetConversationsAsync(
        [AsParameters] ConversationsQueryParams  q,
        IDashboardService                        service,
        ITenantContext                            tenantCtx,
        CancellationToken                        ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var (from, to) = ParseDateRange(q.DateFrom, q.DateTo);

        var result = await service.GetConversationsAsync(
            tenantId, from, to,
            q.FlowId, q.Status, q.RequiresHuman,
            q.Page, q.PageSize, ct);

        return Results.Ok(result);
    }

    // ── GET /api/dashboard/revenue-overview ────────────────────────────────

    private static async Task<IResult> GetRevenueOverviewAsync(
        [AsParameters] DashboardQueryParams  q,
        IDashboardService                    service,
        ITenantContext                        tenantCtx,
        CancellationToken                    ct)
    {
        var tenantId = tenantCtx.RequireTenantId();
        var (from, to) = ParseDateRange(q.DateFrom, q.DateTo);

        var result = await service.GetRevenueOverviewAsync(tenantId, from, to, q.FlowId, ct);
        return Results.Ok(result);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parsea los parámetros de fecha ISO "YYYY-MM-DD".
    /// Si no se proporcionan, usa [hoy - DefaultDays, mañana).
    /// </summary>
    private static (DateTimeOffset from, DateTimeOffset to) ParseDateRange(
        string? dateFrom,
        string? dateTo)
    {
        var utcNow = DateTimeOffset.UtcNow;

        DateTimeOffset from = DateOnly.TryParse(dateFrom, out var parsedFrom)
            ? new DateTimeOffset(parsedFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : utcNow.AddDays(-DefaultDays).Date.ToDateTimeOffset();

        DateTimeOffset to = DateOnly.TryParse(dateTo, out var parsedTo)
            ? new DateTimeOffset(parsedTo.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : utcNow.Date.AddDays(1).ToDateTimeOffset();

        // Protección: from no puede ser posterior a to
        if (from > to) (from, to) = (to, from);

        return (from, to);
    }
}

/// <summary>Extension para conversión de DateTime a DateTimeOffset.</summary>
file static class DateTimeExtensions
{
    public static DateTimeOffset ToDateTimeOffset(this DateTime dt)
        => new(dt, TimeSpan.Zero);
}
