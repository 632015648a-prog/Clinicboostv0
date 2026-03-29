using System.Security.Claims;

namespace ClinicBoost.Api.Infrastructure.Tenants;

/// <summary>
/// Extrae y valida claims de tenant desde un <see cref="ClaimsPrincipal"/>.
///
/// Responsabilidad única: parsing + validación tipada de claims.
/// Separado del middleware para facilitar tests unitarios sin HttpContext.
///
/// ORDEN DE BÚSQUEDA para cada claim:
///   1. Claim directo en el JWT (ej. "tenant_id")
///   2. Claim de app_metadata de Supabase (ej. "app_metadata.tenant_id")
///   3. Claim en objeto anidado "app_metadata" como JSON (compatibilidad GoTrue)
/// </summary>
public sealed class ClaimsExtractor
{
    // ── Nombres de claims conocidos ───────────────────────────────────────────

    private static readonly string[] TenantIdClaimNames =
    [
        "tenant_id",
        "app_metadata.tenant_id",
        "https://clinicboost.io/tenant_id"   // namespace personalizado para prod
    ];

    private static readonly string[] UserRoleClaimNames =
    [
        "user_role",
        "app_metadata.user_role",
        "https://clinicboost.io/user_role"
    ];

    // ── API pública ───────────────────────────────────────────────────────────

    /// <summary>
    /// Extrae el tenant_id del principal.
    /// </summary>
    /// <returns>
    /// (success: true, tenantId: Guid) si se encuentra un UUID válido.
    /// (success: false, error: código) en caso contrario.
    /// </returns>
    public ExtractionResult<Guid> ExtractTenantId(ClaimsPrincipal principal)
    {
        var raw = FindFirstValue(principal, TenantIdClaimNames);

        // El claim no existe en absoluto → MissingTenantId (1001)
        if (raw is null)
            return ExtractionResult<Guid>.Fail(TenantContextErrorCode.MissingTenantId,
                "El JWT no contiene el claim tenant_id.");

        // El claim existe pero está vacío o es solo espacios → InvalidTenantIdFormat (1002)
        // (incluye el caso InlineData("  ") del test)
        if (!Guid.TryParse(raw, out var tenantId))
            return ExtractionResult<Guid>.Fail(TenantContextErrorCode.InvalidTenantIdFormat,
                $"El claim tenant_id '{raw.Trim()}' no es un UUID válido.");

        return ExtractionResult<Guid>.Ok(tenantId);
    }

    /// <summary>
    /// Extrae y valida el user_role del principal.
    /// Si no está presente devuelve null (no es error — puede ser un rol por defecto).
    /// Si está presente pero no es válido, devuelve error.
    /// </summary>
    public ExtractionResult<string?> ExtractUserRole(ClaimsPrincipal principal)
    {
        var raw = FindFirstValue(principal, UserRoleClaimNames);

        if (string.IsNullOrWhiteSpace(raw))
            return ExtractionResult<string?>.Ok(null);   // Ausencia no es error

        if (!TenantRole.IsValid(raw))
            return ExtractionResult<string?>.Fail(TenantContextErrorCode.InvalidRole,
                $"El claim user_role '{raw}' no es un rol permitido. " +
                $"Valores válidos: owner|admin|therapist|receptionist|service.");

        return ExtractionResult<string?>.Ok(raw);
    }

    /// <summary>
    /// Extrae el user_id (claim 'sub') del principal.
    /// </summary>
    public ExtractionResult<Guid?> ExtractUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)   // "nameidentifier" estándar
               ?? principal.FindFirstValue("sub");                       // JWT estándar

        if (string.IsNullOrWhiteSpace(raw))
            return ExtractionResult<Guid?>.Ok(null);   // Ausente es válido (service account)

        if (!Guid.TryParse(raw, out var userId))
            return ExtractionResult<Guid?>.Fail(TenantContextErrorCode.InvalidUserIdFormat,
                $"El claim sub '{raw}' no es un UUID válido.");

        return ExtractionResult<Guid?>.Ok(userId);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Busca el primer claim presente (no nulo ni vacío-string).
    /// NOTA: no filtra por IsNullOrWhiteSpace — los valores con solo espacios
    /// se devuelven tal cual para que el caller pueda distinguir entre
    /// "claim ausente" (MissingXxx) e "claim con valor inválido" (InvalidXxxFormat).
    /// </summary>
    private static string? FindFirstValue(ClaimsPrincipal principal, string[] names)
    {
        foreach (var name in names)
        {
            var value = principal.FindFirstValue(name);
            if (value is not null)   // el claim existe (aunque sea "  ")
                return value;
        }
        return null;
    }
}

/// <summary>
/// Resultado de una operación de extracción de claim.
/// Patrón Railway-oriented para evitar excepciones en el path normal.
/// </summary>
public readonly struct ExtractionResult<T>
{
    public bool                    Success   { get; }
    public T?                      Value     { get; }
    public TenantContextErrorCode? ErrorCode { get; }
    public string?                 ErrorMsg  { get; }

    private ExtractionResult(bool success, T? value,
        TenantContextErrorCode? code, string? msg)
    {
        Success   = success;
        Value     = value;
        ErrorCode = code;
        ErrorMsg  = msg;
    }

    public static ExtractionResult<T> Ok(T value) =>
        new(true, value, null, null);

    public static ExtractionResult<T> Fail(TenantContextErrorCode code, string msg) =>
        new(false, default, code, msg);
}
