using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Appointments;

/// <summary>
/// Evento inmutable del ciclo de vida de una cita (event sourcing ligero).
/// Permite auditoría completa y reconstrucción del estado sin depender
/// de campos mutables en Appointment. Nunca se actualiza ni borra.
/// </summary>
public sealed class AppointmentEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid AppointmentId { get; init; }

    public required string EventType { get; init; }
    // created | confirmed | cancelled | completed | no_show_marked
    // reminder_sent | reschedule_requested | rescheduled | recovered

    public required string ActorType { get; init; }  // system | patient | therapist | admin | ai
    public Guid? ActorId { get; init; }

    /// <summary>
    /// Payload libre del evento. No debe contener PII directa;
    /// usar referencias (IDs) en su lugar.
    /// </summary>
    public string Payload { get; init; } = "{}";     // JSON serializado

    public Guid? CorrelationId { get; init; }
    public string? FlowId { get; init; }              // flow_01, flow_03, etc.

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    // Sin UpdatedAt: inmutable
}
