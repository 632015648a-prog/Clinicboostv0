using System.Text.Json;
using System.Text.Json.Serialization;
using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Appointments;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// ToolRegistry
//
// Define las tools disponibles para el agente conversacional y las ejecuta.
//
// TOOLS DISPONIBLES
// ─────────────────
//   · get_patient_appointments   — citas activas/futuras del paciente
//   · get_available_slots        — huecos disponibles para una fecha/terapeuta
//   · propose_appointment        — registra una propuesta de cita (NO confirma)
//   · propose_cancellation       — registra solicitud de cancelación
//   · confirm_appointment_response — registra sí/no a un recordatorio
//   · escalate_to_human          — deriva la conversación a un agente humano
//
// HARD LIMITS EN TOOLS
// ────────────────────
//   · propose_appointment NO crea ningún registro en appointments.
//     Solo devuelve un resumen para que el usuario lo confirme visualmente.
//     El endpoint de backend es el único que puede escribir en appointments.
//   · escalate_to_human es siempre permitida (nunca bloqueada por HardLimitGuard).
//
// REGISTRO
// ────────
// Registrar como Scoped (depende de AppDbContext e ICalendarService).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Registra las tool definitions de OpenAI y ejecuta las tool calls recibidas.
/// Registrar como <b>Scoped</b>.
/// </summary>
public sealed class ToolRegistry
{
    private readonly AppDbContext            _db;
    private readonly ICalendarService        _calendarService;
    private readonly ILogger<ToolRegistry>   _logger;

    public ToolRegistry(
        AppDbContext            db,
        ICalendarService        calendarService,
        ILogger<ToolRegistry>   logger)
    {
        _db              = db;
        _calendarService = calendarService;
        _logger          = logger;
    }

    // ── Definiciones (enviadas a OpenAI en cada turno) ────────────────────

    /// <summary>
    /// Devuelve el array JSON de tool definitions para OpenAI.
    /// </summary>
    public static IReadOnlyList<object> GetToolDefinitions() =>
    [
        new
        {
            type     = "function",
            function = new
            {
                name        = "get_patient_appointments",
                description = "Obtiene las citas activas y futuras del paciente en la clínica.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        patient_id = new { type = "string", description = "UUID del paciente" },
                        limit      = new { type = "integer", description = "Máximo de citas a devolver (default 5)" }
                    },
                    required = new[] { "patient_id" }
                }
            }
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "get_available_slots",
                description = "Devuelve los huecos disponibles para reservar cita. Usa esto ANTES de proponer una cita.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        tenant_id      = new { type = "string",  description = "UUID del tenant" },
                        date_from      = new { type = "string",  description = "Fecha inicio ISO-8601 (YYYY-MM-DD)" },
                        date_to        = new { type = "string",  description = "Fecha fin ISO-8601 (YYYY-MM-DD)" },
                        therapist_name = new { type = "string",  description = "Nombre del terapeuta (opcional)" },
                        duration_min   = new { type = "integer", description = "Duración de la cita en minutos (default 60)" }
                    },
                    required = new[] { "tenant_id", "date_from", "date_to" }
                }
            }
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "propose_appointment",
                description  =
                    "Registra una PROPUESTA de cita para que el backend la confirme. " +
                    "NUNCA confirmes tú la cita. Esta tool solo genera la propuesta.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        patient_id     = new { type = "string", description = "UUID del paciente" },
                        therapist_name = new { type = "string", description = "Nombre del terapeuta" },
                        starts_at_utc  = new { type = "string", description = "Inicio de la cita en ISO-8601 UTC" },
                        ends_at_utc    = new { type = "string", description = "Fin de la cita en ISO-8601 UTC" },
                        notes          = new { type = "string", description = "Notas adicionales (opcional)" }
                    },
                    required = new[] { "patient_id", "therapist_name", "starts_at_utc", "ends_at_utc" }
                }
            }
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "propose_cancellation",
                description = "Registra la solicitud de cancelación de una cita. No cancela automáticamente.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        appointment_id = new { type = "string", description = "UUID de la cita a cancelar" },
                        reason         = new { type = "string", description = "Motivo de la cancelación (opcional)" }
                    },
                    required = new[] { "appointment_id" }
                }
            }
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "confirm_appointment_response",
                description = "Registra la respuesta del paciente a un recordatorio (confirma o rechaza la cita).",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        appointment_id = new { type = "string", description = "UUID de la cita" },
                        confirmed      = new { type = "boolean", description = "true = confirma, false = cancela/no asiste" }
                    },
                    required = new[] { "appointment_id", "confirmed" }
                }
            }
        },
        new
        {
            type     = "function",
            function = new
            {
                name        = "escalate_to_human",
                description =
                    "Deriva la conversación a un agente humano. " +
                    "Usa esto cuando: el paciente lo pide, no tienes suficiente contexto, " +
                    "hay una queja, o cualquier situación fuera de tu alcance.",
                parameters  = new
                {
                    type       = "object",
                    properties = new
                    {
                        reason = new { type = "string", description = "Motivo de la derivación" }
                    },
                    required = new[] { "reason" }
                }
            }
        },
    ];

    // ── Ejecución de tool calls ───────────────────────────────────────────

    /// <summary>
    /// Ejecuta una tool call recibida de OpenAI y devuelve el resultado como string JSON.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        string            toolName,
        string            argsJson,
        AgentContext      ctx,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[ToolRegistry] Ejecutando tool '{Tool}'. Args={Args} TenantId={TenantId}",
            toolName, argsJson, ctx.TenantId);

        return toolName switch
        {
            "get_patient_appointments"    => await GetPatientAppointmentsAsync(argsJson, ctx, ct),
            "get_available_slots"         => await GetAvailableSlotsAsync(argsJson, ctx, ct),
            "propose_appointment"         => ParseAndBuildProposal(argsJson, ctx),
            "propose_cancellation"        => await ProposeCancellationAsync(argsJson, ctx, ct),
            "confirm_appointment_response"=> await ConfirmAppointmentResponseAsync(argsJson, ctx, ct),
            "escalate_to_human"           => BuildEscalation(argsJson),
            _                             => ToolExecutionResult.Error($"Tool '{toolName}' no reconocida."),
        };
    }

    // ── Implementaciones ──────────────────────────────────────────────────

    private async Task<ToolExecutionResult> GetPatientAppointmentsAsync(
        string argsJson, AgentContext ctx, CancellationToken ct)
    {
        try
        {
            var args  = JsonDocument.Parse(argsJson).RootElement;
            var limit = args.TryGetProperty("limit", out var lp) ? lp.GetInt32() : 5;

            var now   = DateTimeOffset.UtcNow;
            // Materializar primero como entidades, luego proyectar en memoria
            // (.Select() anónimo + ToListAsync() tiene inferencia ambigua en .NET 10)
            var rawAppts = await _db.Appointments
                .Where(a =>
                    a.TenantId   == ctx.TenantId  &&
                    a.PatientId  == ctx.PatientId  &&
                    a.StartsAtUtc >= now           &&
                    a.Status != AppointmentStatus.Cancelled &&
                    a.Status != AppointmentStatus.Completed)
                .OrderBy(a => a.StartsAtUtc)
                .Take(limit)
                .ToListAsync(ct);

            var appointments = rawAppts.Select(a => new
                {
                    id            = a.Id,
                    therapist     = a.TherapistName,
                    starts_at_utc = a.StartsAtUtc,
                    ends_at_utc   = a.EndsAtUtc,
                    status        = a.Status.ToString().ToLowerInvariant(),
                }).ToList();

            return ToolExecutionResult.Ok(
                appointments.Count > 0
                    ? JsonSerializer.Serialize(appointments)
                    : "{\"appointments\":[],\"message\":\"No hay citas próximas.\"}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolRegistry] Error en get_patient_appointments.");
            return ToolExecutionResult.Error("Error al obtener citas.");
        }
    }

    private async Task<ToolExecutionResult> GetAvailableSlotsAsync(
        string argsJson, AgentContext ctx, CancellationToken ct)
    {
        // Implementación real: usa ICalendarService (cache-aside persistida + fallback).
        // El agente recibe los slots ocupados del calendario iCal del terapeuta;
        // la disponibilidad se calcula filtrando bloques ocupados (IsOpaque = true).
        try
        {
            var args        = JsonDocument.Parse(argsJson).RootElement;
            var dateFromArg = args.GetProperty("date_from").GetString()!;
            var dateFrom    = DateTimeOffset.Parse(dateFromArg).ToUniversalTime();
            var durationMin = args.TryGetProperty("duration_min", out var dp) ? dp.GetInt32() : 60;
            var dateTo      = dateFrom.AddDays(7);

            // Obtener la conexión iCal activa del tenant
            var connection = await _db.CalendarConnections
                .AsNoTracking()
                .Where(c => c.TenantId == ctx.TenantId && c.IsActive &&
                            c.Provider == "ical" && c.IcalUrl != null)
                .OrderByDescending(c => c.IsPrimary)
                .FirstOrDefaultAsync(ct);

            if (connection is null)
            {
                // Sin conexión iCal configurada: devolvemos respuesta informativa
                _logger.LogInformation(
                    "[ToolRegistry] Sin CalendarConnection iCal para TenantId={TenantId}",
                    ctx.TenantId);
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    slots            = Array.Empty<object>(),
                    calendar_status  = "no_connection",
                    message          = "No hay calendario iCal configurado para este tenant.",
                }));
            }

            // Consultar el calendario (cache-aside persistida)
            var result = await _calendarService.GetSlotsAsync(ctx.TenantId, connection.Id, ct);

            if (result.Status == CalendarCacheStatus.Unavailable)
            {
                _logger.LogWarning(
                    "[ToolRegistry] Calendario no disponible. TenantId={TenantId} Error={Error}",
                    ctx.TenantId, result.ErrorMessage);
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    slots            = Array.Empty<object>(),
                    calendar_status  = "unavailable",
                    message          = "El calendario no está disponible temporalmente.",
                }));
            }

            // Filtrar slots ocupados en el rango solicitado
            var busySlots = result.Slots
                .Where(s => s.IsOpaque &&
                            s.StartsAtUtc < dateTo &&
                            s.EndsAtUtc   > dateFrom)
                .ToList();

            // Generar huecos libres entre los bloques ocupados
            var freeSlots = ComputeFreeSlots(
                dateFrom, dateTo, busySlots, TimeSpan.FromMinutes(durationMin));

            _logger.LogInformation(
                "[ToolRegistry] get_available_slots. TenantId={TenantId} " +
                "CalendarStatus={Status} BusyCount={Busy} FreeCount={Free}",
                ctx.TenantId, result.Status, busySlots.Count, freeSlots.Count);

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                slots            = freeSlots,
                calendar_status  = result.Status.ToString().ToLowerInvariant(),
                fetched_at_utc   = result.FetchedAtUtc,
                is_stale         = result.Status == CalendarCacheStatus.Stale,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolRegistry] Error en get_available_slots.");
            return ToolExecutionResult.Error("Error al consultar disponibilidad.");
        }
    }

    // ── Cálculo de huecos libres ──────────────────────────────────────────────

    /// <summary>
    /// Dado un rango y una lista de bloques ocupados, calcula los huecos
    /// de al menos <paramref name="slotDuration"/> de duración.
    /// Asume slots ordenados por StartsAtUtc.
    /// </summary>
    private static IReadOnlyList<object> ComputeFreeSlots(
        DateTimeOffset          from,
        DateTimeOffset          to,
        IReadOnlyList<ICalSlot> busySlots,
        TimeSpan                slotDuration)
    {
        var result  = new List<object>();
        var cursor  = from;

        foreach (var busy in busySlots.OrderBy(s => s.StartsAtUtc))
        {
            if (busy.StartsAtUtc > cursor && (busy.StartsAtUtc - cursor) >= slotDuration)
            {
                result.Add(new
                {
                    starts_at_utc = cursor.ToString("O"),
                    ends_at_utc   = busy.StartsAtUtc.ToString("O"),
                    available     = true,
                });
            }

            if (busy.EndsAtUtc > cursor)
                cursor = busy.EndsAtUtc;
        }

        // Hueco final (después del último bloque ocupado)
        if (cursor < to && (to - cursor) >= slotDuration)
        {
            result.Add(new
            {
                starts_at_utc = cursor.ToString("O"),
                ends_at_utc   = to.ToString("O"),
                available     = true,
            });
        }

        return result;
    }

    private static ToolExecutionResult ParseAndBuildProposal(
        string argsJson, AgentContext ctx)
    {
        // HARD LIMIT [HL-1]: esta tool NUNCA escribe en la BD.
        // Solo construye una propuesta para que el agente informe al paciente.
        // El backend confirma en /api/appointments (otro endpoint).
        try
        {
            var args = JsonDocument.Parse(argsJson).RootElement;

            var proposal = new AppointmentProposal
            {
                TherapistName = args.GetProperty("therapist_name").GetString()!,
                StartsAtUtc   = DateTimeOffset.Parse(args.GetProperty("starts_at_utc").GetString()!),
                EndsAtUtc     = DateTimeOffset.Parse(args.GetProperty("ends_at_utc").GetString()!),
                Notes         = args.TryGetProperty("notes", out var n) ? n.GetString() : null,
            };

            var summary = $"Propuesta creada: {proposal.TherapistName} el " +
                          $"{proposal.StartsAtUtc:dd/MM/yyyy HH:mm} UTC. " +
                          $"Pendiente de confirmación por el backend.";

            return ToolExecutionResult.WithProposal(summary, proposal);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Error("Datos de propuesta inválidos: " + ex.Message);
        }
    }

    private async Task<ToolExecutionResult> ProposeCancellationAsync(
        string argsJson, AgentContext ctx, CancellationToken ct)
    {
        try
        {
            var args           = JsonDocument.Parse(argsJson).RootElement;
            var appointmentId  = Guid.Parse(args.GetProperty("appointment_id").GetString()!);

            var appointment = await _db.Appointments
                .FirstOrDefaultAsync(
                    a => a.Id == appointmentId && a.TenantId == ctx.TenantId, ct);

            if (appointment is null)
                return ToolExecutionResult.Error("Cita no encontrada.");

            // Registrar evento de cancelación solicitada (no confirmar).
            // La cancelación real la ejecuta el backend en otro endpoint.
            var evt = new ClinicBoost.Domain.Appointments.AppointmentEvent
            {
                TenantId      = ctx.TenantId,
                AppointmentId = appointmentId,
                EventType     = "cancellation_requested",
                ActorType     = "ai",
                FlowId        = "flow_00",
                CorrelationId = Guid.TryParse(ctx.CorrelationId, out var cg1) ? cg1 : Guid.NewGuid(),
                Payload       = $"{{\"source\":\"whatsapp\",\"conversation_id\":\"{ctx.ConversationId}\"}}",
            };
            _db.AppointmentEvents.Add(evt);
            await _db.SaveChangesAsync(ct);

            return ToolExecutionResult.Ok(
                "{\"status\":\"cancellation_requested\",\"message\":\"Solicitud de cancelación registrada.\"}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolRegistry] Error en propose_cancellation.");
            return ToolExecutionResult.Error("Error al registrar cancelación.");
        }
    }

    private async Task<ToolExecutionResult> ConfirmAppointmentResponseAsync(
        string argsJson, AgentContext ctx, CancellationToken ct)
    {
        try
        {
            var args           = JsonDocument.Parse(argsJson).RootElement;
            var appointmentId  = Guid.Parse(args.GetProperty("appointment_id").GetString()!);
            var confirmed      = args.GetProperty("confirmed").GetBoolean();

            var appointment = await _db.Appointments
                .FirstOrDefaultAsync(
                    a => a.Id == appointmentId && a.TenantId == ctx.TenantId, ct);

            if (appointment is null)
                return ToolExecutionResult.Error("Cita no encontrada.");

            var eventType = confirmed ? "patient_confirmed" : "patient_cancelled";
            var evt = new ClinicBoost.Domain.Appointments.AppointmentEvent
            {
                TenantId      = ctx.TenantId,
                AppointmentId = appointmentId,
                EventType     = eventType,
                ActorType     = "patient",
                FlowId        = "flow_00",
                CorrelationId = Guid.TryParse(ctx.CorrelationId, out var cg2) ? cg2 : Guid.NewGuid(),
                Payload       = $"{{\"source\":\"whatsapp\",\"confirmed\":{confirmed.ToString().ToLowerInvariant()},\"conversation_id\":\"{ctx.ConversationId}\"}}",
            };
            _db.AppointmentEvents.Add(evt);
            await _db.SaveChangesAsync(ct);

            return ToolExecutionResult.Ok(
                confirmed
                    ? "{\"status\":\"confirmed\",\"message\":\"Confirmación registrada.\"}"
                    : "{\"status\":\"cancellation_requested\",\"message\":\"Cancelación registrada.\"}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolRegistry] Error en confirm_appointment_response.");
            return ToolExecutionResult.Error("Error al registrar respuesta.");
        }
    }

    private static ToolExecutionResult BuildEscalation(string argsJson)
    {
        try
        {
            var args   = JsonDocument.Parse(argsJson).RootElement;
            var reason = args.GetProperty("reason").GetString() ?? "Derivación solicitada";
            return ToolExecutionResult.Escalation(reason);
        }
        catch
        {
            return ToolExecutionResult.Escalation("Derivación solicitada");
        }
    }
}

// ── Resultado de ejecución de tool ────────────────────────────────────────────

/// <summary>
/// Resultado de ejecutar una tool call.
/// </summary>
public sealed record ToolExecutionResult
{
    public bool                   IsError    { get; init; }
    public bool                   IsEscalation { get; init; }
    public bool                   IsProposal { get; init; }
    public required string        Content    { get; init; }
    public AppointmentProposal?   Proposal   { get; init; }
    public string?                EscalationReason { get; init; }

    public static ToolExecutionResult Ok(string content) =>
        new() { Content = content };

    public static ToolExecutionResult Error(string message) =>
        new() { IsError = true, Content = $"{{\"error\":\"{message}\"}}" };

    public static ToolExecutionResult WithProposal(string summary, AppointmentProposal p) =>
        new() { IsProposal = true, Content = summary, Proposal = p };

    public static ToolExecutionResult Escalation(string reason) =>
        new() { IsEscalation = true, Content = reason, EscalationReason = reason };
}
