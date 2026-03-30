namespace ClinicBoost.Api.Features.Audit;

/// <summary>
/// Gestiona una lista negra de JTIs de JWT con caché en memoria
/// para comprobaciones rápidas de revocación.
/// </summary>
public interface ISessionInvalidationService
{
    Task RevokeJtiAsync(Guid tenantId, Guid userId, string jti,
        DateTimeOffset jwtExpiresAt, string? reason = null);

    Task<bool> IsRevokedAsync(string jti);

    Task<int> CleanupExpiredAsync();
}
