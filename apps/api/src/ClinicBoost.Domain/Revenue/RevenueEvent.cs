using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Revenue;

/// <summary>
/// Evento monetario INMUTABLE atribuido a ClinicBoost.
/// Base del dashboard de ROI y del cálculo del success fee (15% primeros 90 días).
/// Una fila = un ingreso recuperado. Nunca se actualiza ni borra.
/// </summary>
public sealed class RevenueEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid? AppointmentId { get; init; }
    public Guid? PatientId { get; init; }

    public required string EventType { get; init; }
    // missed_call_converted | gap_filled | no_show_recovered |
    // waitlist_booked | reactivation_booked | reschedule_saved | lead_converted

    public required string FlowId { get; init; }

    public decimal Amount { get; init; }
    public string Currency { get; init; } = "EUR";

    // DiscountGuard
    public decimal? OriginalAmount { get; init; }
    public decimal? DiscountPct { get; init; }

    /// <summary>TRUE si el evento cae en la ventana de 90 días de activación.</summary>
    public bool IsSuccessFeeEligible { get; init; } = false;
    public decimal? SuccessFeeAmount { get; init; }    // 15% de Amount si eligible

    /// <summary>Metadatos de atribución (fuente, campaña, canal, etc.).</summary>
    public string AttributionData { get; init; } = "{}";  // JSON

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Sin UpdatedAt: inmutable
}
