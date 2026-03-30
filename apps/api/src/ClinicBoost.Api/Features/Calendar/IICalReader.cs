namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// IICalReader
//
// Abstracción del lector de feeds iCal (RFC 5545).
//
// CONTRATO
// ────────
//  · ReadAsync descarga el .ics desde la URL, lo parsea y devuelve los slots
//    ordenados por StartsAtUtc.
//  · Si la petición supera el timeout configurado lanza OperationCanceledException
//    (o TaskCanceledException).
//  · Si el servidor responde 304 Not Modified (petición condicional), devuelve
//    ICalReadResult.NotModified.
//  · Nunca escribe en BD; toda la persistencia la gestiona ICalendarCacheStore.
//
// EXTENSIBILIDAD
// ──────────────
//  · HttpICalReader → implementación por defecto con HttpClient.
//  · En tests se puede sustituir por un stub/mock.
//  · Para Google Calendar API (OAuth) se puede añadir GoogleCalendarReader sin
//    cambiar el contrato.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resultado de una lectura de feed iCal.
/// </summary>
public sealed record ICalReadResult
{
    public bool                     IsSuccess    { get; init; }
    public bool                     IsNotModified { get; init; }
    public IReadOnlyList<ICalSlot>  Slots        { get; init; } = [];
    public string?                  NewETag      { get; init; }
    public DateTimeOffset?          NewLastModified { get; init; }
    public string?                  ContentHash  { get; init; }
    public string?                  ErrorMessage { get; init; }

    public static ICalReadResult Success(
        IReadOnlyList<ICalSlot> slots,
        string?                 etag           = null,
        DateTimeOffset?         lastModified   = null,
        string?                 contentHash    = null)
        => new()
        {
            IsSuccess       = true,
            Slots           = slots,
            NewETag         = etag,
            NewLastModified = lastModified,
            ContentHash     = contentHash,
        };

    public static ICalReadResult NotModified()
        => new() { IsSuccess = true, IsNotModified = true };

    public static ICalReadResult Failure(string error)
        => new() { IsSuccess = false, ErrorMessage = error };
}

/// <summary>
/// Lee feeds iCal (RFC 5545) desde una URL y devuelve los slots parseados.
/// </summary>
public interface IICalReader
{
    /// <summary>
    /// Descarga y parsea el feed iCal de <paramref name="url"/>.
    /// </summary>
    /// <param name="url">URL del feed .ics.</param>
    /// <param name="etag">ETag previo para petición condicional (If-None-Match).</param>
    /// <param name="lastModified">Last-Modified previo para petición condicional (If-Modified-Since).</param>
    /// <param name="ct">Token de cancelación; el timeout se aplica internamente.</param>
    Task<ICalReadResult> ReadAsync(
        string            url,
        string?           etag         = null,
        DateTimeOffset?   lastModified = null,
        CancellationToken ct           = default);
}
