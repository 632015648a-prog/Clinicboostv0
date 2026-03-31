using ClinicBoost.Domain.Variants;

namespace ClinicBoost.Api.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// IVariantTrackingService
//
// Contrato para el tracking del funnel de conversión por variante A/B.
//
// RESPONSABILIDADES
// ─────────────────
//  · SelectVariantAsync    — elige la variante activa para un mensaje outbound
//                            según los pesos configurados (distribución aleatoria
//                            ponderada). Garantiza que siempre devuelve una
//                            variante aunque solo haya una.
//  · RecordEventAsync      — inserta un VariantConversionEvent (funnel step).
//                            Fire-and-forget: los errores se logean pero nunca
//                            bloquean el flujo de negocio.
//  · GetVariantStatsAsync  — estadísticas del funnel para el endpoint de dashboard.
//
// CONTRATO DE IDEMPOTENCIA
// ────────────────────────
//  · RecordEventAsync no deduplica por sí mismo: el caller debe evitar
//    llamadas duplicadas (los workers ya usan IIdempotencyService para el job).
//  · Para delivered/read/booked la deduplicación la garantiza la regla de
//    no-regresión del MessageStatusService (un solo callback por transición).
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Servicio de tracking de variantes A/B: selección y registro de eventos de conversión.
/// </summary>
public interface IVariantTrackingService
{
    /// <summary>
    /// Selecciona la variante activa para un mensaje outbound según distribución
    /// de pesos configurada. Devuelve null si no hay variantes activas para
    /// el par (flowId, templateId) de este tenant.
    /// </summary>
    /// <param name="tenantId">Tenant que envía el mensaje.</param>
    /// <param name="flowId">Identificador del flujo (flow_01, etc.).</param>
    /// <param name="templateId">ID de la plantilla Twilio o clave interna.</param>
    /// <param name="ct">Token de cancelación.</param>
    Task<MessageVariant?> SelectVariantAsync(
        Guid              tenantId,
        string            flowId,
        string            templateId,
        CancellationToken ct = default);

    /// <summary>
    /// Registra un evento del funnel de conversión para una variante.
    /// Fire-and-forget: los errores se logean pero no se propagan.
    /// </summary>
    /// <param name="evt">Evento a registrar.</param>
    /// <param name="ct">Token de cancelación.</param>
    Task RecordEventAsync(
        VariantConversionEvent evt,
        CancellationToken      ct = default);

    /// <summary>
    /// Devuelve las estadísticas del funnel completo para una variante concreta.
    /// </summary>
    /// <param name="tenantId">Tenant propietario.</param>
    /// <param name="variantId">ID de la variante.</param>
    /// <param name="from">Inicio del rango UTC.</param>
    /// <param name="to">Fin del rango UTC.</param>
    /// <param name="ct">Token de cancelación.</param>
    Task<VariantStats> GetVariantStatsAsync(
        Guid              tenantId,
        Guid              variantId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default);

    /// <summary>
    /// Devuelve el listado de variantes con sus estadísticas de funnel para un
    /// flow+template concreto, para comparación directa entre variantes.
    /// </summary>
    /// <param name="tenantId">Tenant propietario.</param>
    /// <param name="flowId">Flujo a analizar.</param>
    /// <param name="templateId">Plantilla a analizar (null = todos los templates del flow).</param>
    /// <param name="from">Inicio del rango UTC.</param>
    /// <param name="to">Fin del rango UTC.</param>
    /// <param name="ct">Token de cancelación.</param>
    Task<IReadOnlyList<VariantStats>> GetVariantComparisonAsync(
        Guid              tenantId,
        string            flowId,
        string?           templateId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default);
}
