using FluentValidation;

namespace ClinicBoost.Api.Features.Appointments;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentValidators.cs
//
// Validadores de FluentValidation para los DTOs de citas.
//
// DISEÑO (Vertical Slice)
// ────────────────────────
//  · Un validador por operación, en el mismo feature slice.
//  · Los validadores NO acceden a la BD (eso es responsabilidad del servicio).
//  · Los errores se devuelven como 400 Bad Request desde el endpoint.
// ════════════════════════════════════════════════════════════════════════════

public sealed class GetAvailableSlotsValidator : AbstractValidator<GetAvailableSlotsRequest>
{
    public GetAvailableSlotsValidator()
    {
        RuleFor(x => x.DateFrom)
            .NotEmpty().WithMessage("DateFrom es obligatorio.")
            .Must(d => DateOnly.TryParse(d, out _))
            .WithMessage("DateFrom debe ser formato YYYY-MM-DD.");

        RuleFor(x => x.DateTo)
            .NotEmpty().WithMessage("DateTo es obligatorio.")
            .Must(d => DateOnly.TryParse(d, out _))
            .WithMessage("DateTo debe ser formato YYYY-MM-DD.")
            .Must((req, dateTo) =>
            {
                if (!DateOnly.TryParse(req.DateFrom, out var from)) return true;
                if (!DateOnly.TryParse(dateTo, out var to)) return true;
                return to >= from;
            })
            .WithMessage("DateTo debe ser igual o posterior a DateFrom.");

        RuleFor(x => x.DurationMinutes)
            .InclusiveBetween(15, 480)
            .WithMessage("DurationMinutes debe estar entre 15 y 480.");
    }
}

public sealed class BookAppointmentValidator : AbstractValidator<BookAppointmentRequest>
{
    public BookAppointmentValidator()
    {
        RuleFor(x => x.PatientId)
            .NotEmpty().WithMessage("PatientId es obligatorio.");

        RuleFor(x => x.TherapistName)
            .NotEmpty().WithMessage("TherapistName es obligatorio.")
            .MaximumLength(200);

        RuleFor(x => x.StartsAtUtc)
            .NotEmpty().WithMessage("StartsAtUtc es obligatorio.")
            .GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5))
            .WithMessage("No se puede reservar una cita en el pasado.");

        RuleFor(x => x.EndsAtUtc)
            .NotEmpty().WithMessage("EndsAtUtc es obligatorio.")
            .GreaterThan(x => x.StartsAtUtc)
            .WithMessage("EndsAtUtc debe ser posterior a StartsAtUtc.");

        RuleFor(x => (x.EndsAtUtc - x.StartsAtUtc).TotalMinutes)
            .InclusiveBetween(15, 480)
            .WithMessage("La duración de la cita debe ser entre 15 y 480 minutos.");

        RuleFor(x => x.DiscountPct)
            .InclusiveBetween(0, 100)
            .When(x => x.DiscountPct.HasValue)
            .WithMessage("DiscountPct debe estar entre 0 y 100.");

        RuleFor(x => x.SessionAmount)
            .GreaterThanOrEqualTo(0)
            .When(x => x.SessionAmount.HasValue)
            .WithMessage("SessionAmount no puede ser negativo.");

        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(128)
            .When(x => x.IdempotencyKey is not null)
            .WithMessage("IdempotencyKey máximo 128 caracteres.");
    }
}

public sealed class CancelAppointmentValidator : AbstractValidator<CancelAppointmentRequest>
{
    private static readonly string[] AllowedActors =
        ["patient", "therapist", "admin", "ai", "system"];

    public CancelAppointmentValidator()
    {
        RuleFor(x => x.AppointmentId)
            .NotEmpty().WithMessage("AppointmentId es obligatorio.");

        RuleFor(x => x.ActorType)
            .Must(a => AllowedActors.Contains(a))
            .WithMessage($"ActorType debe ser uno de: {string.Join(", ", AllowedActors)}.");

        RuleFor(x => x.Reason)
            .MaximumLength(500)
            .When(x => x.Reason is not null)
            .WithMessage("Reason máximo 500 caracteres.");
    }
}

public sealed class RescheduleAppointmentValidator : AbstractValidator<RescheduleAppointmentRequest>
{
    private static readonly string[] AllowedActors =
        ["patient", "therapist", "admin", "ai", "system"];

    public RescheduleAppointmentValidator()
    {
        RuleFor(x => x.AppointmentId)
            .NotEmpty().WithMessage("AppointmentId es obligatorio.");

        RuleFor(x => x.TherapistName)
            .NotEmpty().WithMessage("TherapistName es obligatorio.")
            .MaximumLength(200);

        RuleFor(x => x.NewStartsAtUtc)
            .NotEmpty().WithMessage("NewStartsAtUtc es obligatorio.")
            .GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5))
            .WithMessage("No se puede reprogramar a una hora en el pasado.");

        RuleFor(x => x.NewEndsAtUtc)
            .NotEmpty().WithMessage("NewEndsAtUtc es obligatorio.")
            .GreaterThan(x => x.NewStartsAtUtc)
            .WithMessage("NewEndsAtUtc debe ser posterior a NewStartsAtUtc.");

        RuleFor(x => (x.NewEndsAtUtc - x.NewStartsAtUtc).TotalMinutes)
            .InclusiveBetween(15, 480)
            .WithMessage("La duración de la nueva cita debe ser entre 15 y 480 minutos.");

        RuleFor(x => x.ActorType)
            .Must(a => AllowedActors.Contains(a))
            .WithMessage($"ActorType debe ser uno de: {string.Join(", ", AllowedActors)}.");

        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(128)
            .When(x => x.IdempotencyKey is not null)
            .WithMessage("IdempotencyKey máximo 128 caracteres.");
    }
}
