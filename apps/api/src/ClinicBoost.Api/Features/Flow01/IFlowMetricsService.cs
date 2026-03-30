namespace ClinicBoost.Api.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// IFlowMetricsService
//
// Abstracción para registrar y consultar métricas KPI de los flujos.
//
// PRINCIPIOS
// ──────────
//  · RecordAsync  — INSERT-only; nunca actualiza filas existentes.
//  · GetFlow01SummaryAsync — agrega KPIs en memoria desde la BD.
//  · La lógica de revenue NUNCA viene del frontend ni de prompts de IA.
//    Solo se inserta desde el orquestador / AppointmentService (backend).
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Registra y consulta métricas KPI de flujos.
/// </summary>
public interface IFlowMetricsService
{
    /// <summary>
    /// Registra un evento de métricas de forma fire-and-forget (no lanza excepción).
    /// </summary>
    Task RecordAsync(FlowMetricsEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Devuelve los KPIs agregados de Flow01 para el rango indicado.
    /// </summary>
    Task<Flow01MetricsSummary> GetFlow01SummaryAsync(
        Guid              tenantId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default);
}
