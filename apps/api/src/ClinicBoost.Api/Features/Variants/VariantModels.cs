namespace ClinicBoost.Api.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantModels
//
// DTOs de respuesta para el endpoint de estadísticas de variantes A/B.
// Todos son records inmutables (solo lectura, no se persisten en BD).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Estadísticas del funnel de conversión para una variante A/B concreta.
/// </summary>
public sealed record VariantStats
{
    public Guid           VariantId   { get; init; }
    public Guid           TenantId    { get; init; }
    public string         FlowId      { get; init; } = string.Empty;
    public string         TemplateId  { get; init; } = string.Empty;
    public string         VariantKey  { get; init; } = string.Empty;
    public string?        BodyPreview { get; init; }
    public short          WeightPct   { get; init; }
    public bool           IsActive    { get; init; }

    // ── Rango temporal ────────────────────────────────────────────────────
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To   { get; init; }

    // ── Contadores del funnel ─────────────────────────────────────────────
    public int SentCount      { get; init; }
    public int DeliveredCount { get; init; }
    public int ReadCount      { get; init; }
    public int ReplyCount     { get; init; }
    public int BookedCount    { get; init; }

    // ── Tasas del funnel (0-1) ────────────────────────────────────────────

    /// <summary>delivered / sent</summary>
    public double DeliveryRate { get; init; }

    /// <summary>read / delivered</summary>
    public double ReadRate     { get; init; }

    /// <summary>reply / read</summary>
    public double ReplyRate    { get; init; }

    /// <summary>booked / sent  (tasa de conversión final)</summary>
    public double BookingRate  { get; init; }

    // ── Revenue ───────────────────────────────────────────────────────────
    public decimal TotalRecoveredRevenue { get; init; }
    public string  Currency             { get; init; } = "EUR";

    // ── Tiempos medianos (ms desde outbound_sent) ─────────────────────────

    /// <summary>P50 del tiempo de entrega (outbound_sent → delivered).</summary>
    public double? P50DeliveredMs { get; init; }

    /// <summary>P50 del tiempo de lectura (outbound_sent → read).</summary>
    public double? P50ReadMs      { get; init; }

    /// <summary>P50 del tiempo hasta reserva (outbound_sent → booked).</summary>
    public double? P50BookedMs    { get; init; }

    // ── Factory ───────────────────────────────────────────────────────────
    public static VariantStats Empty(Guid variantId, DateTimeOffset from, DateTimeOffset to)
        => new() { VariantId = variantId, From = from, To = to };
}

/// <summary>
/// Respuesta del endpoint GET /api/variants/{id}/stats.
/// </summary>
public sealed record VariantStatsResponse
{
    public VariantStats Stats  { get; init; } = VariantStats.Empty(Guid.Empty,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

/// <summary>
/// Respuesta del endpoint GET /api/variants/comparison?flowId=&amp;templateId=.
/// </summary>
public sealed record VariantComparisonResponse
{
    public string                     FlowId     { get; init; } = string.Empty;
    public string?                    TemplateId { get; init; }
    public DateTimeOffset             From       { get; init; }
    public DateTimeOffset             To         { get; init; }
    public IReadOnlyList<VariantStats> Variants  { get; init; } =
        Array.Empty<VariantStats>();

    /// <summary>
    /// Variante con mayor tasa de conversión (booked/sent) en el periodo.
    /// Null si no hay datos o solo hay una variante.
    /// </summary>
    public string? WinnerVariantKey { get; init; }
}

/// <summary>
/// Request body para crear una nueva variante.
/// POST /api/variants
/// </summary>
public sealed record CreateVariantRequest
{
    public required string FlowId      { get; init; }
    public required string TemplateId  { get; init; }
    public required string VariantKey  { get; init; }
    public string?         BodyPreview { get; init; }
    public string?         TemplateVars { get; init; }
    public short           WeightPct   { get; init; } = 50;
    public bool            IsActive    { get; init; } = true;
    public string          Metadata    { get; init; } = "{}";
}

/// <summary>
/// Request body para actualizar el peso o estado de una variante.
/// PATCH /api/variants/{id}
/// </summary>
public sealed record UpdateVariantRequest
{
    public short? WeightPct  { get; init; }
    public bool?  IsActive   { get; init; }
    public string? BodyPreview { get; init; }
    public string? Metadata  { get; init; }
}
