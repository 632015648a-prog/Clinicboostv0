using System.Text.Json;
using ClinicBoost.Api.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// CalendarService
//
// Orquestador de la capa iCal read-only.
//
// ARQUITECTURA (Vertical Slice)
// ─────────────────────────────
//  · No hay ApplicationService genérico; este servicio es la implementación
//    completa del feature Calendar dentro de su propia slice.
//  · Accede a AppDbContext directamente (ADR-002) solo para obtener la URL iCal
//    de CalendarConnection; la caché la delega en ICalendarCacheStore.
//
// POLÍTICA CACHE-ASIDE
// ─────────────────────
//  1. GetAsync(cache)
//     · Si existe y edad < FreshnessTtl → Fresh (sin llamar a la URL).
//     · Si existe y edad ≥ FreshnessTtl → Stale; intentar refrescar.
//     · Si no existe → intentar leer directamente.
//  2. ReadAsync(url)
//     · Si 304 Not Modified → actualiza solo ExpiresAtUtc, devuelve slots de caché.
//     · Si OK → UpsertAsync y devuelve Fresh.
//     · Si falla → MarkErrorAsync; si hay caché (edad < MaxStaleAge) devuelve Stale.
//  3. InvalidateCacheAsync → borra la entrada de caché; fuerza lectura en el
//     próximo GetSlotsAsync.
//
// TIMEZONE
// ────────
//  · Solo se trabaja con UTC internamente.
//  · La conversión a hora local del tenant la hace el consumidor (AppointmentService).
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext y EfCalendarCacheStore que son Scoped).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="ICalendarService"/>.
/// Orquesta cache-aside, freshness, timeout y fallback para feeds iCal.
/// </summary>
public sealed class CalendarService : ICalendarService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppDbContext              _db;
    private readonly ICalendarCacheStore       _cacheStore;
    private readonly IICalReader               _reader;
    private readonly ICalOptions               _opts;
    private readonly ILogger<CalendarService>  _logger;

    public CalendarService(
        AppDbContext             db,
        ICalendarCacheStore      cacheStore,
        IICalReader              reader,
        IOptions<ICalOptions>    opts,
        ILogger<CalendarService> logger)
    {
        _db         = db;
        _cacheStore = cacheStore;
        _reader     = reader;
        _opts       = opts.Value;
        _logger     = logger;
    }

    // ── GetSlotsAsync ─────────────────────────────────────────────────────────

    public async Task<CalendarResult> GetSlotsAsync(
        Guid              tenantId,
        Guid              connectionId,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "[CalendarService] GetSlots. TenantId={TenantId} ConnectionId={ConnectionId}",
            tenantId, connectionId);

        // ── 1. Obtener URL iCal de la BD ──────────────────────────────────────
        var connection = await _db.CalendarConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Id == connectionId && c.TenantId == tenantId && c.IsActive,
                ct);

        if (connection is null)
        {
            _logger.LogWarning(
                "[CalendarService] CalendarConnection no encontrada. TenantId={TenantId} ConnectionId={ConnectionId}",
                tenantId, connectionId);
            return CalendarResult.Unavailable("Conexión de calendario no encontrada.");
        }

        if (string.IsNullOrEmpty(connection.IcalUrl))
        {
            _logger.LogWarning(
                "[CalendarService] CalendarConnection sin IcalUrl. ConnectionId={ConnectionId}",
                connectionId);
            return CalendarResult.Unavailable("La conexión no tiene URL iCal configurada.");
        }

        // ── 2. Leer caché persistida ──────────────────────────────────────────
        var cached = await _cacheStore.GetAsync(tenantId, connectionId, ct);
        var now    = DateTimeOffset.UtcNow;

        // ── 3. ¿Datos frescos? ────────────────────────────────────────────────
        if (cached is not null && (now - cached.FetchedAtUtc) < _opts.FreshnessTtl)
        {
            _logger.LogDebug(
                "[CalendarService] Caché fresca. ConnectionId={ConnectionId} Age={Age}s",
                connectionId, (now - cached.FetchedAtUtc).TotalSeconds);

            return CalendarResult.Fresh(DeserializeSlots(cached.SlotsJson), cached.FetchedAtUtc);
        }

        // ── 4. Intentar refrescar desde la URL iCal ───────────────────────────
        var readResult = await _reader.ReadAsync(
            connection.IcalUrl,
            etag:         cached?.ETag,
            lastModified: cached?.LastModifiedUtc,
            ct:           ct);

        // ── 4a. 304 Not Modified ──────────────────────────────────────────────
        if (readResult.IsSuccess && readResult.IsNotModified)
        {
            _logger.LogDebug(
                "[CalendarService] 304 Not Modified. Extendiendo TTL. ConnectionId={ConnectionId}",
                connectionId);

            // Extender ExpiresAtUtc aunque no cambiemos los slots
            if (cached is not null)
            {
                cached.ExpiresAtUtc = now + _opts.FreshnessTtl;
                cached.UpdatedAt    = now;
                await _cacheStore.UpsertAsync(cached, ct);
                return CalendarResult.Fresh(DeserializeSlots(cached.SlotsJson), now);
            }
        }

        // ── 4b. Lectura exitosa ───────────────────────────────────────────────
        if (readResult.IsSuccess && !readResult.IsNotModified)
        {
            var slotsJson = JsonSerializer.Serialize(readResult.Slots, JsonOpts);

            var newEntry = new CalendarCache
            {
                Id             = cached?.Id ?? Guid.NewGuid(),
                TenantId       = tenantId,
                ConnectionId   = connectionId,
                SlotsJson      = slotsJson,
                FetchedAtUtc   = now,
                ExpiresAtUtc   = now + _opts.FreshnessTtl,
                ETag           = readResult.NewETag,
                LastModifiedUtc = readResult.NewLastModified,
                ContentHash    = readResult.ContentHash,
                CreatedAt      = cached?.CreatedAt ?? now,
                UpdatedAt      = now,
            };

            await _cacheStore.UpsertAsync(newEntry, ct);

            // Actualizar SyncStatus en CalendarConnection
            await UpdateConnectionSyncStatusAsync(connectionId, "ok", null, ct);

            _logger.LogInformation(
                "[CalendarService] Caché actualizada. ConnectionId={ConnectionId} Slots={Count}",
                connectionId, readResult.Slots.Count);

            return CalendarResult.Fresh(readResult.Slots, now);
        }

        // ── 4c. Lectura fallida ───────────────────────────────────────────────
        var errorMsg = readResult.ErrorMessage ?? "Error desconocido al leer el calendario.";

        _logger.LogWarning(
            "[CalendarService] Error al leer iCal. ConnectionId={ConnectionId} Error={Error}",
            connectionId, errorMsg);

        await _cacheStore.MarkErrorAsync(tenantId, connectionId, errorMsg, ct);
        await UpdateConnectionSyncStatusAsync(connectionId, "error", errorMsg, ct);

        // Fallback: usar caché stale si no es demasiado antigua
        if (cached is not null && (now - cached.FetchedAtUtc) < _opts.MaxStaleAge)
        {
            _logger.LogWarning(
                "[CalendarService] Usando caché stale. ConnectionId={ConnectionId} Age={Age}h",
                connectionId, (now - cached.FetchedAtUtc).TotalHours);

            return CalendarResult.Stale(
                DeserializeSlots(cached.SlotsJson),
                cached.FetchedAtUtc,
                errorMsg);
        }

        // Sin fallback disponible
        return CalendarResult.Unavailable(errorMsg);
    }

    // ── InvalidateCacheAsync ──────────────────────────────────────────────────

    public async Task InvalidateCacheAsync(
        Guid              tenantId,
        Guid              connectionId,
        CancellationToken ct = default)
    {
        try
        {
            // Eliminamos la entrada; la próxima lectura forzará refrescar desde la URL
            await _db.CalendarCaches
                .Where(c => c.TenantId == tenantId && c.ConnectionId == connectionId)
                .ExecuteDeleteAsync(ct);

            _logger.LogInformation(
                "[CalendarService] Caché invalidada. TenantId={TenantId} ConnectionId={ConnectionId}",
                tenantId, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CalendarService] Error al invalidar caché. ConnectionId={ConnectionId}",
                connectionId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<ICalSlot> DeserializeSlots(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ICalSlot>>(json, JsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task UpdateConnectionSyncStatusAsync(
        Guid              connectionId,
        string            status,
        string?           error,
        CancellationToken ct)
    {
        try
        {
            await _db.CalendarConnections
                .Where(c => c.Id == connectionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.SyncStatus,    status)
                    .SetProperty(c => c.LastSyncedAt,  DateTimeOffset.UtcNow)
                    .SetProperty(c => c.SyncError,     error),
                    ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CalendarService] Error al actualizar SyncStatus. ConnectionId={ConnectionId}",
                connectionId);
        }
    }
}
