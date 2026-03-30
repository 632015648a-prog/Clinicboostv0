using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ClinicBoost.Api.Features.Audit;

public sealed class SessionInvalidationService : ISessionInvalidationService
{
    private const int PositiveCacheSecs = 30;
    private const int NegativeCacheSecs = 5;

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SessionInvalidationService> _logger;

    public SessionInvalidationService(
        AppDbContext db,
        IMemoryCache cache,
        ILogger<SessionInvalidationService> logger)
    {
        _db    = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task RevokeJtiAsync(
        Guid tenantId, Guid userId, string jti,
        DateTimeOffset jwtExpiresAt, string? reason = null)
    {
        bool exists = await _db.SessionRevocations.AnyAsync(r => r.Jti == jti);
        if (exists) return;

        _db.SessionRevocations.Add(new SessionRevocation
        {
            TenantId      = tenantId,
            UserId        = userId,
            Jti           = jti,
            JwtExpiresAt  = jwtExpiresAt,
            Reason        = reason,
        });
        await _db.SaveChangesAsync();

        var ttl = jwtExpiresAt - DateTimeOffset.UtcNow;
        var effectiveTtl = ttl > TimeSpan.FromSeconds(PositiveCacheSecs)
            ? TimeSpan.FromSeconds(PositiveCacheSecs)
            : ttl;

        _cache.Set(CacheKey(jti), true,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl });
    }

    public async Task<bool> IsRevokedAsync(string jti)
    {
        if (_cache.TryGetValue(CacheKey(jti), out bool cached))
            return cached;

        bool revoked = await _db.SessionRevocations.AnyAsync(r => r.Jti == jti);

        _cache.Set(CacheKey(jti), revoked, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = revoked
                ? TimeSpan.FromSeconds(PositiveCacheSecs)
                : TimeSpan.FromSeconds(NegativeCacheSecs)
        });

        return revoked;
    }

    public async Task<int> CleanupExpiredAsync()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            int deleted = 0;
            try
            {
                deleted = await _db.SessionRevocations
                    .Where(r => r.JwtExpiresAt < now)
                    .ExecuteDeleteAsync();
            }
            catch (InvalidOperationException)
            {
                // Fallback para InMemory provider (tests)
                var expired = await _db.SessionRevocations
                    .Where(r => r.JwtExpiresAt < now)
                    .ToListAsync();
                _db.SessionRevocations.RemoveRange(expired);
                await _db.SaveChangesAsync();
                deleted = expired.Count;
            }

            _logger.LogInformation("CleanupExpired: eliminados {Count} JTIs expirados", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en CleanupExpiredAsync");
            return 0;
        }
    }

    private static string CacheKey(string jti) => $"jti_revoked:{jti}";
}
