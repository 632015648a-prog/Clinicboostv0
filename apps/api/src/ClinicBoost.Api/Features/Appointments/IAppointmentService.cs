namespace ClinicBoost.Api.Features.Appointments;

/// <summary>
/// Contrato del servicio de citas para la feature Appointments (Vertical Slice).
///
/// RESPONSABILIDADES
/// ─────────────────
///  · GetAvailableSlots  — consulta huecos libres, convierte UTC↔timezone del tenant.
///  · BookAppointment    — transacción con pessimistic lock, valida slot, revenue telemetry.
///  · CancelAppointment  — transacción, AppointmentEvent, revenue telemetry de oportunidad perdida.
///  · RescheduleAppointment — transacción atómica (cancela + reserva), AppointmentEvent, revenue.
///
/// HARD LIMITS (aplicados en el servicio, no en endpoints ni prompts)
/// ───────────────────────────────────────────────────────────────────
///  · HL-1: NUNCA confirmar cita sin que el backend ejecute BookAppointmentAsync.
///  · HL-2: Descuento validado contra RuleConfig global/discount_max_pct en BD.
///  · HL-3: RevenueEvent solo si IsRecovered = true (source != Manual y dentro ventana).
///  · HL-4: Race condition controlada con SELECT … FOR UPDATE (Postgres row lock).
/// </summary>
public interface IAppointmentService
{
    /// <summary>
    /// Devuelve huecos disponibles en el rango de fechas para el tenant.
    /// Las fechas de request vienen en timezone del tenant; la respuesta incluye
    /// ambas representaciones (UTC + local).
    /// </summary>
    Task<GetAvailableSlotsResponse> GetAvailableSlotsAsync(
        Guid                     tenantId,
        GetAvailableSlotsRequest request,
        CancellationToken        ct = default);

    /// <summary>
    /// Reserva una cita con transacción y control de race condition.
    ///
    /// FLUJO
    /// ─────
    ///  1. Valida discount vs RuleConfig.
    ///  2. BeginTransaction(IsolationLevel.RepeatableRead).
    ///  3. SELECT … WITH (UPDLOCK) / FOR UPDATE sobre el rango de tiempo.
    ///  4. Si hay overlap → Rollback → devuelve SlotConflict.
    ///  5. INSERT Appointment (status = Scheduled).
    ///  6. INSERT AppointmentEvent (event_type = created, actor_type = source).
    ///  7. Si IsRecovered → INSERT RevenueEvent (amount, success_fee, attribution).
    ///  8. Commit.
    ///
    /// IDEMPOTENCIA
    /// ────────────
    /// Si IdempotencyKey ya existe en processed_events → devuelve el appointment
    /// ya creado sin duplicar.
    /// </summary>
    Task<(BookAppointmentResponse? Response, AppointmentError? Error)> BookAppointmentAsync(
        Guid                    tenantId,
        BookAppointmentRequest  request,
        CancellationToken       ct = default);

    /// <summary>
    /// Cancela una cita existente.
    ///
    /// FLUJO
    /// ─────
    ///  1. BeginTransaction.
    ///  2. SELECT appointment FOR UPDATE (evita cancelación concurrente).
    ///  3. Valida que el estado sea Scheduled | Confirmed.
    ///  4. UPDATE status = Cancelled.
    ///  5. INSERT AppointmentEvent (event_type = cancelled).
    ///  6. Si IsRecovered → INSERT RevenueEvent (event_type = cancellation_loss,
    ///     amount negativo para trazabilidad del revenue perdido).
    ///  7. Commit.
    /// </summary>
    Task<(CancelAppointmentResponse? Response, AppointmentError? Error)> CancelAppointmentAsync(
        Guid                      tenantId,
        CancelAppointmentRequest  request,
        CancellationToken         ct = default);

    /// <summary>
    /// Reprograma una cita de forma atómica.
    ///
    /// FLUJO
    /// ─────
    ///  1. Valida actor y nuevo slot.
    ///  2. BeginTransaction(RepeatableRead).
    ///  3. FOR UPDATE sobre la cita original y sobre el nuevo rango.
    ///  4. Cancela la cita original (status = Cancelled, RescheduledFromId en la nueva).
    ///  5. INSERT nueva Appointment (status = Scheduled, source = Rescheduled).
    ///  6. INSERT AppointmentEvent en la original (rescheduled_out) y en la nueva (created).
    ///  7. Si IsRecovered → INSERT RevenueEvent (event_type = reschedule_saved).
    ///  8. Commit.
    ///
    /// IDEMPOTENCIA
    /// ────────────
    /// Si IdempotencyKey ya existe → devuelve el appointment ya creado.
    /// </summary>
    Task<(RescheduleAppointmentResponse? Response, AppointmentError? Error)> RescheduleAppointmentAsync(
        Guid                         tenantId,
        RescheduleAppointmentRequest request,
        CancellationToken            ct = default);
}
