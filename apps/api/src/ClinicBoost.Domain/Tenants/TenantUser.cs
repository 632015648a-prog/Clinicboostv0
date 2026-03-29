using ClinicBoost.Domain.Common;

namespace ClinicBoost.Domain.Tenants;

/// <summary>
/// Usuario de Supabase Auth vinculado a un tenant con un rol.
/// Un mismo auth_user_id puede pertenecer a múltiples tenants.
/// Roles: owner &gt; admin &gt; therapist | receptionist
/// </summary>
public sealed class TenantUser : BaseEntity
{
    /// <summary>UUID del usuario en auth.users de Supabase GoTrue.</summary>
    public Guid AuthUserId { get; init; }

    public required string Role { get; set; }       // owner | admin | therapist | receptionist
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// Conexión con un ERP / sistema de agenda externo.
/// Los tokens OAuth se almacenan cifrados (access_token_enc, refresh_token_enc).
/// </summary>
public sealed class CalendarConnection : BaseEntity
{
    public required string Provider { get; set; }   // ical | google | clinicalia | fisify | janeapp | custom_ical
    public required string DisplayName { get; set; }
    public string? IcalUrl { get; set; }

    // Tokens cifrados — la app descifra en memoria; NUNCA en texto plano en logs
    public byte[]? AccessTokenEnc { get; set; }
    public byte[]? RefreshTokenEnc { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? SyncError { get; set; }
    public required string SyncStatus { get; set; } = "pending"; // pending | ok | error | disabled

    public bool IsPrimary { get; set; } = false;
    public bool IsActive { get; set; } = true;
}
