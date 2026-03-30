namespace ClinicBoost.Api.Features.Audit;

/// <summary>
/// Registra eventos de seguridad y autenticación de forma asíncrona.
/// GARANTÍA: nunca lanza excepción — errores se registran en ILogger internamente.
/// </summary>
public interface ISecurityAuditService
{
    /// <summary>Evento de autenticación (login, logout, token rotation…).</summary>
    Task RecordAuthAsync(
        Guid   tenantId,
        string action,
        string outcome      = "success",
        Guid?  actorId      = null,
        string? actorRole   = null,
        string? ipAddress   = null,
        string? userAgent   = null,
        string? correlationId = null,
        string? metadata    = null,
        int    riskScore    = -1);

    /// <summary>Evento de datos (create, update, delete).</summary>
    Task RecordDataAsync(
        Guid   tenantId,
        string action,
        string outcome      = "success",
        Guid?  actorId      = null,
        string? actorRole   = null,
        string? entityType  = null,
        string? entityId    = null,
        string? metadata    = null,
        int    riskScore    = -1);

    /// <summary>Evento de seguridad crítico (breach, intrusión…). Siempre Critical.</summary>
    Task RecordSecurityAsync(
        Guid   tenantId,
        string action,
        string outcome    = "success",
        Guid?  actorId    = null,
        string? ipAddress = null,
        string? metadata  = null,
        int    riskScore  = 10);
}
