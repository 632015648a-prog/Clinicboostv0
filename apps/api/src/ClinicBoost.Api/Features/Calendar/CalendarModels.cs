using ClinicBoost.Domain.Common;

namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// CalendarModels
//
// DTOs y entidades de dominio ligeras para la capa iCal read-only.
//
// PRINCIPIOS
// ──────────
//  · Vertical Slice: todos los tipos de este feature en un único namespace.
//  · CalendarCache es una entidad de infraestructura (no de dominio puro);
//    vive en Features/Calendar para respetar la slice.
//  · ICalSlot es inmutable (init-only) para evitar mutaciones accidentales.
//  · CalendarCacheStatus diferencia lectura fresca de datos stale usados en
//    fallback, permitiendo trazabilidad en logs y telemetría.
// ════════════════════════════════════════════════════════════════════════════

// ── Slot leído del calendario (inmutable) ────────────────────────────────────

/// <summary>
/// Evento/bloqueo leído desde un calendario iCal.
/// Representa un intervalo ocupado o disponible según el contexto.
/// </summary>
public sealed record ICalSlot
{
    /// <summary>Inicio del evento en UTC.</summary>
    public required DateTimeOffset StartsAtUtc { get; init; }

    /// <summary>Fin del evento en UTC.</summary>
    public required DateTimeOffset EndsAtUtc   { get; init; }

    /// <summary>Summary del evento (campo SUMMARY en el .ics).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>UID del evento para deduplicación.</summary>
    public string Uid { get; init; } = string.Empty;

    /// <summary>True si el evento marca tiempo ocupado (TRANSP:OPAQUE).</summary>
    public bool IsOpaque { get; init; } = true;
}

// ── Resultado de la lectura del calendario ──────────────────────────────────

/// <summary>
/// Estado de la respuesta del <see cref="ICalendarService"/>.
/// Permite al consumidor saber si los datos son frescos o proceden de un fallback.
/// </summary>
public enum CalendarCacheStatus
{
    /// <summary>Datos leídos directamente de la URL iCal; dentro del TTL.</summary>
    Fresh = 1,

    /// <summary>Datos servidos desde la caché persistida (BD); la URL iCal falló.</summary>
    Stale = 2,

    /// <summary>No hay datos: ni iCal ni caché disponibles.</summary>
    Unavailable = 3,
}

/// <summary>
/// Resultado devuelto por <see cref="ICalendarService.GetSlotsAsync"/>.
/// </summary>
public sealed record CalendarResult
{
    /// <summary>Slots del calendario (puede estar vacío).</summary>
    public IReadOnlyList<ICalSlot> Slots { get; init; } = [];

    /// <summary>Estado de la fuente de datos.</summary>
    public CalendarCacheStatus Status { get; init; }

    /// <summary>Momento en que los datos fueron leídos o cacheados (UTC).</summary>
    public DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Error si Status == Unavailable o Stale (informativo, no lanzable).</summary>
    public string? ErrorMessage { get; init; }

    // ── Factorías ──────────────────────────────────────────────────────────

    public static CalendarResult Fresh(IReadOnlyList<ICalSlot> slots, DateTimeOffset fetchedAt)
        => new() { Slots = slots, Status = CalendarCacheStatus.Fresh, FetchedAtUtc = fetchedAt };

    public static CalendarResult Stale(IReadOnlyList<ICalSlot> slots, DateTimeOffset cachedAt, string? error = null)
        => new() { Slots = slots, Status = CalendarCacheStatus.Stale, FetchedAtUtc = cachedAt, ErrorMessage = error };

    public static CalendarResult Unavailable(string error)
        => new() { Slots = [], Status = CalendarCacheStatus.Unavailable, FetchedAtUtc = DateTimeOffset.UtcNow, ErrorMessage = error };
}

// ── Entidad de caché persistida ──────────────────────────────────────────────

/// <summary>
/// Entrada de caché de calendario almacenada en la BD (tabla calendar_cache).
///
/// Diseño:
///  · Una fila por (tenant_id, connection_id).
///  · Se sobreescribe con UPSERT; nunca hay dos filas activas para la misma conexión.
///  · SlotsJson almacena el array de ICalSlot serializado como JSONB.
///  · FetchedAtUtc permite calcular la "edad" del dato sin consultar el iCal externo.
///  · ETag / LastModified permiten peticiones condicionales (If-None-Match / If-Modified-Since)
///    para ahorrar ancho de banda cuando el servidor iCal los soporte.
/// </summary>
public sealed class CalendarCache
{
    public Guid   Id             { get; init; } = Guid.NewGuid();
    public Guid   TenantId       { get; init; }
    public Guid   ConnectionId   { get; init; }

    /// <summary>Slots serializados como JSON (JSONB en Postgres).</summary>
    public required string SlotsJson  { get; set; }

    /// <summary>Momento en que se hizo la última lectura exitosa de la URL iCal (UTC).</summary>
    public DateTimeOffset FetchedAtUtc { get; set; }

    /// <summary>Próxima lectura obligatoria (= FetchedAtUtc + TTL). Permite expiración pasiva.</summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>ETag recibido del servidor iCal (opcional, para peticiones condicionales).</summary>
    public string? ETag { get; set; }

    /// <summary>Last-Modified recibido del servidor iCal (opcional).</summary>
    public DateTimeOffset? LastModifiedUtc { get; set; }

    /// <summary>Hash SHA-256 del contenido crudo del .ics; permite detectar cambios sin parsear.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Error de la última lectura fallida (null si la última fue exitosa).</summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>Timestamp del registro (auditoría).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Última actualización (auditoría).</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ── Opciones de configuración ────────────────────────────────────────────────

/// <summary>
/// Opciones de la capa iCal, ligadas a la sección "ICalOptions" en appsettings.
/// </summary>
public sealed class ICalOptions
{
    public const string SectionName = "ICalOptions";

    /// <summary>TTL antes de que un dato se considere stale y se reintente la URL. Por defecto 15 min.</summary>
    public TimeSpan FreshnessTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Timeout para la petición HTTP al servidor iCal. Por defecto 10 s.</summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tiempo máximo que se puede servir un dato stale como fallback
    /// antes de devolver Unavailable. Por defecto 24 h.
    /// </summary>
    public TimeSpan MaxStaleAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Número máximo de eventos a procesar por lectura (protección DoS contra .ics gigantes).
    /// Por defecto 5 000.
    /// </summary>
    public int MaxEventsPerFeed { get; set; } = 5_000;
}
