namespace ClinicBoost.Api.Infrastructure.Tenants;

/// <summary>
/// Implementación mutable de <see cref="ITenantContext"/>.
///
/// DISEÑO DE SEGURIDAD:
/// · La inicialización se hace una sola vez por request (guard de doble init).
/// · Initialize() es internal: solo <see cref="TenantMiddleware"/> puede llamarla.
/// · Los setters son private: los consumidores solo pueden leer.
/// · IsInitialized es el único indicador de estado; no existe estado "parcial".
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private bool _initialized;

    // ── Propiedades públicas (lectura) ────────────────────────────────────────

    public Guid?   TenantId  { get; private set; }
    public string? UserRole  { get; private set; }
    public Guid?   UserId    { get; private set; }
    public bool    IsInitialized => _initialized;

    // ── Inicialización (solo TenantMiddleware) ────────────────────────────────

    /// <summary>
    /// Establece el contexto a partir de los claims del JWT.
    /// Solo puede llamarse una vez por ciclo de vida Scoped (una por request).
    /// </summary>
    /// <exception cref="TenantContextException">
    /// Code 1006 si se intenta inicializar un contexto ya inicializado.
    /// </exception>
    internal void Initialize(Guid tenantId, string? userRole, Guid? userId)
    {
        if (_initialized)
            throw new TenantContextException(
                TenantContextErrorCode.ContextAlreadyInitialized,
                $"El TenantContext ya fue inicializado para TenantId={TenantId}. " +
                "No se puede reinicializar en el mismo request.");

        TenantId     = tenantId;
        UserRole     = TenantRole.IsValid(userRole) ? userRole : null;
        UserId       = userId;
        _initialized = true;
    }

    // ── Helpers de autorización ───────────────────────────────────────────────

    /// <inheritdoc/>
    public Guid RequireTenantId()
    {
        if (!_initialized || TenantId is null)
            throw new TenantContextException(
                TenantContextErrorCode.ContextNotInitialized,
                "Se requiere tenant_id pero el contexto no está inicializado. " +
                "Asegúrate de que el endpoint requiere autenticación y de que " +
                "TenantMiddleware está registrado en el pipeline.");

        return TenantId.Value;
    }

    /// <inheritdoc/>
    public bool HasAtLeastRole(string minimumRole) =>
        _initialized && TenantRole.HasAtLeast(UserRole, minimumRole);

    /// <inheritdoc/>
    public bool IsRole(string role) =>
        _initialized &&
        string.Equals(UserRole, role, StringComparison.Ordinal);
}
