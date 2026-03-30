namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// ICalendarCacheStore
//
// Abstracción de la caché persistida de calendarios.
//
// CONTRATO
// ────────
//  · GetAsync   → devuelve la entrada existente o null (sin tirar excepción).
//  · UpsertAsync → inserta o actualiza atómicamente (ON CONFLICT DO UPDATE).
//  · MarkErrorAsync → actualiza solo LastErrorMessage y UpdatedAt para registrar
//                     fallos transitorios sin sobreescribir datos válidos.
//
// EXTENSIBILIDAD FUTURA
// ─────────────────────
//  · EfCalendarCacheStore usa EF Core + Postgres (implementación actual).
//  · En el futuro se puede añadir RedisCalendarCacheStore implementando esta
//    misma interfaz y registrándola en DI; el ICalendarService no cambia.
//  · Si se necesita cache L1 (in-process) + L2 (Postgres) se puede implementar
//    un decorator sobre esta interfaz sin tocar el orquestador.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Caché persistida de entradas de calendario.
/// Implementaciones disponibles: <see cref="EfCalendarCacheStore"/>.
/// En el futuro: RedisCalendarCacheStore, HybridCalendarCacheStore.
/// </summary>
public interface ICalendarCacheStore
{
    /// <summary>
    /// Obtiene la entrada de caché para una conexión concreta.
    /// Devuelve <c>null</c> si no existe entrada; nunca lanza excepción de infraestructura.
    /// </summary>
    Task<CalendarCache?> GetAsync(Guid tenantId, Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Inserta o actualiza la entrada de caché de forma atómica.
    /// Equivale a un INSERT … ON CONFLICT (tenant_id, connection_id) DO UPDATE.
    /// </summary>
    Task UpsertAsync(CalendarCache entry, CancellationToken ct = default);

    /// <summary>
    /// Marca un error de lectura sin sobreescribir los slots válidos existentes.
    /// Actualiza solo <see cref="CalendarCache.LastErrorMessage"/> y <see cref="CalendarCache.UpdatedAt"/>.
    /// Si no existe entrada para esa conexión, no hace nada (no hay datos que conservar).
    /// </summary>
    Task MarkErrorAsync(Guid tenantId, Guid connectionId, string errorMessage, CancellationToken ct = default);
}
