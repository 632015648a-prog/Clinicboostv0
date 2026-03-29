namespace ClinicBoost.Api.Infrastructure.Tenants;

/// <summary>
/// Errores tipados del subsistema de contexto de tenant.
/// Cada caso tiene un código de error único para facilitar búsqueda en logs y alertas.
/// </summary>
public enum TenantContextErrorCode
{
    /// <summary>No se encontró el claim tenant_id en el JWT.</summary>
    MissingTenantId = 1001,

    /// <summary>El claim tenant_id no es un UUID válido.</summary>
    InvalidTenantIdFormat = 1002,

    /// <summary>El claim user_role tiene un valor no reconocido.</summary>
    InvalidRole = 1003,

    /// <summary>El claim sub (user_id) no es un UUID válido.</summary>
    InvalidUserIdFormat = 1004,

    /// <summary>El contexto aún no ha sido inicializado antes de acceder a él.</summary>
    ContextNotInitialized = 1005,

    /// <summary>Intento de re-inicializar un contexto ya inicializado en el mismo request.</summary>
    ContextAlreadyInitialized = 1006,
}

/// <summary>
/// Excepción lanzada cuando el contexto de tenant tiene datos inválidos o está ausente.
/// No se captura globalmente para evitar swallow silencioso; llega al ExceptionMiddleware.
/// </summary>
public sealed class TenantContextException : Exception
{
    public TenantContextErrorCode Code { get; }

    public TenantContextException(TenantContextErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public TenantContextException(TenantContextErrorCode code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>
    /// Mensaje estructurado para Serilog (incluye el código para alertas).
    /// </summary>
    public string StructuredMessage =>
        $"[TenantCtx-{(int)Code}] {Code}: {Message}";
}
