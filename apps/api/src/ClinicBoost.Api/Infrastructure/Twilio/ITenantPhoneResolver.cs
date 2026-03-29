namespace ClinicBoost.Api.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// ITenantPhoneResolver
//
// PROPÓSITO
// ─────────
// Resuelve el tenant_id a partir del número de teléfono E.164 al que llegó
// la llamada (campo "To" en el webhook de Twilio).
//
// DISEÑO
// ──────
// · Cada tenant tiene un WhatsAppNumber único en la tabla tenants.
//   Este mismo número se usa también para voz cuando no hay número de voz
//   dedicado (escenario inicial de ClinicBoost).
// · El resolver hace una única consulta SQL (SELECT id FROM tenants
//   WHERE whatsapp_number = @number AND is_active = true) y almacena el
//   resultado en IMemoryCache con TTL configurable (por defecto 5 minutos).
// · En caso de fallo de BD, devuelve null en lugar de propagar la excepción,
//   permitiendo que el handler decida el comportamiento (rechazar el webhook
//   con 200 para que Twilio no reintente, o 500 para que reintente).
//
// ESCALABILIDAD
// ─────────────
// · Cache local (IMemoryCache) es suficiente en escenario de instancia única.
// · Si se escala horizontalmente, sustituir por IDistributedCache (Redis) sin
//   cambiar la interfaz (solo la implementación).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resuelve el <c>tenant_id</c> a partir del número E.164 asignado a un tenant.
/// Registrar como <b>Singleton</b> para aprovechar la caché de vida larga.
/// </summary>
public interface ITenantPhoneResolver
{
    /// <summary>
    /// Busca el tenant asociado al número <paramref name="phoneNumber"/>.
    /// </summary>
    /// <param name="phoneNumber">
    /// Número de teléfono en formato E.164 (p.ej. "+34612345678").
    /// Se normaliza internamente; no se aplica transformación al valor entrante.
    /// </param>
    /// <param name="ct">Token de cancelación.</param>
    /// <returns>
    /// El <see cref="Guid"/> del tenant activo que posee ese número,
    /// o <c>null</c> si no existe ningún tenant activo con ese número
    /// o si ocurre un error de infraestructura.
    /// </returns>
    Task<Guid?> ResolveAsync(string phoneNumber, CancellationToken ct = default);

    /// <summary>
    /// Invalida la entrada de caché para un número concreto.
    /// Llamar cuando se cambia el número de un tenant para evitar datos obsoletos.
    /// </summary>
    void Invalidate(string phoneNumber);
}
