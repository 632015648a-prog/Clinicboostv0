using FluentValidation;
using ClinicBoost.Api.Infrastructure.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace ClinicBoost.Api.Features.Appointments;

// ════════════════════════════════════════════════════════════════════════════
// AppointmentEndpoints.cs
//
// Endpoints de gestión de citas (Vertical Slice).
//
// RUTAS
// ─────
//   GET  /api/appointments/slots        — huecos disponibles (query params)
//   POST /api/appointments/book         — reservar cita
//   POST /api/appointments/cancel       — cancelar cita
//   POST /api/appointments/reschedule   — reprogramar cita
//
// SEGURIDAD
// ─────────
//  · Todos los endpoints requieren JWT válido ([Authorize] implícito por la política default).
//  · TenantId se extrae de ITenantContext (JWT claim), NO del body.
//  · Nunca se lee service role o credenciales privilegiadas desde el frontend.
//
// REGLAS ECONÓMICAS
// ─────────────────
//  · SessionAmount, DiscountPct y cálculo de success_fee viven en AppointmentService.
//  · El endpoint NO calcula ni valida reglas de negocio económicas.
// ════════════════════════════════════════════════════════════════════════════

public static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/appointments")
            .RequireAuthorization()
            .WithTags("Appointments");
            // .WithOpenApi() eliminado: obsoleto en .NET 10 (aspnet/deprecate/002)

        // GET /api/appointments/slots
        group.MapGet("/slots", GetAvailableSlotsAsync)
            .WithSummary("Obtiene los huecos disponibles para reservar cita.")
            .WithDescription(
                "Devuelve hasta 20 slots libres en el rango de fechas solicitado, " +
                "respetando el horario laboral y la timezone del tenant. " +
                "Fechas en formato YYYY-MM-DD (timezone del tenant).");

        // POST /api/appointments/book
        group.MapPost("/book", BookAppointmentAsync)
            .WithSummary("Reserva una cita.")
            .WithDescription(
                "Crea una cita con control de race condition (IsolationLevel.RepeatableRead). " +
                "Si el slot ya está ocupado devuelve 409. " +
                "Calcula revenue telemetry en backend si Source != Manual.");

        // POST /api/appointments/cancel
        group.MapPost("/cancel", CancelAppointmentAsync)
            .WithSummary("Cancela una cita existente.")
            .WithDescription(
                "Sólo cancela citas en estado Scheduled o Confirmed. " +
                "Registra AppointmentEvent y, si la cita era recovered, un RevenueEvent de pérdida.");

        // POST /api/appointments/reschedule
        group.MapPost("/reschedule", RescheduleAppointmentAsync)
            .WithSummary("Reprograma una cita de forma atómica.")
            .WithDescription(
                "Cancela la cita original y crea una nueva en una transacción atómica " +
                "con control de race condition. Registra revenue telemetry si aplica.");

        return app;
    }

    // ── GET /api/appointments/slots ───────────────────────────────────────────

    private static async Task<IResult> GetAvailableSlotsAsync(
        [AsParameters] GetAvailableSlotsQueryParams query,
        IAppointmentService                         service,
        IValidator<GetAvailableSlotsRequest>        validator,
        ITenantContext                               tenantCtx,
        CancellationToken                           ct)
    {
        var tenantId = tenantCtx.RequireTenantId();

        var request = new GetAvailableSlotsRequest
        {
            DateFrom       = query.DateFrom ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            DateTo         = query.DateTo   ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            TherapistName  = query.TherapistName,
            DurationMinutes = query.DurationMinutes ?? 60,
        };

        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var response = await service.GetAvailableSlotsAsync(tenantId, request, ct);
        return Results.Ok(response);
    }

    // ── POST /api/appointments/book ───────────────────────────────────────────

    private static async Task<IResult> BookAppointmentAsync(
        [FromBody] BookAppointmentRequest           request,
        IAppointmentService                         service,
        IValidator<BookAppointmentRequest>          validator,
        ITenantContext                               tenantCtx,
        CancellationToken                           ct)
    {
        var tenantId   = tenantCtx.RequireTenantId();
        var validation = await validator.ValidateAsync(request, ct);

        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (response, error) = await service.BookAppointmentAsync(tenantId, request, ct);

        return error?.Code switch
        {
            "SLOT_CONFLICT"     => Results.Conflict(error),
            "DISCOUNT_EXCEEDED" => Results.BadRequest(error),
            not null            => Results.BadRequest(error),
            null                => Results.Created($"/api/appointments/{response!.AppointmentId}", response),
        };
    }

    // ── POST /api/appointments/cancel ─────────────────────────────────────────

    private static async Task<IResult> CancelAppointmentAsync(
        [FromBody] CancelAppointmentRequest         request,
        IAppointmentService                         service,
        IValidator<CancelAppointmentRequest>        validator,
        ITenantContext                               tenantCtx,
        CancellationToken                           ct)
    {
        var tenantId   = tenantCtx.RequireTenantId();
        var validation = await validator.ValidateAsync(request, ct);

        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (response, error) = await service.CancelAppointmentAsync(tenantId, request, ct);

        return error?.Code switch
        {
            "NOT_FOUND"     => Results.NotFound(error),
            "INVALID_STATUS"=> Results.Conflict(error),
            "INVALID_ACTOR" => Results.BadRequest(error),
            not null        => Results.BadRequest(error),
            null            => Results.Ok(response),
        };
    }

    // ── POST /api/appointments/reschedule ─────────────────────────────────────

    private static async Task<IResult> RescheduleAppointmentAsync(
        [FromBody] RescheduleAppointmentRequest      request,
        IAppointmentService                          service,
        IValidator<RescheduleAppointmentRequest>     validator,
        ITenantContext                                tenantCtx,
        CancellationToken                            ct)
    {
        var tenantId   = tenantCtx.RequireTenantId();
        var validation = await validator.ValidateAsync(request, ct);

        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var (response, error) = await service.RescheduleAppointmentAsync(tenantId, request, ct);

        return error?.Code switch
        {
            "NOT_FOUND"     => Results.NotFound(error),
            "SLOT_CONFLICT" => Results.Conflict(error),
            "INVALID_STATUS"=> Results.Conflict(error),
            "INVALID_ACTOR" => Results.BadRequest(error),
            not null        => Results.BadRequest(error),
            null            => Results.Created($"/api/appointments/{response!.NewAppointmentId}", response),
        };
    }
}

/// <summary>
/// Query params para GET /api/appointments/slots.
/// Separados del DTO de request para compatibilidad con [AsParameters].
/// </summary>
public sealed record GetAvailableSlotsQueryParams
{
    [FromQuery(Name = "date_from")]
    public string? DateFrom { get; init; }

    [FromQuery(Name = "date_to")]
    public string? DateTo { get; init; }

    [FromQuery(Name = "therapist_name")]
    public string? TherapistName { get; init; }

    [FromQuery(Name = "duration_minutes")]
    public int? DurationMinutes { get; init; }
}
