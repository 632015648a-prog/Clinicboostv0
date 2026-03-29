namespace ClinicBoost.Api.Infrastructure.Idempotency;

// ════════════════════════════════════════════════════════════════════════════
// IdempotencyResult
//
// Resultado del intento de registrar un evento como procesado.
// Sigue el patrón Result/Railway: el caller decide qué hacer sin excepciones
// en el path normal.
//
// ESTADOS POSIBLES
// ────────────────
//
//   AlreadyProcessed = false, IsReplay = false
//     → Primer procesamiento. Proceder con la lógica de negocio.
//
//   AlreadyProcessed = true, IsReplay = false
//     → Evento duplicado idéntico. Devolver 200/respuesta idempotente.
//
//   AlreadyProcessed = true, IsReplay = true
//     → Mismo ID, payload diferente (hash distinto). Posible ataque de
//       replay alterado o bug del proveedor. Loguear alerta y rechazar.
//
//   IsError = true
//     → Error al acceder a la BD. No procesar; dejar que el retry lo reintente.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resultado inmutable del intento de idempotencia.
/// Crear mediante los métodos de fábrica estáticos.
/// </summary>
public sealed class IdempotencyResult
{
    // ── Propiedades de resultado ──────────────────────────────────────────────

    /// <summary>
    /// True si el evento ya existía en <c>processed_events</c> antes de este intento.
    /// Cuando true, el handler debe devolver la respuesta idempotente sin re-procesar.
    /// </summary>
    public bool AlreadyProcessed { get; }

    /// <summary>
    /// True si el evento ya existía PERO con un payload_hash diferente.
    /// Indica potencial re-entrega con cuerpo alterado (replay attack) o
    /// error del proveedor. Requiere log de alerta e investigación.
    /// Solo puede ser true cuando <see cref="AlreadyProcessed"/> es true.
    /// </summary>
    public bool IsPayloadMismatch { get; }

    /// <summary>
    /// True cuando ocurrió un error de infraestructura (BD caída, timeout…).
    /// El handler debe dejar que el retry del proveedor vuelva a intentarlo.
    /// </summary>
    public bool IsError { get; }

    /// <summary>
    /// Error de infraestructura si <see cref="IsError"/> es true.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>
    /// ID del registro en <c>processed_events</c> si se creó uno nuevo,
    /// o del existente si <see cref="AlreadyProcessed"/> es true.
    /// Null cuando <see cref="IsError"/> es true.
    /// </summary>
    public Guid? ProcessedEventId { get; }

    /// <summary>
    /// Timestamp en que el evento fue procesado por primera vez (UTC).
    /// Null cuando <see cref="IsError"/> es true.
    /// </summary>
    public DateTimeOffset? FirstProcessedAt { get; }

    // ── Propiedades derivadas convenientes ────────────────────────────────────

    /// <summary>
    /// True si el evento es nuevo y debe procesarse.
    /// Equivale a !AlreadyProcessed && !IsError.
    /// </summary>
    public bool ShouldProcess => !AlreadyProcessed && !IsError;

    /// <summary>
    /// True si el handler debe devolver una respuesta idempotente vacía (200/204)
    /// sin ejecutar lógica de negocio.
    /// </summary>
    public bool ShouldSkip => AlreadyProcessed && !IsError;

    // ── Constructor privado ───────────────────────────────────────────────────

    private IdempotencyResult(
        bool             alreadyProcessed,
        bool             isPayloadMismatch,
        bool             isError,
        Exception?       error,
        Guid?            processedEventId,
        DateTimeOffset?  firstProcessedAt)
    {
        AlreadyProcessed  = alreadyProcessed;
        IsPayloadMismatch = isPayloadMismatch;
        IsError           = isError;
        Error             = error;
        ProcessedEventId  = processedEventId;
        FirstProcessedAt  = firstProcessedAt;
    }

    // ── Métodos de fábrica ────────────────────────────────────────────────────

    /// <summary>
    /// Evento nuevo: se insertó correctamente en processed_events.
    /// El handler debe continuar con la lógica de negocio.
    /// </summary>
    public static IdempotencyResult NewEvent(Guid id, DateTimeOffset processedAt) =>
        new(alreadyProcessed: false,
            isPayloadMismatch: false,
            isError: false,
            error: null,
            processedEventId: id,
            firstProcessedAt: processedAt);

    /// <summary>
    /// Evento duplicado idéntico (mismo ID y mismo hash).
    /// El handler debe devolver respuesta idempotente sin re-procesar.
    /// </summary>
    public static IdempotencyResult Duplicate(Guid id, DateTimeOffset firstProcessedAt) =>
        new(alreadyProcessed: true,
            isPayloadMismatch: false,
            isError: false,
            error: null,
            processedEventId: id,
            firstProcessedAt: firstProcessedAt);

    /// <summary>
    /// Evento duplicado con payload diferente (mismo ID, hash distinto).
    /// Indica posible replay attack o bug del proveedor.
    /// El handler debe loguear alerta y rechazar el evento.
    /// </summary>
    public static IdempotencyResult PayloadMismatch(Guid id, DateTimeOffset firstProcessedAt) =>
        new(alreadyProcessed: true,
            isPayloadMismatch: true,
            isError: false,
            error: null,
            processedEventId: id,
            firstProcessedAt: firstProcessedAt);

    /// <summary>
    /// Error de infraestructura al consultar/insertar en processed_events.
    /// El handler NO debe procesar el evento; el reintento del proveedor lo resolverá.
    /// </summary>
    public static IdempotencyResult Failure(Exception error) =>
        new(alreadyProcessed: false,
            isPayloadMismatch: false,
            isError: true,
            error: error,
            processedEventId: null,
            firstProcessedAt: null);

    // ── Deconstruct ───────────────────────────────────────────────────────────

    /// <summary>
    /// Permite usar el patrón <c>(var shouldProcess, var id) = result;</c>
    /// </summary>
    public void Deconstruct(out bool shouldProcess, out Guid? processedEventId)
    {
        shouldProcess    = ShouldProcess;
        processedEventId = ProcessedEventId;
    }

    public override string ToString() => IsError
        ? $"IdempotencyResult[Error: {Error?.GetType().Name}]"
        : $"IdempotencyResult[AlreadyProcessed={AlreadyProcessed}, " +
          $"Mismatch={IsPayloadMismatch}, Id={ProcessedEventId}]";
}
