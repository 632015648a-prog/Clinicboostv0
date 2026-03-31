using System.Data;
using System.Text.Json;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Revenue;
using Microsoft.EntityFrameworkCore;
using TimeZoneConverter;

namespace ClinicBoost.Api.Features.Appointments;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentService
//
// ARQUITECTURA (Vertical Slice)
// ─────────────────────────────
//  · Un servicio por feature. No hay ApplicationService genérico.
//  · Accede directamente a AppDbContext (ADR-002).
//  · Transacciones explícitas con IsolationLevel.RepeatableRead.
//
// TIMEZONE
// ────────
//  · Todas las fechas se almacenan en UTC.
//  · La conversión local usa TimeZoneConverter (TZConvert) con la IANA tz del tenant.
//  · NUNCA AddHours(1) ni AddHours(2) hardcoded para España.
//
// RACE CONDITIONS
// ───────────────
//  · BookAppointment y RescheduleAppointment usan BeginTransactionAsync con
//    IsolationLevel.RepeatableRead + re-query dentro de la transacción para
//    detectar overlaps. El lock real en Postgres lo da el nivel de aislamiento.
//
// IDEMPOTENCIA
// ────────────
//  · Se usa IIdempotencyService.TryProcessAsync antes de abrir la transacción.
//  · Si AlreadyProcessed → recuperar la cita existente y devolver sin re-insertar.
//
// REVENUE TELEMETRY (nunca en frontend ni en prompts)
// ────────────────────────────────────────────────────
//  · IsRecovered = true cuando Source != Manual.
//  · success_fee = configurable vía RuleConfig global/success_fee_pct (default 15%).
//  · La lógica de descuento se valida contra RuleConfig global/discount_max_pct.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Servicio de citas. Registrar como <b>Scoped</b>.
/// </summary>
public sealed class AppointmentService : IAppointmentService
{
    private readonly AppDbContext               _db;
    private readonly IIdempotencyService        _idempotency;
    private readonly ILogger<AppointmentService> _logger;

    private static readonly HashSet<string> ValidActors =
        ["patient", "therapist", "admin", "ai", "system"];

    // N-P2-04: DefaultSuccessFeePct eliminada (era dead code).
    // El porcentaje real se lee siempre de RuleConfig global/success_fee_pct
    // mediante GetSuccessFeePctAsync (default 0.15 solo como fallback interno de ese helper).

    public AppointmentService(
        AppDbContext                db,
        IIdempotencyService         idempotency,
        ILogger<AppointmentService> logger)
    {
        _db          = db;
        _idempotency = idempotency;
        _logger      = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // GetAvailableSlots
    // ══════════════════════════════════════════════════════════════════════

    public async Task<GetAvailableSlotsResponse> GetAvailableSlotsAsync(
        Guid                     tenantId,
        GetAvailableSlotsRequest request,
        CancellationToken        ct = default)
    {
        var tzId  = await GetTenantTimeZoneAsync(tenantId, ct);
        var tz    = TZConvert.GetTimeZoneInfo(tzId);
        var now   = DateTimeOffset.UtcNow;

        // Parsear fechas locales del request a UTC
        if (!DateOnly.TryParse(request.DateFrom, out var dateFrom))
            dateFrom = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!DateOnly.TryParse(request.DateTo, out var dateTo))
            dateTo = dateFrom.AddDays(7);

        var startUtcDt = TimeZoneInfo.ConvertTimeToUtc(
            dateFrom.ToDateTime(TimeOnly.MinValue), tz);
        var endUtcDt   = TimeZoneInfo.ConvertTimeToUtc(
            dateTo.ToDateTime(new TimeOnly(23, 59, 59)), tz);

        var startUtc = new DateTimeOffset(startUtcDt, TimeSpan.Zero);
        var endUtc   = new DateTimeOffset(endUtcDt, TimeSpan.Zero);

        if (startUtc < now) startUtc = now;

        var durationMin = Math.Max(15, Math.Min(480, request.DurationMinutes));

        // Obtener citas existentes en el rango para calcular conflictos
        var booked = await _db.Appointments
            .Where(a =>
                a.TenantId    == tenantId &&
                a.StartsAtUtc <  endUtc   &&
                a.EndsAtUtc   >  startUtc &&
                a.Status != AppointmentStatus.Cancelled &&
                a.Status != AppointmentStatus.Completed &&
                (request.TherapistName == null || a.TherapistName == request.TherapistName))
            .Select(a => new { a.StartsAtUtc, a.EndsAtUtc, a.TherapistName })
            .OrderBy(a => a.StartsAtUtc)
            .ToListAsync(ct);

        // Terapeutas únicos conocidos del tenant
        var therapists = await _db.Appointments
            .Where(a => a.TenantId == tenantId &&
                        a.Status   != AppointmentStatus.Cancelled &&
                        (request.TherapistName == null || a.TherapistName == request.TherapistName))
            .Select(a => a.TherapistName)
            .Distinct()
            .Take(20)
            .ToListAsync(ct);

        if (therapists.Count == 0)
            therapists = ["Disponible"];

        // Generar slots en horario laboral (UTC, con conversión desde timezone del tenant)
        // P1: horarios leídos de RuleConfig para flexibilidad por tenant.
        var (workStartH, workEndWeekdayH, workEndSaturdayH) =
            await GetWorkHoursAsync(tenantId, ct);

        var slots = new List<AvailableSlot>();

        for (var day = dateFrom; day <= dateTo && slots.Count < 20; day = day.AddDays(1))
        {
            var dayOfWeek = day.DayOfWeek;
            if (dayOfWeek == DayOfWeek.Sunday) continue;

            // Sábados y L-V: horas laborables configurables por tenant (RuleConfig)
            var workEnd = dayOfWeek == DayOfWeek.Saturday
                ? new TimeOnly(workEndSaturdayH, 0)
                : new TimeOnly(workEndWeekdayH, 0);

            foreach (var therapist in therapists)
            {
                for (var time = new TimeOnly(workStartH, 0);
                     time.Add(TimeSpan.FromMinutes(durationMin)) <= workEnd;
                     time = time.Add(TimeSpan.FromMinutes(durationMin)))
                {
                    var slotStartDt = TimeZoneInfo.ConvertTimeToUtc(
                        day.ToDateTime(time), tz);
                    var slotStart   = new DateTimeOffset(slotStartDt, TimeSpan.Zero);
                    var slotEnd     = slotStart.AddMinutes(durationMin);

                    if (slotStart < now) continue;

                    var hasConflict = booked.Any(b =>
                        b.TherapistName == therapist &&
                        b.StartsAtUtc   <  slotEnd   &&
                        b.EndsAtUtc     >  slotStart);

                    if (!hasConflict)
                    {
                        var localStart = TimeZoneInfo.ConvertTimeFromUtc(
                            slotStart.UtcDateTime, tz);
                        var localEnd   = TimeZoneInfo.ConvertTimeFromUtc(
                            slotEnd.UtcDateTime, tz);

                        slots.Add(new AvailableSlot
                        {
                            StartsAtUtc   = slotStart,
                            EndsAtUtc     = slotEnd,
                            TherapistName = therapist,
                            StartsAtLocal = localStart.ToString("dd/MM/yyyy HH:mm"),
                            EndsAtLocal   = localEnd.ToString("HH:mm"),
                        });

                        if (slots.Count >= 20) break;
                    }
                }
                if (slots.Count >= 20) break;
            }
        }

        _logger.LogInformation(
            "[AppointmentService] GetAvailableSlots: {Count} slots. TenantId={TenantId} " +
            "Rango={From}→{To} TZ={Tz}",
            slots.Count, tenantId, dateFrom, dateTo, tzId);

        return new GetAvailableSlotsResponse
        {
            Slots    = slots,
            TimeZone = tzId,
            Count    = slots.Count,
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // BookAppointment
    // ══════════════════════════════════════════════════════════════════════

    public async Task<(BookAppointmentResponse? Response, AppointmentError? Error)>
        BookAppointmentAsync(
            Guid                   tenantId,
            BookAppointmentRequest request,
            CancellationToken      ct = default)
    {
        // ── HL-2: Validar descuento contra RuleConfig (solo en backend) ─────────
        var discountError = await ValidateDiscountAsync(tenantId, request.DiscountPct, ct);
        if (discountError is not null) return (null, discountError);

        // ── Idempotencia: usar TryProcessAsync con el key del cliente ───────────
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var idempResult = await _idempotency.TryProcessAsync(
                eventType: "appointment.book",
                eventId:   request.IdempotencyKey,
                tenantId:  tenantId,
                ct:        ct);

            if (idempResult.AlreadyProcessed)
            {
                _logger.LogInformation(
                    "[AppointmentService] BookAppointment idempotente. " +
                    "Key={Key} TenantId={TenantId}",
                    request.IdempotencyKey, tenantId);

                // Recuperar la cita más reciente del paciente en ese slot (heurística segura)
                var existing = await _db.Appointments
                    .Where(a =>
                        a.TenantId    == tenantId            &&
                        a.PatientId   == request.PatientId   &&
                        a.StartsAtUtc == request.StartsAtUtc &&
                        a.Status != AppointmentStatus.Cancelled)
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync(ct);

                if (existing is not null)
                    return (await BuildBookResponseAsync(existing, tenantId, ct), null);
            }
        }

        // ── Transacción con control de race condition ─────────────────────────
        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);

        try
        {
            // Re-query dentro de la transacción para detectar overlaps
            var hasConflict = await _db.Appointments
                .Where(a =>
                    a.TenantId      == tenantId               &&
                    a.TherapistName == request.TherapistName  &&
                    a.StartsAtUtc   <  request.EndsAtUtc      &&
                    a.EndsAtUtc     >  request.StartsAtUtc    &&
                    a.Status != AppointmentStatus.Cancelled   &&
                    a.Status != AppointmentStatus.Completed)
                .AnyAsync(ct);

            if (hasConflict)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    "[AppointmentService] SlotConflict. Therapist={T} Start={S} TenantId={Id}",
                    request.TherapistName, request.StartsAtUtc, tenantId);
                return (null, AppointmentError.SlotConflict(request.StartsAtUtc));
            }

            var actorType = request.Source == AppointmentSource.Manual ? "therapist" : "ai";

            // ── INSERT Appointment ─────────────────────────────────────────────
            var appointment = new Appointment
            {
                TenantId         = tenantId,
                PatientId        = request.PatientId,
                TherapistName    = request.TherapistName,
                StartsAtUtc      = request.StartsAtUtc,
                EndsAtUtc        = request.EndsAtUtc,
                Status           = AppointmentStatus.Scheduled,
                Source           = request.Source,
                IsRecovered      = request.Source != AppointmentSource.Manual,
                RecoveredRevenue = request.SessionAmount,
            };
            _db.Appointments.Add(appointment);

            // ── INSERT AppointmentEvent (created) ──────────────────────────────
            _db.AppointmentEvents.Add(new AppointmentEvent
            {
                TenantId      = tenantId,
                AppointmentId = appointment.Id,
                EventType     = "created",
                ActorType     = actorType,
                FlowId        = request.FlowId,
                Payload       = JsonSerializer.Serialize(new
                {
                    source        = request.Source.ToString().ToLowerInvariant(),
                    therapist     = request.TherapistName,
                    starts_at_utc = request.StartsAtUtc,
                    ends_at_utc   = request.EndsAtUtc,
                    flow_id       = request.FlowId,
                }),
            });

            // ── Revenue telemetry (HL-3: solo en backend, nunca en prompt/frontend) ─
            var revenueTracked = false;
            if (appointment.IsRecovered &&
                request.SessionAmount.HasValue &&
                request.SessionAmount > 0)
            {
                var successFeePct = await GetSuccessFeePctAsync(tenantId, ct);
                var finalAmount   = request.SessionAmount.Value;

                if (request.DiscountPct.HasValue && request.DiscountPct > 0)
                    finalAmount = Math.Round(finalAmount * (1 - request.DiscountPct.Value / 100), 2);

                var successFee = Math.Round(finalAmount * successFeePct, 2);

                _db.RevenueEvents.Add(new RevenueEvent
                {
                    TenantId             = tenantId,
                    AppointmentId        = appointment.Id,
                    PatientId            = request.PatientId,
                    EventType            = MapSourceToRevenueEventType(request.Source),
                    FlowId               = request.FlowId ?? "flow_00",
                    Amount               = finalAmount,
                    OriginalAmount       = request.SessionAmount,
                    DiscountPct          = request.DiscountPct,
                    Currency             = "EUR",
                    IsSuccessFeeEligible = true,
                    SuccessFeeAmount     = successFee,
                    AttributionData      = JsonSerializer.Serialize(new
                    {
                        source    = request.Source.ToString().ToLowerInvariant(),
                        flow_id   = request.FlowId,
                        channel   = "whatsapp",
                        booked_at = DateTimeOffset.UtcNow,
                    }),
                });
                revenueTracked = true;

                _logger.LogInformation(
                    "[AppointmentService] RevenueEvent INSERT. EventType={Et} " +
                    "Amount={A} EUR SuccessFee={Sf} TenantId={T}",
                    MapSourceToRevenueEventType(request.Source), finalAmount, successFee, tenantId);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "[AppointmentService] Cita creada. AppointmentId={Id} PatientId={P} " +
                "Therapist={T} StartsAt={S} RevenueTracked={R} TenantId={Tenant}",
                appointment.Id, request.PatientId, request.TherapistName,
                request.StartsAtUtc, revenueTracked, tenantId);

            return (await BuildBookResponseAsync(appointment, tenantId, ct,
                revenueTracked: revenueTracked), null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex,
                "[AppointmentService] Error en BookAppointment. TenantId={TenantId}", tenantId);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // CancelAppointment
    // ══════════════════════════════════════════════════════════════════════

    public async Task<(CancelAppointmentResponse? Response, AppointmentError? Error)>
        CancelAppointmentAsync(
            Guid                     tenantId,
            CancelAppointmentRequest request,
            CancellationToken        ct = default)
    {
        if (!ValidActors.Contains(request.ActorType))
            return (null, AppointmentError.InvalidActor());

        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);

        try
        {
            // Cargar dentro de la transacción (semántica FOR UPDATE con RepeatableRead)
            var appointment = await _db.Appointments
                .Where(a => a.Id == request.AppointmentId && a.TenantId == tenantId)
                .FirstOrDefaultAsync(ct);

            if (appointment is null)
            {
                await tx.RollbackAsync(ct);
                return (null, AppointmentError.NotFound(request.AppointmentId));
            }

            if (appointment.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed))
            {
                await tx.RollbackAsync(ct);
                return (null, AppointmentError.InvalidStatus(
                    appointment.Status.ToString(), "cancelled"));
            }

            var wasRecovered        = appointment.IsRecovered;
            var prevStatus          = appointment.Status.ToString().ToLowerInvariant();
            appointment.Status = AppointmentStatus.Cancelled;

            _db.AppointmentEvents.Add(new AppointmentEvent
            {
                TenantId      = tenantId,
                AppointmentId = appointment.Id,
                EventType     = "cancelled",
                ActorType     = request.ActorType,
                FlowId        = request.FlowId,
                Payload       = JsonSerializer.Serialize(new
                {
                    reason          = request.Reason,
                    actor_type      = request.ActorType,
                    flow_id         = request.FlowId,
                    previous_status = prevStatus,
                }),
            });

            // Revenue: registrar pérdida si la cita era recovered
            if (wasRecovered &&
                appointment.RecoveredRevenue.HasValue &&
                appointment.RecoveredRevenue > 0)
            {
                _db.RevenueEvents.Add(new RevenueEvent
                {
                    TenantId             = tenantId,
                    AppointmentId        = appointment.Id,
                    PatientId            = appointment.PatientId,
                    EventType            = "cancellation_loss",
                    FlowId               = request.FlowId ?? "flow_00",
                    Amount               = -appointment.RecoveredRevenue.Value,   // negativo = pérdida
                    OriginalAmount       = appointment.RecoveredRevenue,
                    Currency             = "EUR",
                    IsSuccessFeeEligible = false,
                    AttributionData      = JsonSerializer.Serialize(new
                    {
                        reason       = request.Reason,
                        actor_type   = request.ActorType,
                        flow_id      = request.FlowId,
                        cancelled_at = DateTimeOffset.UtcNow,
                    }),
                });

                _logger.LogInformation(
                    "[AppointmentService] CancellationLoss Revenue. AppointmentId={Id} " +
                    "Revenue={R} TenantId={T}",
                    appointment.Id, appointment.RecoveredRevenue, tenantId);
            }

            var cancelledAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "[AppointmentService] Cita cancelada. AppointmentId={Id} Actor={A} " +
                "TenantId={T}",
                appointment.Id, request.ActorType, tenantId);

            return (new CancelAppointmentResponse
            {
                AppointmentId  = appointment.Id,
                Status         = "cancelled",
                CancelledAtUtc = cancelledAt,
            }, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex,
                "[AppointmentService] Error en CancelAppointment. TenantId={TenantId}", tenantId);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // RescheduleAppointment
    // ══════════════════════════════════════════════════════════════════════

    public async Task<(RescheduleAppointmentResponse? Response, AppointmentError? Error)>
        RescheduleAppointmentAsync(
            Guid                         tenantId,
            RescheduleAppointmentRequest request,
            CancellationToken            ct = default)
    {
        if (!ValidActors.Contains(request.ActorType))
            return (null, AppointmentError.InvalidActor());

        // ── Idempotencia ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var idempResult = await _idempotency.TryProcessAsync(
                eventType: "appointment.reschedule",
                eventId:   request.IdempotencyKey,
                tenantId:  tenantId,
                ct:        ct);

            if (idempResult.AlreadyProcessed)
            {
                _logger.LogInformation(
                    "[AppointmentService] Reschedule idempotente. Key={Key}",
                    request.IdempotencyKey);

                // Recuperar la nueva cita (rescheduled_from_id = original)
                var existing = await _db.Appointments
                    .Where(a =>
                        a.TenantId          == tenantId                 &&
                        a.RescheduledFromId == request.AppointmentId    &&
                        a.Status            != AppointmentStatus.Cancelled)
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync(ct);

                if (existing is not null)
                    return (await BuildRescheduleResponseAsync(
                        existing, request.AppointmentId, tenantId, ct), null);
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);

        try
        {
            // ── Cargar cita original ───────────────────────────────────────────
            var original = await _db.Appointments
                .Where(a => a.Id == request.AppointmentId && a.TenantId == tenantId)
                .FirstOrDefaultAsync(ct);

            if (original is null)
            {
                await tx.RollbackAsync(ct);
                return (null, AppointmentError.NotFound(request.AppointmentId));
            }

            if (original.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed))
            {
                await tx.RollbackAsync(ct);
                return (null, AppointmentError.InvalidStatus(
                    original.Status.ToString(), "rescheduled"));
            }

            // ── Detectar conflict en el nuevo slot ─────────────────────────────
            var hasConflict = await _db.Appointments
                .Where(a =>
                    a.TenantId      == tenantId                  &&
                    a.TherapistName == request.TherapistName     &&
                    a.Id            != request.AppointmentId     &&
                    a.StartsAtUtc   <  request.NewEndsAtUtc      &&
                    a.EndsAtUtc     >  request.NewStartsAtUtc    &&
                    a.Status != AppointmentStatus.Cancelled      &&
                    a.Status != AppointmentStatus.Completed)
                .AnyAsync(ct);

            if (hasConflict)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    "[AppointmentService] SlotConflict en Reschedule. " +
                    "Therapist={T} NewStart={S} TenantId={Id}",
                    request.TherapistName, request.NewStartsAtUtc, tenantId);
                return (null, AppointmentError.SlotConflict(request.NewStartsAtUtc));
            }

            // ── Cancelar cita original ─────────────────────────────────────────
            original.Status = AppointmentStatus.Cancelled;
            _db.AppointmentEvents.Add(new AppointmentEvent
            {
                TenantId      = tenantId,
                AppointmentId = original.Id,
                EventType     = "rescheduled_out",
                ActorType     = request.ActorType,
                FlowId        = request.FlowId,
                Payload       = JsonSerializer.Serialize(new
                {
                    reason        = request.Reason,
                    new_starts_at = request.NewStartsAtUtc,
                    new_therapist = request.TherapistName,
                }),
            });

            // ── Crear nueva cita ───────────────────────────────────────────────
            var newAppointment = new Appointment
            {
                TenantId          = tenantId,
                PatientId         = original.PatientId,
                TherapistName     = request.TherapistName,
                StartsAtUtc       = request.NewStartsAtUtc,
                EndsAtUtc         = request.NewEndsAtUtc,
                Status            = AppointmentStatus.Scheduled,
                Source            = AppointmentSource.Rescheduled,
                RescheduledFromId = original.Id,
                IsRecovered       = original.IsRecovered,
                RecoveredRevenue  = original.RecoveredRevenue,
            };
            _db.Appointments.Add(newAppointment);

            _db.AppointmentEvents.Add(new AppointmentEvent
            {
                TenantId      = tenantId,
                AppointmentId = newAppointment.Id,
                EventType     = "created",
                ActorType     = request.ActorType,
                FlowId        = request.FlowId,
                Payload       = JsonSerializer.Serialize(new
                {
                    rescheduled_from_id = original.Id,
                    therapist           = request.TherapistName,
                    starts_at_utc       = request.NewStartsAtUtc,
                    ends_at_utc         = request.NewEndsAtUtc,
                    reason              = request.Reason,
                }),
            });

            // ── Revenue: reschedule_saved ──────────────────────────────────────
            if (original.IsRecovered &&
                original.RecoveredRevenue.HasValue &&
                original.RecoveredRevenue > 0)
            {
                var successFeePct = await GetSuccessFeePctAsync(tenantId, ct);
                var successFee    = Math.Round(original.RecoveredRevenue.Value * successFeePct, 2);

                _db.RevenueEvents.Add(new RevenueEvent
                {
                    TenantId             = tenantId,
                    AppointmentId        = newAppointment.Id,
                    PatientId            = original.PatientId,
                    EventType            = "reschedule_saved",
                    FlowId               = request.FlowId ?? "flow_00",
                    Amount               = original.RecoveredRevenue.Value,
                    OriginalAmount       = original.RecoveredRevenue,
                    Currency             = "EUR",
                    IsSuccessFeeEligible = true,
                    SuccessFeeAmount     = successFee,
                    AttributionData      = JsonSerializer.Serialize(new
                    {
                        original_appointment_id = original.Id,
                        reason         = request.Reason,
                        actor_type     = request.ActorType,
                        flow_id        = request.FlowId,
                        rescheduled_at = DateTimeOffset.UtcNow,
                    }),
                });

                _logger.LogInformation(
                    "[AppointmentService] RescheduleSaved Revenue. OldId={Old} NewId={New} " +
                    "TenantId={T}",
                    original.Id, newAppointment.Id, tenantId);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // ── Marcar idempotencia ────────────────────────────────────────────
            // (lo hacemos DESPUÉS del commit para no romper la transacción)
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                // Ya fue marcado por TryProcessAsync antes de la transacción.
                // No hay que duplicar; el Metadata del ProcessedEvent no existe en
                // el contrato actual. El lookup idempotente usa PatientId+StartsAtUtc.
            }

            _logger.LogInformation(
                "[AppointmentService] Cita reprogramada. OldId={Old} NewId={New} " +
                "TenantId={T}",
                original.Id, newAppointment.Id, tenantId);

            return (await BuildRescheduleResponseAsync(
                newAppointment, original.Id, tenantId, ct), null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex,
                "[AppointmentService] Error en RescheduleAppointment. TenantId={TenantId}",
                tenantId);
            throw;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers privados
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lee los límites de horario laboral desde RuleConfig del tenant.
    /// Claves: work_start_hour (default 9), work_end_weekday_hour (default 18),
    ///         work_end_saturday_hour (default 14).
    /// Permite personalizar la clínica sin redeployar código.
    /// </summary>
    private async Task<(int StartH, int EndWeekdayH, int EndSaturdayH)>
        GetWorkHoursAsync(Guid tenantId, CancellationToken ct)
    {
        var rules = await _db.RuleConfigs
            .AsNoTracking()
            .Where(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "global" &&
                r.IsActive  &&
                (r.RuleKey == "work_start_hour"        ||
                 r.RuleKey == "work_end_weekday_hour"   ||
                 r.RuleKey == "work_end_saturday_hour"))
            .Select(r => new { r.RuleKey, r.RuleValue })
            .ToListAsync(ct);

        int Get(string key, int defaultVal)
        {
            var v = rules.FirstOrDefault(r => r.RuleKey == key)?.RuleValue;
            return v is not null && int.TryParse(v, out var h) && h is >= 0 and <= 23 ? h : defaultVal;
        }

        return (Get("work_start_hour", 9),
                Get("work_end_weekday_hour", 18),
                Get("work_end_saturday_hour", 14));
    }

    private async Task<string> GetTenantTimeZoneAsync(Guid tenantId, CancellationToken ct)    {
        var tz = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.TimeZone)
            .FirstOrDefaultAsync(ct);
        return tz ?? "Europe/Madrid";
    }

    private async Task<decimal> GetSuccessFeePctAsync(Guid tenantId, CancellationToken ct)
    {
        // Fallback explícito si el tenant no tiene RuleConfig configurada.
        const decimal DefaultFee = 0.15m;

        var rule = await _db.RuleConfigs
            .Where(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "global"  &&
                r.RuleKey  == "success_fee_pct" &&
                r.IsActive)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);

        if (rule is not null && decimal.TryParse(rule, out var pct))
            return pct / 100m;

        return DefaultFee;
    }

    private async Task<AppointmentError?> ValidateDiscountAsync(
        Guid              tenantId,
        decimal?          discountPct,
        CancellationToken ct)
    {
        if (!discountPct.HasValue || discountPct <= 0) return null;

        var maxRule = await _db.RuleConfigs
            .Where(r =>
                r.TenantId == tenantId &&
                r.FlowId   == "global"  &&
                r.RuleKey  == "discount_max_pct" &&
                r.IsActive)
            .Select(r => r.RuleValue)
            .FirstOrDefaultAsync(ct);

        decimal maxPct = 0;
        if (maxRule is not null) decimal.TryParse(maxRule, out maxPct);

        if (discountPct.Value > maxPct)
            return AppointmentError.DiscountExceeded(discountPct.Value, maxPct);

        return null;
    }

    private async Task<BookAppointmentResponse> BuildBookResponseAsync(
        Appointment       appointment,
        Guid              tenantId,
        CancellationToken ct,
        bool              revenueTracked = false)
    {
        var tzId  = await GetTenantTimeZoneAsync(tenantId, ct);
        var tz    = TZConvert.GetTimeZoneInfo(tzId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(appointment.StartsAtUtc.UtcDateTime, tz);

        return new BookAppointmentResponse
        {
            AppointmentId  = appointment.Id,
            Status         = appointment.Status.ToString().ToLowerInvariant(),
            StartsAtUtc    = appointment.StartsAtUtc,
            EndsAtUtc      = appointment.EndsAtUtc,
            TherapistName  = appointment.TherapistName,
            StartsAtLocal  = local.ToString("dd/MM/yyyy HH:mm"),
            RevenueTracked = revenueTracked,
        };
    }

    private async Task<RescheduleAppointmentResponse> BuildRescheduleResponseAsync(
        Appointment       newAppointment,
        Guid              oldAppointmentId,
        Guid              tenantId,
        CancellationToken ct)
    {
        var tzId  = await GetTenantTimeZoneAsync(tenantId, ct);
        var tz    = TZConvert.GetTimeZoneInfo(tzId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(newAppointment.StartsAtUtc.UtcDateTime, tz);

        return new RescheduleAppointmentResponse
        {
            NewAppointmentId = newAppointment.Id,
            OldAppointmentId = oldAppointmentId,
            Status           = newAppointment.Status.ToString().ToLowerInvariant(),
            StartsAtUtc      = newAppointment.StartsAtUtc,
            EndsAtUtc        = newAppointment.EndsAtUtc,
            TherapistName    = newAppointment.TherapistName,
            StartsAtLocal    = local.ToString("dd/MM/yyyy HH:mm"),
        };
    }

    private static string MapSourceToRevenueEventType(AppointmentSource source) => source switch
    {
        AppointmentSource.WhatsApp     => "missed_call_converted",
        AppointmentSource.GapFill      => "gap_filled",
        AppointmentSource.Reactivation => "reactivation_booked",
        AppointmentSource.Rescheduled  => "reschedule_saved",
        _                              => "lead_converted",
    };
}
