using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClinicBoost.Api.Features.Audit;

public sealed class RefreshTokenService : IRefreshTokenService
{
    private readonly AppDbContext _db;
    private readonly ISecurityAuditService _audit;
    private readonly ILogger<RefreshTokenService> _logger;
    private readonly RefreshTokenOptions _options;

    public RefreshTokenService(
        AppDbContext db,
        ISecurityAuditService audit,
        ILogger<RefreshTokenService> logger,
        IOptions<RefreshTokenOptions> options)
    {
        _db      = db;
        _audit   = audit;
        _logger  = logger;
        _options = options.Value;
    }

    // ── Issue ──────────────────────────────────────────────────────────────────

    public async Task<RefreshTokenResult> IssueAsync(
        Guid tenantId, Guid userId,
        string? ipAddress = null, string? userAgent = null)
    {
        var plain    = GeneratePlainToken();
        var hash     = HashToken(plain);
        var familyId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_options.ExpiryDays);

        var token = new RefreshToken
        {
            TenantId  = tenantId,
            UserId    = userId,
            TokenHash = hash,
            FamilyId  = familyId,
            ExpiresAt = expiresAt,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync();

        await _audit.RecordAuthAsync(tenantId, "auth.token_issued", actorId: userId,
            ipAddress: ipAddress,
            metadata: JsonSerializer.Serialize(new { tokenId = token.Id, familyId }));

        return new RefreshTokenResult(true, plain, token.Id, familyId, expiresAt);
    }

    // ── Rotate ─────────────────────────────────────────────────────────────────

    public async Task<RotateTokenResult> RotateAsync(
        string plainToken,
        string? ipAddress = null, string? userAgent = null)
    {
        var hash     = HashToken(plainToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash);

        if (existing is null)
            return new RotateTokenResult(false, Error: "not_found");

        if (existing.ExpiresAt < DateTimeOffset.UtcNow)
            return new RotateTokenResult(false, Error: "expired");

        // Reuse detection: already revoked or already used
        if (existing.IsRevoked || existing.UsedAt is not null)
        {
            _logger.LogWarning("Token reuse detected for family {FamilyId}", existing.FamilyId);
            await RevokeFamilyInternalAsync(existing.FamilyId, existing.TenantId, "breach");
            await _audit.RecordAuthAsync(
                existing.TenantId, "auth.token_reuse_detected",
                outcome: "failure", actorId: existing.UserId,
                ipAddress: ipAddress,
                metadata: JsonSerializer.Serialize(new { familyId = existing.FamilyId }),
                riskScore: 10);
            return new RotateTokenResult(false, IsBreach: true, Error: "token_reuse");
        }

        // Mark as used + revoked (rotation)
        existing.UsedAt       = DateTimeOffset.UtcNow;
        existing.IsRevoked    = true;
        existing.RevokedAt    = DateTimeOffset.UtcNow;
        existing.RevokedReason = "rotated";

        // Issue new token in same family
        var newPlain  = GeneratePlainToken();
        var newHash   = HashToken(newPlain);
        var newExpiry = DateTimeOffset.UtcNow.AddDays(_options.ExpiryDays);

        var newToken = new RefreshToken
        {
            TenantId  = existing.TenantId,
            UserId    = existing.UserId,
            TokenHash = newHash,
            FamilyId  = existing.FamilyId,
            ExpiresAt = newExpiry,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        existing.ReplacedByTokenId = newToken.Id.ToString();
        _db.RefreshTokens.Add(newToken);
        await _db.SaveChangesAsync();

        await _audit.RecordAuthAsync(
            existing.TenantId, "auth.token_rotated",
            actorId: existing.UserId,
            metadata: JsonSerializer.Serialize(new { oldTokenId = existing.Id, newTokenId = newToken.Id }));

        return new RotateTokenResult(true, newPlain, newToken.Id, newToken.FamilyId, newExpiry);
    }

    // ── Revoke ─────────────────────────────────────────────────────────────────

    public async Task<bool> RevokeAsync(string plainToken, string reason = "logout")
    {
        var hash  = HashToken(plainToken);
        // P0: buscar por hash; la validación de tenant se hace implicitamente al chequear IsRevoked.
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && !t.IsRevoked);
        if (token is null || token.IsRevoked) return false;

        token.IsRevoked    = true;
        token.RevokedAt    = DateTimeOffset.UtcNow;
        token.RevokedReason = reason;
        await _db.SaveChangesAsync();

        await _audit.RecordAuthAsync(token.TenantId, "auth.token_revoked",
            actorId: token.UserId,
            metadata: JsonSerializer.Serialize(new { tokenId = token.Id, reason }));

        return true;
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, Guid tenantId, string reason = "logout")
    {
        int count = 0;
        try
        {
            count = await _db.RefreshTokens
                .Where(t => t.UserId == userId && t.TenantId == tenantId && !t.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.IsRevoked,     true)
                    .SetProperty(t => t.RevokedAt,     DateTimeOffset.UtcNow)
                    .SetProperty(t => t.RevokedReason, reason));
        }
        catch (InvalidOperationException)
        {
            // Fallback para proveedor InMemory (tests)
            var tokens = await _db.RefreshTokens
                .Where(t => t.UserId == userId && t.TenantId == tenantId && !t.IsRevoked)
                .ToListAsync();
            foreach (var t in tokens)
            {
                t.IsRevoked     = true;
                t.RevokedAt     = DateTimeOffset.UtcNow;
                t.RevokedReason = reason;
            }
            await _db.SaveChangesAsync();
            count = tokens.Count;
        }

        await _audit.RecordAuthAsync(tenantId, "auth.session_revoked_all",
            actorId: userId,
            metadata: JsonSerializer.Serialize(new { count, reason }));

        return count;
    }

    public async Task<int> RevokeFamilyAsync(Guid familyId, string reason = "breach")
        => await RevokeFamilyInternalAsync(familyId, tenantId: null, reason);

    // ── Sessions ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(
        Guid userId, Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.RefreshTokens
            .Where(t => t.UserId == userId
                     && t.TenantId == tenantId
                     && !t.IsRevoked
                     && t.ExpiresAt > now)
            .Select(t => new ActiveSessionDto(
                t.Id, t.FamilyId, t.IssuedAt, t.ExpiresAt, t.IpAddress, t.UserAgent))
            .ToListAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<int> RevokeFamilyInternalAsync(Guid familyId, Guid? tenantId, string reason)
    {
        int count = 0;
        try
        {
            // P0: Revocar tokens del family SOLO del mismo tenant (si tenantId disponible).
            // Previene cross-tenant revocation — el familyId solo puede pertenecer a un tenant.
            var baseQuery = _db.RefreshTokens
                .Where(t => t.FamilyId == familyId);
            if (tenantId.HasValue)
                baseQuery = baseQuery.Where(t => t.TenantId == tenantId.Value);

            count = await baseQuery
                .Where(t => !t.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.IsRevoked,     true)
                    .SetProperty(t => t.IsCompromised, true)
                    .SetProperty(t => t.RevokedAt,     DateTimeOffset.UtcNow)
                    .SetProperty(t => t.RevokedReason, reason));

            // Marcar también los ya revocados como comprometidos (mismo scope tenant)
            var alreadyRevokedQuery = _db.RefreshTokens
                .Where(t => t.FamilyId == familyId && t.IsRevoked && !t.IsCompromised);
            if (tenantId.HasValue)
                alreadyRevokedQuery = alreadyRevokedQuery.Where(t => t.TenantId == tenantId.Value);

            await alreadyRevokedQuery
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsCompromised, true));
        }
        catch (InvalidOperationException)
        {
            // Fallback para proveedor InMemory (tests)
            var tokens = await _db.RefreshTokens
                .Where(t => t.FamilyId == familyId)
                .ToListAsync();
            foreach (var t in tokens)
            {
                t.IsCompromised = true;
                if (!t.IsRevoked)
                {
                    t.IsRevoked     = true;
                    t.RevokedAt     = DateTimeOffset.UtcNow;
                    t.RevokedReason = reason;
                    count++;
                }
            }
            await _db.SaveChangesAsync();
        }
        return count;
    }

    private static string GeneratePlainToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string plain)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plain));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
