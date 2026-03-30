using ClinicBoost.Domain.Appointments;

namespace ClinicBoost.Api.Features.Appointments;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentModels.cs
//
// DTOs de REQUEST y RESPONSE para la feature de citas.
//
// DISEÑO (Vertical Slice — no capas genéricas)
// ─────────────────────────────────────────────
//  · Cada operación tiene su propio DTO de entrada y salida.
//  · La lógica de negocio vive en IAppointmentService, no aquí.
//  · Los DTOs no contienen datos calculados (timezone, revenue): el servicio
//    los computa y los incluye en el response.
//
// REGLAS ECONÓMICAS (NUNCA en frontend ni en prompt)
// ───────────────────────────────────────────────────
//  · IsRecovered, RecoveredRevenue y SuccessFeeAmount se calculan SÓLO en
//    AppointmentService basándose en RuleConfig de la BD.
//  · El frontend NO recibe ningún campo que le permita deducir la tarifa.
// ════════════════════════════════════════════════════════════════════════════

// ── Slots disponibles ─────────────────────────────────────────────────────────

public sealed record GetAvailableSlotsRequest
{
    /// <summary>Fecha inicio de búsqueda (inclusive). Formato: YYYY-MM-DD en timezone del tenant.</summary>
    public required string DateFrom { get; init; }

    /// <summary>Fecha fin de búsqueda (inclusive). Formato: YYYY-MM-DD en timezone del tenant.</summary>
    public required string DateTo { get; init; }

    /// <summary>Nombre del terapeuta. Null = cualquier terapeuta disponible.</summary>
    public string? TherapistName { get; init; }

    /// <summary>Duración solicitada en minutos. Default 60.</summary>
    public int DurationMinutes { get; init; } = 60;
}

public sealed record AvailableSlot
{
    public required DateTimeOffset StartsAtUtc   { get; init; }
    public required DateTimeOffset EndsAtUtc     { get; init; }
    public required string         TherapistName { get; init; }
    /// <summary>Representación local en la timezone del tenant (solo para display).</summary>
    public required string         StartsAtLocal { get; init; }
    public required string         EndsAtLocal   { get; init; }
}

public sealed record GetAvailableSlotsResponse
{
    public required IReadOnlyList<AvailableSlot> Slots     { get; init; }
    public required string                        TimeZone  { get; init; }
    public required int                           Count     { get; init; }
}

// ── Reserva de cita ───────────────────────────────────────────────────────────

public sealed record BookAppointmentRequest
{
    public required Guid             PatientId      { get; init; }
    public required string           TherapistName  { get; init; }
    /// <summary>Inicio en UTC ISO-8601. El backend valida contra la BD; nunca confiar en el cliente.</summary>
    public required DateTimeOffset   StartsAtUtc    { get; init; }
    public required DateTimeOffset   EndsAtUtc      { get; init; }
    public          AppointmentSource Source        { get; init; } = AppointmentSource.WhatsApp;
    /// <summary>Flow que origina la reserva. Necesario para telemetría de revenue.</summary>
    public          string?          FlowId         { get; init; }
    /// <summary>Monto en EUR de la sesión. Necesario para calcular revenue recovery y success fee.
    /// NUNCA viene del frontend en producción; se lee de RuleConfig o calendário.</summary>
    public          decimal?         SessionAmount  { get; init; }
    /// <summary>Porcentaje de descuento aplicado. Validado contra RuleConfig.discount_max_pct.</summary>
    public          decimal?         DiscountPct    { get; init; }
    /// <summary>ID de idempotencia del cliente para evitar doble booking.</summary>
    public          string?          IdempotencyKey { get; init; }
}

public sealed record BookAppointmentResponse
{
    public required Guid             AppointmentId  { get; init; }
    public required string           Status         { get; init; }  // scheduled | conflict
    public required DateTimeOffset   StartsAtUtc    { get; init; }
    public required DateTimeOffset   EndsAtUtc      { get; init; }
    public required string           TherapistName  { get; init; }
    public required string           StartsAtLocal  { get; init; }
    /// <summary>True si se creó un RevenueEvent para esta reserva.</summary>
    public          bool             RevenueTracked { get; init; }
}

// ── Cancelación ───────────────────────────────────────────────────────────────

public sealed record CancelAppointmentRequest
{
    public required Guid   AppointmentId { get; init; }
    /// <summary>Motivo de cancelación. Se persiste en AppointmentEvent.Payload.</summary>
    public          string? Reason       { get; init; }
    /// <summary>Actor que cancela. Default 'patient'. Validado: patient|therapist|admin|ai|system.</summary>
    public          string  ActorType    { get; init; } = "patient";
    public          string? FlowId       { get; init; }
}

public sealed record CancelAppointmentResponse
{
    public required Guid             AppointmentId { get; init; }
    public required string           Status        { get; init; }  // cancelled
    public required DateTimeOffset   CancelledAtUtc { get; init; }
}

// ── Reprogramación ────────────────────────────────────────────────────────────

public sealed record RescheduleAppointmentRequest
{
    public required Guid             AppointmentId  { get; init; }
    public required string           TherapistName  { get; init; }
    public required DateTimeOffset   NewStartsAtUtc { get; init; }
    public required DateTimeOffset   NewEndsAtUtc   { get; init; }
    public          string?          Reason         { get; init; }
    public          string           ActorType      { get; init; } = "patient";
    public          string?          FlowId         { get; init; }
    /// <summary>ID de idempotencia del cliente para evitar doble reprogramación.</summary>
    public          string?          IdempotencyKey { get; init; }
}

public sealed record RescheduleAppointmentResponse
{
    public required Guid             NewAppointmentId  { get; init; }
    public required Guid             OldAppointmentId  { get; init; }
    public required string           Status            { get; init; }  // scheduled | conflict
    public required DateTimeOffset   StartsAtUtc       { get; init; }
    public required DateTimeOffset   EndsAtUtc         { get; init; }
    public required string           TherapistName     { get; init; }
    public required string           StartsAtLocal     { get; init; }
}

// ── Errores de dominio ────────────────────────────────────────────────────────

public sealed record AppointmentError
{
    public required string Code    { get; init; }
    public required string Message { get; init; }

    public static AppointmentError SlotConflict(DateTimeOffset at)
        => new() { Code = "SLOT_CONFLICT",   Message = $"El slot {at:u} ya está ocupado." };

    public static AppointmentError NotFound(Guid id)
        => new() { Code = "NOT_FOUND",       Message = $"Cita {id} no encontrada." };

    public static AppointmentError InvalidStatus(string current, string required)
        => new() { Code = "INVALID_STATUS",  Message = $"Estado actual '{current}' no permite '{required}'." };

    public static AppointmentError DiscountExceeded(decimal requested, decimal maxAllowed)
        => new() { Code = "DISCOUNT_EXCEEDED", Message = $"Descuento {requested}% supera el máximo permitido {maxAllowed}%." };

    public static AppointmentError InvalidActor()
        => new() { Code = "INVALID_ACTOR",   Message = "ActorType debe ser patient|therapist|admin|ai|system." };
}
