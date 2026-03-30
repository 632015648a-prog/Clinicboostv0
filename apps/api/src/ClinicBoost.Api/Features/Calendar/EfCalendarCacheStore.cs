using ClinicBoost.Api.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// EfCalendarCacheStore
//
// Implementación de ICalendarCacheStore sobre EF Core + Postgres.
//
// DISEÑO
// ──────
//  · GetAsync     → SELECT con AsNoTracking; rápido, no reserva change tracker.
//  · UpsertAsync  → Add o Update; EF detecta si la entidad existe con FindAsync.
//                   Alternativa: SQL raw con ON CONFLICT DO UPDATE (más eficiente
//                   en alta concurrencia; elegir si los tests de carga lo requieren).
//  · MarkErrorAsync → ExecuteUpdateAsync directamente (sin cargar la entidad completa).
//
// REGISTRO EN DI
// ──────────────
//  Scoped (depende de AppDbContext que es Scoped).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Implementación de <see cref="ICalendarCacheStore"/> usando EF Core + Postgres.
/// </summary>
public sealed class EfCalendarCacheStore : ICalendarCacheStore
{
    private readonly AppDbContext              _db;
    private readonly ILogger<EfCalendarCacheStore> _logger;

    public EfCalendarCacheStore(AppDbContext db, ILogger<EfCalendarCacheStore> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── GetAsync ──────────────────────────────────────────────────────────────

    public async Task<CalendarCache?> GetAsync(
        Guid tenantId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        try
        {
            return await _db.CalendarCaches
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.TenantId == tenantId && c.ConnectionId == connectionId,
                    ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CalendarCache] Error al leer caché. TenantId={TenantId} ConnectionId={ConnectionId}",
                tenantId, connectionId);
            return null; // fallback: actúa como si no hubiera caché
        }
    }

    // ── UpsertAsync ───────────────────────────────────────────────────────────

    public async Task UpsertAsync(CalendarCache entry, CancellationToken ct = default)
    {
        try
        {
            // Comprobamos si ya existe (tracked) para decidir Add vs Update
            var existing = await _db.CalendarCaches
                .FirstOrDefaultAsync(
                    c => c.TenantId == entry.TenantId && c.ConnectionId == entry.ConnectionId,
                    ct);

            if (existing is null)
            {
                _db.CalendarCaches.Add(entry);
            }
            else
            {
                // Actualizamos solo los campos mutables; Id y TenantId son inmutables
                existing.SlotsJson        = entry.SlotsJson;
                existing.FetchedAtUtc     = entry.FetchedAtUtc;
                existing.ExpiresAtUtc     = entry.ExpiresAtUtc;
                existing.ETag             = entry.ETag;
                existing.LastModifiedUtc  = entry.LastModifiedUtc;
                existing.ContentHash      = entry.ContentHash;
                existing.LastErrorMessage = null; // limpiamos el error al tener datos frescos
                existing.UpdatedAt        = DateTimeOffset.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "[CalendarCache] Caché upserted. TenantId={TenantId} ConnectionId={ConnectionId} ExpiresAt={Expires}",
                entry.TenantId, entry.ConnectionId, entry.ExpiresAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CalendarCache] Error al persistir caché. TenantId={TenantId} ConnectionId={ConnectionId}",
                entry.TenantId, entry.ConnectionId);
            // No re-lanzamos: el error de caché no debe bloquear la respuesta
        }
    }

    // ── MarkErrorAsync ────────────────────────────────────────────────────────

    public async Task MarkErrorAsync(
        Guid   tenantId,
        Guid   connectionId,
        string errorMessage,
        CancellationToken ct = default)
    {
        try
        {
            var updated = await _db.CalendarCaches
                .Where(c => c.TenantId == tenantId && c.ConnectionId == connectionId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(c => c.LastErrorMessage, errorMessage)
                        .SetProperty(c => c.UpdatedAt,        DateTimeOffset.UtcNow),
                    ct);

            if (updated == 0)
            {
                _logger.LogDebug(
                    "[CalendarCache] MarkError: no existía entrada. TenantId={TenantId} ConnectionId={ConnectionId}",
                    tenantId, connectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CalendarCache] Error en MarkErrorAsync. TenantId={TenantId} ConnectionId={ConnectionId}",
                tenantId, connectionId);
        }
    }
}
