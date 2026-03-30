namespace ClinicBoost.Domain.Security;

// ── RefreshToken ───────────────────────────────────────────────────────────────

/// <summary>
/// Token opaco de refresco. NUNCA se persiste el plaintext —
/// solo el hash SHA-256 (64 hex chars) se almacena.
/// Detección de reutilización: si un token revocado es presentado
/// de nuevo se activa la brecha y toda la familia se invalida.
/// </summary>
public sealed class RefreshToken
{
    public Guid   Id          { get; init; } = Guid.NewGuid();
    public Guid   TenantId    { get; init; }
    public Guid   UserId      { get; init; }

    /// <summary>SHA-256 del token plaintext, en hex minúsculas.</summary>
    public required string TokenHash  { get; init; }

    /// <summary>Identificador de la cadena de rotaciones.</summary>
    public Guid   FamilyId    { get; init; }

    public DateTimeOffset IssuedAt   { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt  { get; set; }

    // Mutable: se actualiza en rotación / revocación
    public DateTimeOffset? UsedAt        { get; set; }
    public DateTimeOffset? RevokedAt     { get; set; }
    public string?         RevokedReason { get; set; }
    public bool            IsRevoked     { get; set; }

    /// <summary>
    /// Marca la familia entera como comprometida si se detecta reutilización.
    /// </summary>
    public bool   IsCompromised    { get; set; }
    public string? ReplacedByTokenId { get; set; }

    public string? IpAddress  { get; init; }
    public string? UserAgent  { get; init; }
}

// ── SessionRevocation (JWT JTI blacklist) ────────────────────────────────────

/// <summary>
/// Registro de revocación de un JWT concreto identificado por su JTI.
/// Permite invalidar tokens antes de su expiración natural
/// (logout, cambio de contraseña, sospecha de compromiso).
/// </summary>
public sealed class SessionRevocation
{
    public Guid   Id          { get; init; } = Guid.NewGuid();
    public Guid   TenantId    { get; init; }
    public Guid   UserId      { get; init; }

    /// <summary>JWT ID claim (`jti`) que se está revocando.</summary>
    public required string Jti { get; init; }

    public DateTimeOffset RevokedAt     { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset JwtExpiresAt  { get; init; }
    public string?        Reason        { get; init; }
}
