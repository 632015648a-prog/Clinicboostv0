using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Appointments;

/// <summary>
/// Entrada en la lista de espera inteligente (Flow 02 y 03).
/// Cuando se detecta un hueco, el sistema oferta la cita al paciente
/// con mayor prioridad (menor número = mayor prioridad).
/// </summary>
public sealed class WaitlistEntry : BaseEntity
{
    public Guid PatientId { get; init; }

    // Preferencias del paciente
    public string? PreferredTherapistName { get; set; }
    public string[]? PreferredDays { get; set; }         // ["monday","wednesday"]
    public TimeOnly? PreferredTimeFrom { get; set; }     // hora mínima (hora local del tenant)
    public TimeOnly? PreferredTimeTo { get; set; }
    public string? Notes { get; set; }

    public int Priority { get; set; } = 100;             // menor número = mayor prioridad

    public required string Status { get; set; } = "waiting";
    // waiting | offered | accepted | declined | expired | cancelled

    // Tracking de la oferta activa
    public Guid? OfferedAppointmentId { get; set; }
    public DateTimeOffset? OfferedAt { get; set; }
    public DateTimeOffset? OfferExpiresAt { get; set; }
    public int OfferCount { get; set; } = 0;
}
