using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// Flow01Endpoints
//
// Endpoints REST para el flujo flow_01:
//   GET  /api/metrics/flow01       — KPIs agregados del flujo
//
// SEGURIDAD
// ─────────
//  · [Authorize]: requiere JWT válido (Supabase GoTrue).
//  · Solo puede acceder un usuario del mismo tenant (TenantContext).
//  · Las credenciales de Twilio y la lógica de revenue NUNCA salen aquí.
// ════════════════════════════════════════════════════════════════════════════

public static class Flow01Endpoints
{
    public static IEndpointRouteBuilder MapFlow01Endpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/api/metrics")
            .RequireAuthorization()
            .WithTags("Flow01 Metrics");

        // GET /api/metrics/flow01?from=2026-01-01&to=2026-01-31
        group.MapGet("flow01", GetFlow01MetricsAsync)
            .WithName("GetFlow01Metrics")
            .WithSummary("KPIs del flujo flow_01: llamada perdida → WA recovery");

        return app;
    }

    private static async Task<IResult> GetFlow01MetricsAsync(
        [FromQuery] string?  from,
        [FromQuery] string?  to,
        ITenantContext       tenantCtx,
        IFlowMetricsService  metricsService,
        ILogger<Program>     logger,
        CancellationToken    ct)
    {
        if (!tenantCtx.IsInitialized)
            return Results.Unauthorized();

        // Rango por defecto: últimos 30 días
        var dateTo   = to is not null && DateTimeOffset.TryParse(to, out var parsedTo)
            ? parsedTo
            : DateTimeOffset.UtcNow;

        var dateFrom = from is not null && DateTimeOffset.TryParse(from, out var parsedFrom)
            ? parsedFrom
            : dateTo.AddDays(-30);

        if (dateFrom >= dateTo)
            return Results.BadRequest(new
            {
                error = "El parámetro 'from' debe ser anterior a 'to'.",
            });

        // Limitar a máximo 365 días por petición
        if ((dateTo - dateFrom).TotalDays > 365)
            return Results.BadRequest(new
            {
                error = "El rango máximo permitido es 365 días.",
            });

        try
        {
            var summary = await metricsService.GetFlow01SummaryAsync(
                tenantCtx.RequireTenantId(), dateFrom, dateTo, ct);

            return Results.Ok(summary);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[Flow01Endpoints] Error al obtener métricas. TenantId={TenantId}",
                tenantCtx.TenantId);
            return Results.Problem("Error al obtener métricas del flujo.");
        }
    }
}
