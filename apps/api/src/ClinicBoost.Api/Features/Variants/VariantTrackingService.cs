using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Variants;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantTrackingService
//
// Implementación de IVariantTrackingService sobre EF Core + Postgres.
//
// SELECCIÓN DE VARIANTE (SelectVariantAsync)
// ──────────────────────────────────────────
// · Carga las variantes activas para (tenant, flow, template).
// · Aplica distribución aleatoria ponderada con los WeightPct.
//   Algoritmo: genera un número aleatorio [0, sumaPesos) y recorre
//   las variantes sumando pesos hasta superar el número → esa variante.
// · Si la suma de pesos no es exactamente 100 (configuración incorrecta),
//   el algoritmo sigue funcionando correctamente (solo necesita >0).
// · Thread-safe: usa Random.Shared (dotnet 6+).
//
// REGISTRO DE EVENTOS (RecordEventAsync)
// ───────────────────────────────────────
// · INSERT directo, fire-and-forget.
// · Los errores se logean pero NO se propagan al caller.
//
// ESTADÍSTICAS (GetVariantStatsAsync / GetVariantComparisonAsync)
// ───────────────────────────────────────────────────────────────
// · Carga los eventos en memoria (máx 50.000 filas) y agrega en C#.
// · Para volúmenes mayores usar la vista v_variant_conversion_funnel
//   directamente (ejecutar SQL raw desde un job de reporting).
// · Los percentiles (p50) se calculan sobre los elapsed_ms disponibles.
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="IVariantTrackingService"/>.
/// </summary>
public sealed class VariantTrackingService : IVariantTrackingService
{
    private readonly AppDbContext                    _db;
    private readonly ILogger<VariantTrackingService> _logger;

    public VariantTrackingService(
        AppDbContext                    db,
        ILogger<VariantTrackingService> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── SelectVariantAsync ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<MessageVariant?> SelectVariantAsync(
        Guid              tenantId,
        string            flowId,
        string            templateId,
        CancellationToken ct = default)
    {
        var variants = await _db.MessageVariants
            .AsNoTracking()
            .Where(v =>
                v.TenantId   == tenantId   &&
                v.FlowId     == flowId     &&
                v.TemplateId == templateId &&
                v.IsActive)
            .OrderBy(v => v.VariantKey)   // orden determinista para tests
            .ToListAsync(ct);

        if (variants.Count == 0)
        {
            _logger.LogDebug(
                "[VariantTracking] Sin variantes activas para " +
                "TenantId={TenantId} FlowId={Flow} TemplateId={Template}",
                tenantId, flowId, templateId);
            return null;
        }

        // Distribución aleatoria ponderada
        var totalWeight = variants.Sum(v => v.WeightPct);
        if (totalWeight <= 0)
        {
            // Fallback: primera variante si los pesos están mal configurados
            _logger.LogWarning(
                "[VariantTracking] Suma de pesos = 0 para " +
                "TenantId={TenantId} FlowId={Flow} TemplateId={Template}. " +
                "Usando primera variante como fallback.",
                tenantId, flowId, templateId);
            return variants[0];
        }

        var roll = Random.Shared.Next(totalWeight);
        var accumulated = 0;

        foreach (var variant in variants)
        {
            accumulated += variant.WeightPct;
            if (roll < accumulated)
            {
                _logger.LogDebug(
                    "[VariantTracking] Variante seleccionada: {Key} " +
                    "(WeightPct={W} TotalWeight={T} Roll={R}) " +
                    "TenantId={TenantId} FlowId={Flow}",
                    variant.VariantKey, variant.WeightPct, totalWeight, roll,
                    tenantId, flowId);
                return variant;
            }
        }

        // Nunca debería llegar aquí, pero si hay redondeo de ints devolvemos la última
        return variants[^1];
    }

    // ── RecordEventAsync ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task RecordEventAsync(
        VariantConversionEvent evt,
        CancellationToken      ct = default)
    {
        try
        {
            _db.VariantConversionEvents.Add(evt);
            await _db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "[VariantTracking] Evento registrado. EventType={Type} " +
                "VariantId={VarId} MessageId={MsgId} ElapsedMs={Ms} " +
                "TenantId={TenantId} CorrelationId={Corr}",
                evt.EventType, evt.MessageVariantId, evt.MessageId,
                evt.ElapsedMs, evt.TenantId, evt.CorrelationId);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: no bloquear el flujo de negocio por un fallo de métricas
            _logger.LogError(ex,
                "[VariantTracking] Error al registrar evento. EventType={Type} " +
                "VariantId={VarId} TenantId={TenantId}",
                evt.EventType, evt.MessageVariantId, evt.TenantId);
        }
    }

    // ── GetVariantStatsAsync ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<VariantStats> GetVariantStatsAsync(
        Guid              tenantId,
        Guid              variantId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default)
    {
        var variant = await _db.MessageVariants
            .AsNoTracking()
            .FirstOrDefaultAsync(v =>
                v.Id       == variantId &&
                v.TenantId == tenantId,
                ct);

        if (variant is null)
            return VariantStats.Empty(variantId, from, to);

        var events = await _db.VariantConversionEvents
            .AsNoTracking()
            .Where(e =>
                e.TenantId         == tenantId  &&
                e.MessageVariantId == variantId &&
                e.OccurredAt       >= from       &&
                e.OccurredAt       <  to)
            .Take(50_000)
            .ToListAsync(ct);

        return BuildStats(variant, events, from, to);
    }

    // ── GetVariantComparisonAsync ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VariantStats>> GetVariantComparisonAsync(
        Guid              tenantId,
        string            flowId,
        string?           templateId,
        DateTimeOffset    from,
        DateTimeOffset    to,
        CancellationToken ct = default)
    {
        // Cargar variantes del grupo
        var variantsQuery = _db.MessageVariants
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.FlowId == flowId);

        if (!string.IsNullOrEmpty(templateId))
            variantsQuery = variantsQuery.Where(v => v.TemplateId == templateId);

        var variants = await variantsQuery
            .OrderBy(v => v.VariantKey)
            .ToListAsync(ct);

        if (variants.Count == 0)
            return Array.Empty<VariantStats>();

        var variantIds = variants.Select(v => v.Id).ToList();

        // Cargar todos los eventos del grupo de una sola vez
        var events = await _db.VariantConversionEvents
            .AsNoTracking()
            .Where(e =>
                e.TenantId    == tenantId            &&
                variantIds.Contains(e.MessageVariantId) &&
                e.OccurredAt  >= from                &&
                e.OccurredAt  <  to)
            .Take(100_000)
            .ToListAsync(ct);

        // Agrupar eventos por variante y calcular estadísticas
        var eventsByVariant = events.ToLookup(e => e.MessageVariantId);

        return variants
            .Select(v => BuildStats(v, eventsByVariant[v.Id].ToList(), from, to))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VariantStats BuildStats(
        MessageVariant                 variant,
        IReadOnlyList<VariantConversionEvent> events,
        DateTimeOffset                 from,
        DateTimeOffset                 to)
    {
        var sent      = events.Count(e => e.EventType == VariantEventType.OutboundSent);
        var delivered = events.Count(e => e.EventType == VariantEventType.Delivered);
        var read      = events.Count(e => e.EventType == VariantEventType.Read);
        var reply     = events.Count(e => e.EventType == VariantEventType.Reply);
        var booked    = events.Count(e => e.EventType == VariantEventType.Booked);

        var totalRevenue = events
            .Where(e => e.EventType == VariantEventType.Booked && e.RecoveredRevenue.HasValue)
            .Sum(e => e.RecoveredRevenue!.Value);

        // Percentiles p50 de elapsed_ms por etapa
        static double? P50(IEnumerable<long?> values)
        {
            var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(x => x).ToList();
            if (sorted.Count == 0) return null;
            return sorted[sorted.Count / 2];
        }

        var p50Delivered = P50(events
            .Where(e => e.EventType == VariantEventType.Delivered)
            .Select(e => e.ElapsedMs));

        var p50Read = P50(events
            .Where(e => e.EventType == VariantEventType.Read)
            .Select(e => e.ElapsedMs));

        var p50Booked = P50(events
            .Where(e => e.EventType == VariantEventType.Booked)
            .Select(e => e.ElapsedMs));

        return new VariantStats
        {
            VariantId      = variant.Id,
            TenantId       = variant.TenantId,
            FlowId         = variant.FlowId,
            TemplateId     = variant.TemplateId,
            VariantKey     = variant.VariantKey,
            BodyPreview    = variant.BodyPreview,
            WeightPct      = variant.WeightPct,
            IsActive       = variant.IsActive,
            From           = from,
            To             = to,
            SentCount      = sent,
            DeliveredCount = delivered,
            ReadCount      = read,
            ReplyCount     = reply,
            BookedCount    = booked,
            DeliveryRate   = sent   > 0 ? Math.Round((double)delivered / sent,   4) : 0,
            ReadRate       = delivered > 0 ? Math.Round((double)read / delivered, 4) : 0,
            ReplyRate      = read   > 0 ? Math.Round((double)reply   / read,     4) : 0,
            BookingRate    = sent   > 0 ? Math.Round((double)booked  / sent,     4) : 0,
            TotalRecoveredRevenue = totalRevenue,
            Currency              = "EUR",
            P50DeliveredMs = p50Delivered,
            P50ReadMs      = p50Read,
            P50BookedMs    = p50Booked,
        };
    }
}
