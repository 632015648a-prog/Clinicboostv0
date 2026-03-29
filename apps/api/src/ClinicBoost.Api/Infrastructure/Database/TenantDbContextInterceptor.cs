using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ClinicBoost.Api.Infrastructure.Tenants;

namespace ClinicBoost.Api.Infrastructure.Database;

// ════════════════════════════════════════════════════════════════
// TenantDbContextInterceptor
//
// PROPÓSITO
// ─────────
// Propaga el contexto de tenant (tenant_id, user_role, user_id)
// a Postgres mediante la función claim_tenant_context() al abrir
// cada conexión EF Core.
//
// Esto garantiza que las políticas RLS de Postgres puedan filtrar
// por tenant incluso cuando la petición no lleva JWT (p.ej. workers
// internos que usan directamente app_user).
//
// SEGURIDAD
// ─────────
// · claim_tenant_context() usa SET LOCAL → GUCs viven solo dentro
//   de la transacción actual; se limpian al commit/rollback.
//   Crítico para connection pools (PgBouncer / Supabase pooler).
// · Parámetros SQL tipados → no hay posibilidad de inyección.
// · Si ITenantContext no está inicializado se omite la llamada y
//   Postgres usará current_tenant_id() = NULL, por lo que RLS
//   devolverá 0 filas en cualquier tabla de negocio.
// · Los errores de la llamada a Postgres se capturan, se loguean
//   con contexto estructurado y se relanza para no swallow-ear.
//
// IMPLEMENTACIÓN: DbConnectionInterceptor
// ────────────────────────────────────────
// ConnectionOpened/Async: se invocan tras que la conexión ADO.NET
// queda lista para recibir comandos, antes de cualquier query.
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Interceptor EF Core que llama a <c>claim_tenant_context()</c>
/// al abrir cada conexión, inyectando los GUCs de tenant con SET LOCAL.
/// </summary>
public sealed class TenantDbContextInterceptor : DbConnectionInterceptor
{
    private const string ClaimSql =
        "SELECT claim_tenant_context(@tenant_id::uuid, @user_role::text, @user_id::uuid)";

    private readonly ITenantContext                       _tenant;
    private readonly ILogger<TenantDbContextInterceptor> _logger;

    public TenantDbContextInterceptor(
        ITenantContext                       tenant,
        ILogger<TenantDbContextInterceptor> logger)
    {
        _tenant = tenant;
        _logger = logger;
    }

    // ── Punto de entrada síncrono ─────────────────────────────────────────────

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        if (!_tenant.IsInitialized)
        {
            LogSkipped();
            return;
        }

        try
        {
            using var cmd = BuildCommand(connection);
            cmd.ExecuteNonQuery();
            LogClaimed();
        }
        catch (Exception ex)
        {
            LogAndRethrow(ex);
        }
    }

    // ── Punto de entrada asíncrono ────────────────────────────────────────────

    public override async Task ConnectionOpenedAsync(
        DbConnection       connection,
        ConnectionEndEventData eventData,
        CancellationToken  ct = default)
    {
        if (!_tenant.IsInitialized)
        {
            LogSkipped();
            return;
        }

        try
        {
            using var cmd = BuildCommand(connection);
            await cmd.ExecuteNonQueryAsync(ct);
            LogClaimed();
        }
        catch (Exception ex)
        {
            LogAndRethrow(ex);
        }
    }

    // ── Construcción del comando ──────────────────────────────────────────────

    private DbCommand BuildCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = ClaimSql;

        AddParam(cmd, "tenant_id",
            _tenant.TenantId.HasValue ? (object)_tenant.TenantId.Value : DBNull.Value);

        // Rol por defecto 'service' si el contexto está inicializado pero sin rol
        // (caso: worker backend sin usuario humano)
        AddParam(cmd, "user_role",
            string.IsNullOrEmpty(_tenant.UserRole)
                ? (object)"service"
                : _tenant.UserRole);

        AddParam(cmd, "user_id",
            _tenant.UserId.HasValue ? (object)_tenant.UserId.Value : DBNull.Value);

        return cmd;
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value         = value;
        cmd.Parameters.Add(p);
    }

    // ── Helpers de log ────────────────────────────────────────────────────────

    private void LogSkipped() =>
        _logger.LogDebug(
            "[TenantInterceptor] Contexto no inicializado — " +
            "claim_tenant_context omitido (endpoint público o anonimo).");

    private void LogClaimed() =>
        _logger.LogDebug(
            "[TenantInterceptor] claim_tenant_context ejecutado. " +
            "TenantId={TenantId} Role={Role} UserId={UserId}",
            _tenant.TenantId, _tenant.UserRole, _tenant.UserId);

    private void LogAndRethrow(Exception ex)
    {
        _logger.LogError(ex,
            "[TenantInterceptor] Error al llamar a claim_tenant_context(). " +
            "TenantId={TenantId} Role={Role}. " +
            "La conexión se cerrará y EF Core reintentará según su política de retry.",
            _tenant.TenantId, _tenant.UserRole);

        // Re-lanzar para que EF Core gestione el retry (EnableRetryOnFailure)
        throw new InvalidOperationException(
            $"Error al ejecutar claim_tenant_context() en Postgres. " +
            $"No se pudo inicializar el contexto de tenant para TenantId={_tenant.TenantId}. " +
            $"Ver inner exception.", ex);
    }
}
