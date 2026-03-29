namespace ClinicBoost.Api.Infrastructure.Tenants;

/// <summary>
/// Valores permitidos para el rol de un usuario dentro de un tenant.
/// Mapeados 1:1 con los roles definidos en las políticas RLS de Postgres.
/// </summary>
public static class TenantRole
{
    public const string Owner       = "owner";
    public const string Admin       = "admin";
    public const string Therapist   = "therapist";
    public const string Receptionist = "receptionist";
    public const string Service     = "service";   // backend worker sin usuario humano

    private static readonly HashSet<string> _valid = new(StringComparer.Ordinal)
    {
        Owner, Admin, Therapist, Receptionist, Service
    };

    /// <summary>True si el valor es uno de los roles conocidos.</summary>
    public static bool IsValid(string? role) =>
        role is not null && _valid.Contains(role);

    /// <summary>
    /// Jerarquía de roles para comprobaciones de "tiene como mínimo X rol".
    /// Cuanto mayor el número, más privilegios.
    ///
    /// DISEÑO: therapist y receptionist son roles paralelos con el mismo nivel (1).
    /// Ninguno puede sustituir al otro; ambos se diferencian por dominio de trabajo,
    /// no por jerarquía de autorización.
    ///   service=0 → receptionist=therapist=1 → admin=2 → owner=3
    /// </summary>
    private static readonly Dictionary<string, int> _rank = new(StringComparer.Ordinal)
    {
        [Service]      = 0,
        [Therapist]    = 1,
        [Receptionist] = 1,   // mismo nivel que therapist — roles paralelos
        [Admin]        = 2,
        [Owner]        = 3
    };

    /// <summary>
    /// True si <paramref name="userRole"/> tiene al menos el nivel de <paramref name="minimumRole"/>.
    ///
    /// NOTA SOBRE ROLES PARALELOS (therapist / receptionist):
    /// Ambos tienen el mismo rango (1) pero son dominios de trabajo distintos.
    /// "admin" sí puede hacer todo lo de therapist o receptionist (rango 2 > 1),
    /// pero un therapist NO puede sustituir a un receptionist y viceversa.
    /// Regla: si los rangos son iguales, el rol debe ser exactamente el mismo.
    /// </summary>
    public static bool HasAtLeast(string? userRole, string minimumRole)
    {
        if (!IsValid(userRole) || !_rank.TryGetValue(minimumRole, out var minRank))
            return false;
        if (!_rank.TryGetValue(userRole!, out var userRank))
            return false;

        // Si el usuario tiene rango superior → siempre autorizado
        if (userRank > minRank)
            return true;

        // Si los rangos son iguales → solo si el rol es exactamente el mismo.
        // Esto maneja el caso de roles paralelos (therapist ≠ receptionist).
        if (userRank == minRank)
            return string.Equals(userRole, minimumRole, StringComparison.Ordinal);

        return false;
    }
}
