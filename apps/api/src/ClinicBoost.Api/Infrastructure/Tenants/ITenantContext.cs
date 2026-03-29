namespace ClinicBoost.Api.Infrastructure.Tenants;

/// <summary>
/// Contexto inmutable del tenant activo para la petición HTTP actual.
///
/// Ciclo de vida: Scoped (una instancia por request).
/// Productor : TenantMiddleware (poblado a partir del JWT).
/// Consumidores:
///   · TenantDbContextInterceptor — inyecta GUCs en Postgres vía claim_tenant_context()
///   · Endpoints / features        — acceden a TenantId para filtros adicionales de capa app
///   · TenantAuthorizationMiddleware — comprueba rol mínimo por ruta
/// </summary>
public interface ITenantContext
{
    // ── Identidad ────────────────────────────────────────────────────────────

    /// <summary>
    /// UUID del tenant (clínica) activo.
    /// Null únicamente en endpoints públicos sin autenticación.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Rol del usuario dentro del tenant.
    /// Valores: owner | admin | therapist | receptionist | service | null.
    /// </summary>
    string? UserRole { get; }

    /// <summary>
    /// ID del usuario autenticado (claim 'sub' del JWT de Supabase Auth).
    /// Null si es una petición de servicio sin usuario humano.
    /// </summary>
    Guid? UserId { get; }

    // ── Estado ───────────────────────────────────────────────────────────────

    /// <summary>True cuando TenantId tiene valor (contexto plenamente inicializado).</summary>
    bool IsInitialized { get; }

    // ── Helpers de autorización ──────────────────────────────────────────────

    /// <summary>
    /// Devuelve TenantId garantizando que no es null.
    /// Lanza <see cref="TenantContextException"/> con code 1005 si no está inicializado.
    /// Usar en features que requieren tenant obligatorio.
    /// </summary>
    Guid RequireTenantId();

    /// <summary>
    /// True si el usuario tiene al menos el rol <paramref name="minimumRole"/>.
    /// Devuelve false si el contexto no está inicializado.
    /// </summary>
    bool HasAtLeastRole(string minimumRole);

    /// <summary>
    /// True si el rol del usuario es exactamente <paramref name="role"/>.
    /// </summary>
    bool IsRole(string role);
}
