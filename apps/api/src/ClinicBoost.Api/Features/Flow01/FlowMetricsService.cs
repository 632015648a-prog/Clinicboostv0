using ClinicBoost.Api.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// FlowMetricsService
//
// Implementación de IFlowMetricsService sobre EF Core + Postgres.
//
// DISEÑO
// ──────
//  · RecordAsync    → INSERT directo, sin transacción (INSERT-only / fire-and-forget).
//                     Si falla, se logea pero no se propaga.
//  · GetFlow01SummaryAsync → consulta la tabla flow_metrics_events filtrando
//    por tenant, flow_id="flow_01" y el rango de fechas.
//    Los percentiles (p95) se calculan en memoria sobre un máximo de 10.000 filas.
//
// LÓGICA ECONÓMICA (HL-3)
// ────────────────────────
//  · Este servicio solo lee/escribe FlowMetricsEvent.
//  · Los RevenueEvents los persiste Flow01Orchestrator (no este servicio).
//  · GetFlow01SummaryAsync lee RecoveredRevenue desde FlowMetricsEvent
//    (no desde revenue_events) para evitar joins complejos.
//    El dashboard de revenue detallado lee desde revenue_events directamente.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IFlowMetricsService"/> sobre EF Core + Postgres.
/// </summary>
public sealed class FlowMetricsService : IFlowMetricsService
{
    private readonly AppDbContext             _db;
    private readonly ILogger<FlowMetricsService> _logger;

    public FlowMetricsService(AppDbContext db, ILogger<FlowMetricsService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── RecordAsync ───────────────────────────────────────────────────────────

    public async Task RecordAsync(FlowMetricsEvent evt, CancellationToken ct = default)
    {
        try
        {
            _db.FlowMetricsEvents.Add(evt);
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "[Metrics] Registrado. FlowId={Flow} MetricType={Type} " +
                "TenantId={TenantId} DurationMs={Ms} CorrelationId={Corr}",
                evt.FlowId, evt.MetricType, evt.TenantId, evt.DurationMs, evt.CorrelationId);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: los fallos de métricas no deben bloquear el flujo de negocio
            _logger.LogError(ex,
                "[Metrics] Error al registrar métrica. FlowId={Flow} MetricType={Type} " +
                "TenantId={TenantId}",
                evt.FlowId, evt.MetricType, evt.TenantId);
        }
    }

    // ── GetFlow01SummaryAsync ─────────────────────────────────────────────────

    public async Task<Flow01MetricsSummary> GetFlow01SummaryAsync(
        Guid              tenantId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default)
    {
        // Cargamos todos los eventos del rango en memoria para calcular percentiles
        // (máx 10.000 filas por seguridad — para datasets mayores usar window functions en Postgres)
        var events = await _db.FlowMetricsEvents
            .AsNoTracking()
            .Where(e =>
                e.TenantId   == tenantId     &&
                e.FlowId     == "flow_01"    &&
                e.OccurredAt >= from         &&
                e.OccurredAt <  to)
            .Take(10_000)
            .ToListAsync(ct);

        var missed   = events.Count(e => e.MetricType == "missed_call_received");
        var sent     = events.Count(e => e.MetricType == "outbound_sent");
        var failed   = events.Count(e => e.MetricType == "outbound_failed");
        var replies  = events.Count(e => e.MetricType == "patient_replied");
        var booked   = events.Count(e => e.MetricType == "appointment_booked");

        var convRate = sent > 0 ? (double)booked / sent : 0.0;

        // Tiempos de respuesta (llamada → WA enviado)
        var responseTimes = events
            .Where(e => e.MetricType == "outbound_sent" && e.DurationMs.HasValue)
            .Select(e => e.DurationMs!.Value)
            .OrderBy(x => x)
            .ToList();

        var avgMs = responseTimes.Count > 0
            ? responseTimes.Average()
            : 0.0;

        var p95Ms = responseTimes.Count > 0
            ? responseTimes[Math.Min((int)(responseTimes.Count * 0.95), responseTimes.Count - 1)]
            : 0.0;

        // Revenue recuperado (de appointment_booked events)
        var totalRevenue = events
            .Where(e => e.MetricType == "appointment_booked" && e.RecoveredRevenue.HasValue)
            .Sum(e => e.RecoveredRevenue!.Value);

        _logger.LogDebug(
            "[Metrics] Flow01Summary. TenantId={TenantId} From={From} To={To} " +
            "Missed={M} Sent={S} Booked={B} Revenue={R}EUR",
            tenantId, from, to, missed, sent, booked, totalRevenue);

        return new Flow01MetricsSummary
        {
            From                   = from,
            To                     = to,
            MissedCallsReceived    = missed,
            OutboundSent           = sent,
            OutboundFailed         = failed,
            PatientReplies         = replies,
            AppointmentsBooked     = booked,
            ConversionRate         = Math.Round(convRate, 4),
            AvgResponseTimeMs      = Math.Round(avgMs, 1),
            P95ResponseTimeMs      = p95Ms,
            TotalRecoveredRevenue  = totalRevenue,
            Currency               = "EUR",
        };
    }
}
