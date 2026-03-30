namespace ClinicBoost.Api.Features.Audit;

// ── Result types ───────────────────────────────────────────────────────────────

public record RefreshTokenResult(
    bool Success,
    string? PlainToken  = null,
    Guid?   TokenId     = null,
    Guid?   FamilyId    = null,
    DateTimeOffset? ExpiresAt = null,
    string? Error       = null);

public record RotateTokenResult(
    bool Success,
    string? PlainToken  = null,
    Guid?   TokenId     = null,
    Guid?   FamilyId    = null,
    DateTimeOffset? ExpiresAt = null,
    bool    IsBreach    = false,
    string? Error       = null);

public record ActiveSessionDto(
    Guid   TokenId,
    Guid   FamilyId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? IpAddress,
    string? UserAgent);

// ── Interface ──────────────────────────────────────────────────────────────────

/// <summary>
/// Ciclo de vida completo de refresh tokens opacos.
/// El plaintext NUNCA se persiste — solo su hash SHA-256.
/// </summary>
public interface IRefreshTokenService
{
    Task<RefreshTokenResult> IssueAsync(
        Guid tenantId, Guid userId,
        string? ipAddress = null, string? userAgent = null);

    Task<RotateTokenResult> RotateAsync(
        string plainToken,
        string? ipAddress = null, string? userAgent = null);

    Task<bool> RevokeAsync(string plainToken, string reason = "logout");

    Task<int> RevokeAllForUserAsync(Guid userId, Guid tenantId, string reason = "logout");

    Task<int> RevokeFamilyAsync(Guid familyId, string reason = "breach");

    Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(Guid userId, Guid tenantId);
}
