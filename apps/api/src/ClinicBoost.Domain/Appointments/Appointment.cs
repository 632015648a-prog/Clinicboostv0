using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Appointments;

/// <summary>
/// Cita clínica. El backend es el único que puede confirmar/cancelar.
/// La IA propone; el backend ejecuta.
/// 
/// Todas las fechas se almacenan en UTC.
/// La conversión a la timezone del tenant se hace en la capa de presentación.
/// </summary>
public sealed class Appointment : BaseEntity
{
    public Guid PatientId { get; init; }
    public required string TherapistName { get; set; }
    public DateTimeOffset StartsAtUtc { get; set; }     // SIEMPRE UTC
    public DateTimeOffset EndsAtUtc { get; set; }       // SIEMPRE UTC
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public AppointmentSource Source { get; set; } = AppointmentSource.Manual;

    // Recuperación de ingresos
    public bool IsRecovered { get; set; } = false;      // true = vía ClinicBoost
    public decimal? RecoveredRevenue { get; set; }

    // Recordatorios (Flow 03)
    public DateTimeOffset? ReminderSentAt { get; set; }
    public bool NoShow { get; set; } = false;

    // Reprogramación (Flow 07)
    public Guid? RescheduledFromId { get; set; }
}

public enum AppointmentStatus
{
    Scheduled  = 1,
    Confirmed  = 2,
    Cancelled  = 3,
    Completed  = 4,
    NoShow     = 5
}

public enum AppointmentSource
{
    Manual          = 1,   // Creada por el terapeuta
    WhatsApp        = 2,   // Flow 01 — llamada perdida
    GapFill         = 3,   // Flow 02 — hueco detectado
    Reactivation    = 4,   // Flow 06 — paciente inactivo
    Rescheduled     = 5    // Flow 07 — reprogramación
}
