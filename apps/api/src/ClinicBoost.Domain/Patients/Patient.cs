using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Patients;

/// <summary>
/// Paciente de una clínica. Siempre scoped a un tenant.
/// </summary>
public sealed class Patient : BaseEntity
{
    public required string FullName { get; set; }
    public required string Phone { get; set; }           // Normalizado E.164
    public string? Email { get; set; }
    public PatientStatus Status { get; set; } = PatientStatus.Active;

    // RGPD — consentimiento explícito antes de enviar cualquier mensaje
    public bool RgpdConsent { get; set; } = false;
    public DateTimeOffset? RgpdConsentAt { get; set; }

    // Reactivación (Flow 06)
    public DateTimeOffset? LastAppointmentAt { get; set; }
    public int DaysSinceLastVisit =>
        LastAppointmentAt.HasValue
            ? (int)(DateTimeOffset.UtcNow - LastAppointmentAt.Value).TotalDays
            : int.MaxValue;
}

public enum PatientStatus
{
    Active   = 1,
    Inactive = 2,   // Más de N días sin cita (configurable por tenant)
    Blocked  = 3    // No contactar (opt-out)
}
