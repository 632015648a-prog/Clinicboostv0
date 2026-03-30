using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClinicBoost.Api.Features.Audit;

/// <summary>
/// Implementación de ISecurityAuditService.
///
/// Reutiliza la entidad AuditLog existente (ClinicBoost.Domain.Common)
/// añadiendo campos semánticos en OldValues/NewValues como JSON.
/// La persistencia es fire-and-forget: nunca bloquea el hilo de negocio.
/// </summary>
public sealed class SecurityAuditService : ISecurityAuditService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecurityAuditService> _logger;

    public SecurityAuditService(
        IServiceScopeFactory scopeFactory,
        ILogger<SecurityAuditService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public Task RecordAuthAsync(
        Guid tenantId, string action, string outcome = "success",
        Guid? actorId = null, string? actorRole = null,
        string? ipAddress = null, string? userAgent = null,
        string? correlationId = null, string? metadata = null, int riskScore = -1)
    {
        var (severity, resolvedRisk) = ResolveSeverityAndRisk("auth", action, outcome, riskScore);
        _ = PersistAsync(Build(tenantId, "auth", action, outcome, actorId,
            severity, resolvedRisk, ipAddress, metadata: metadata));
        return Task.CompletedTask;
    }

    public Task RecordDataAsync(
        Guid tenantId, string action, string outcome = "success",
        Guid? actorId = null, string? actorRole = null,
        string? entityType = null, string? entityId = null,
        string? metadata = null, int riskScore = -1)
    {
        var (severity, resolvedRisk) = ResolveSeverityAndRisk("data", action, outcome, riskScore);
        _ = PersistAsync(Build(tenantId, "data", action, outcome, actorId,
            severity, resolvedRisk, entityType: entityType, entityId: entityId, metadata: metadata));
        return Task.CompletedTask;
    }

    public Task RecordSecurityAsync(
        Guid tenantId, string action, string outcome = "success",
        Guid? actorId = null, string? ipAddress = null,
        string? metadata = null, int riskScore = 10)
    {
        _ = PersistAsync(Build(tenantId, "security", action, outcome, actorId,
            "critical", Math.Clamp(riskScore, 0, 10), ipAddress: ipAddress, metadata: metadata));
        return Task.CompletedTask;
    }

    // ── Severity / risk resolution ─────────────────────────────────────────────

    internal static (string severity, int risk) ResolveSeverityAndRisk(
        string category, string action, string outcome, int suppliedRisk)
    {
        // Reuso de token: siempre crítico, riesgo máximo
        if (action is "auth.token_reuse_detected" or "token_reuse")
            return ("critical", 10);

        // Toda categoría security → critical
        if (category == "security")
            return ("critical", suppliedRisk < 0 ? 10 : Math.Clamp(suppliedRisk, 0, 10));

        // Auth failures
        if (outcome == "failure" || action is "auth.login_failed" or "login_failed")
        {
            int risk = suppliedRisk < 0 ? 5 : suppliedRisk;
            return ("warning", Math.Clamp(risk, 0, 10));
        }

        // Data deletions
        if (category == "data" && action is "data.delete" or "delete")
        {
            int risk = suppliedRisk < 0 ? 3 : suppliedRisk;
            return ("warning", Math.Clamp(risk, 0, 10));
        }

        // Default info
        int defaultRisk = suppliedRisk < 0 ? 1 : suppliedRisk;
        return ("info", Math.Clamp(defaultRisk, 0, 10));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AuditLog Build(
        Guid tenantId, string category, string action, string outcome,
        Guid? actorId, string severity, int riskScore,
        string? ipAddress = null,
        string? entityType = null, string? entityId = null,
        string? metadata = null)
    {
        // We pack extra security fields into NewValues (JSON) to avoid schema changes
        var extra = System.Text.Json.JsonSerializer.Serialize(new
        {
            category,
            outcome,
            severity,
            riskScore,
            ipAddress,
            metadata,
        });

        return new AuditLog
        {
            TenantId   = tenantId,
            EntityType = entityType ?? category,
            EntityId   = entityId is not null && Guid.TryParse(entityId, out var eid) ? eid : Guid.Empty,
            Action     = action,
            ActorId    = actorId,
            NewValues  = extra,
        };
    }

    private async Task PersistAsync(AuditLog entry)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist security audit log for action {Action}", entry.Action);
        }
    }
}
