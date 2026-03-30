namespace ClinicBoost.Api.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// ICalendarService
//
// Interfaz pública del orquestador de calendarios.
// Abstrae la política cache-aside + freshness + fallback para los consumidores.
//
// USO PREVISTO
// ────────────
//  · ToolRegistry.GetAvailableSlotsAsync  → consulta slots para el agente IA.
//  · AppointmentService.GetAvailableSlots → consulta slots para el endpoint REST.
//  · En el futuro: background job de precalentamiento de caché.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Orquestador de calendarios iCal: cache-aside con freshness, timeout y fallback.
/// </summary>
public interface ICalendarService
{
    /// <summary>
    /// Devuelve los slots del calendario de la conexión indicada.
    ///
    /// Política:
    ///  1. Si hay caché válida (edad &lt; FreshnessTtl) → devuelve Fresh.
    ///  2. Si la caché está stale → intenta leer desde la URL iCal.
    ///     a. Si la lectura tiene éxito → actualiza caché y devuelve Fresh.
    ///     b. Si falla y hay caché (edad &lt; MaxStaleAge) → devuelve Stale con log.
    ///     c. Si falla y no hay caché (o es demasiado antigua) → devuelve Unavailable.
    ///  3. Si no hay caché → intenta leer directamente.
    /// </summary>
    /// <param name="tenantId">ID del tenant.</param>
    /// <param name="connectionId">ID de la CalendarConnection (debe tener IcalUrl != null).</param>
    /// <param name="ct">Token de cancelación.</param>
    Task<CalendarResult> GetSlotsAsync(
        Guid              tenantId,
        Guid              connectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Invalida la caché de una conexión (p.ej., tras actualizar la URL iCal).
    /// La próxima llamada a GetSlotsAsync forzará una lectura fresca.
    /// </summary>
    Task InvalidateCacheAsync(
        Guid              tenantId,
        Guid              connectionId,
        CancellationToken ct = default);
}
