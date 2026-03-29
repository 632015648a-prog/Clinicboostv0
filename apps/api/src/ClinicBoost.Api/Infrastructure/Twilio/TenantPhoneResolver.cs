using ClinicBoost.Api.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ClinicBoost.Api.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// TenantPhoneResolver
//
// Implementación con IMemoryCache (in-process).
// · Clave de caché: "phone_tenant:{phoneNumber}"
// · TTL: PhoneCacheTtl (configurable, por defecto 5 minutos)
// · Valor cacheado: Guid? (null cuando no existe el tenant)
//   → Cachear el "no encontrado" evita hammering de BD ante números desconocidos.
//   → TTL corto (30 s) para el caso "no encontrado" en lugar de TTL completo.
//
// THREAD SAFETY
// ─────────────
// IMemoryCache es thread-safe. IServiceScopeFactory crea scopes independientes
// por cada llamada, garantizando que el DbContext sea Scoped correctamente.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Resuelve tenant_id desde un número E.164 con caché en memoria.
/// Registrar como <b>Singleton</b>.
/// </summary>
public sealed class TenantPhoneResolver : ITenantPhoneResolver
{
    private static readonly TimeSpan PhoneCacheTtl      = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NotFoundCacheTtl   = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache        _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenantPhoneResolver> _logger;

    public TenantPhoneResolver(
        IMemoryCache                   cache,
        IServiceScopeFactory           scopeFactory,
        ILogger<TenantPhoneResolver>   logger)
    {
        _cache        = cache;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    /// <inheritdoc/>
    public async Task<Guid?> ResolveAsync(string phoneNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return null;

        var cacheKey = CacheKey(phoneNumber);

        // ── Cache hit ────────────────────────────────────────────────────────
        if (_cache.TryGetValue(cacheKey, out Guid? cached))
        {
            _logger.LogDebug(
                "[TenantPhoneResolver] Cache hit. Phone={Phone} TenantId={TenantId}",
                phoneNumber, cached?.ToString() ?? "null");
            return cached;
        }

        // ── Cache miss: consultar BD via scope independiente ─────────────────
        // TenantPhoneResolver es Singleton, pero AppDbContext es Scoped.
        // Se crea un scope explícito para no contaminar el DbContext del request.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // La consulta busca por whatsapp_number porque ese campo almacena
            // el número Twilio asignado, tanto para WhatsApp como para voz.
            // Índice: ix_tenants_whatsapp_number (ver migración).
            var tenantId = await db.Tenants
                .AsNoTracking()
                .Where(t => t.WhatsAppNumber == phoneNumber && t.IsActive)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(ct);

            // Cachear resultado (incluyendo null = "no encontrado")
            var ttl = tenantId.HasValue ? PhoneCacheTtl : NotFoundCacheTtl;
            _cache.Set(cacheKey, tenantId, ttl);

            _logger.LogDebug(
                "[TenantPhoneResolver] Cache miss resuelto. " +
                "Phone={Phone} TenantId={TenantId} CacheTtl={Ttl}",
                phoneNumber, tenantId?.ToString() ?? "null", ttl);

            return tenantId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[TenantPhoneResolver] Error al resolver tenant por teléfono. Phone={Phone}",
                phoneNumber);
            // No cachear en caso de error; el siguiente request reintentará la BD.
            return null;
        }
    }

    /// <inheritdoc/>
    public void Invalidate(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return;

        _cache.Remove(CacheKey(phoneNumber));
        _logger.LogDebug(
            "[TenantPhoneResolver] Caché invalidada. Phone={Phone}", phoneNumber);
    }

    private static string CacheKey(string phoneNumber) =>
        $"phone_tenant:{phoneNumber}";
}
