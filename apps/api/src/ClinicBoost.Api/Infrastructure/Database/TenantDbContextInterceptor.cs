using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ClinicBoost.Api.Infrastructure.Middleware;

namespace ClinicBoost.Api.Infrastructure.Database;

// ════════════════════════════════════════════════════════════════
// TenantDbContextInterceptor
//
// PROPÓSITO
// ─────────
// Cuando ClinicBoost.Api usa la conexión directa con app_user
// (sin JWT de GoTrue), Postgres no tiene el contexto de tenant.
//
// Este interceptor resuelve eso interceptando la apertura de cada
// conexión EF Core y ejecutando:
//
//   SELECT claim_tenant_context('<tenant_id>', '<role>', '<user_id>')
//
// Eso establece los GUCs app.tenant_id / app.user_role / app.user_id
// con SET LOCAL (scope = transacción), de modo que las políticas RLS
// de Postgres puedan leerlos vía current_tenant_id() y current_user_role().
//
// GARANTÍAS DE SEGURIDAD
// ──────────────────────
// · SET LOCAL dentro de claim_tenant_context(): los GUCs se limpian al
//   COMMIT/ROLLBACK, evitando contaminación entre conexiones del pool
//   (PgBouncer / Supabase Transaction Pooler).
// · Si ITenantContext no está inicializado, la función de Postgres
//   lanzará SEC-001 (assert_tenant_context) en el siguiente DML.
// · El interceptor NO hace bypass de RLS; solo inyecta el contexto
//   que las políticas RLS usan para filtrar.
//
// IMPLEMENTACIÓN: IDbConnectionInterceptor
// ──────────────────────────────────────────
// ConnectionOpened / ConnectionOpenedAsync se invocan justo después
// de que la conexión ADO.NET subyacente queda lista para recibir
// comandos. Es el punto correcto para inyectar el GUC de sesión.
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Interceptor de EF Core que inyecta el contexto de tenant (SET LOCAL)
/// al abrir la conexión, mediante la función de Postgres
/// <c>claim_tenant_context()</c>.
/// </summary>
public sealed class TenantDbContextInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext                       _tenantCtx;
    private readonly ILogger<TenantDbContextInterceptor> _logger;

    public TenantDbContextInterceptor(
        ITenantContext tenantCtx,
        ILogger<TenantDbContextInterceptor> logger)
    {
        _tenantCtx = tenantCtx;
        _logger    = logger;
    }

    // ── Síncrono ──────────────────────────────────────────────────

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        SetTenantContextSync(connection);
    }

    // ── Asíncrono ─────────────────────────────────────────────────

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetTenantContextAsync(connection, cancellationToken);
    }

    // ── Implementación ────────────────────────────────────────────

    private void SetTenantContextSync(DbConnection connection)
    {
        if (!_tenantCtx.IsInitialized)
        {
            _logger.LogDebug(
                "TenantDbContextInterceptor: contexto no inicializado, " +
                "omitiendo claim_tenant_context (posible endpoint público).");
            return;
        }

        using var cmd = connection.CreateCommand();
        BuildCommand(cmd);
        cmd.ExecuteNonQuery();

        _logger.LogDebug(
            "claim_tenant_context ejecutado. TenantId={TenantId} Role={Role}",
            _tenantCtx.TenantId, _tenantCtx.UserRole);
    }

    private async Task SetTenantContextAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        if (!_tenantCtx.IsInitialized)
        {
            _logger.LogDebug(
                "TenantDbContextInterceptor: contexto no inicializado, " +
                "omitiendo claim_tenant_context (posible endpoint público).");
            return;
        }

        using var cmd = connection.CreateCommand();
        BuildCommand(cmd);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogDebug(
            "claim_tenant_context ejecutado (async). TenantId={TenantId} Role={Role}",
            _tenantCtx.TenantId, _tenantCtx.UserRole);
    }

    /// <summary>
    /// Construye el comando SQL para llamar a claim_tenant_context().
    /// Usa parámetros SQL para evitar inyección de valores.
    /// </summary>
    private void BuildCommand(DbCommand cmd)
    {
        // claim_tenant_context es SECURITY DEFINER + SET search_path = public
        // Los GUCs se establecen con SET LOCAL (scope = transacción actual)
        cmd.CommandText =
            "SELECT claim_tenant_context(@tenant_id::uuid, @user_role::text, @user_id::uuid)";

        var pTenant = cmd.CreateParameter();
        pTenant.ParameterName = "tenant_id";
        pTenant.Value         = _tenantCtx.TenantId.HasValue
                                    ? (object)_tenantCtx.TenantId.Value
                                    : DBNull.Value;
        cmd.Parameters.Add(pTenant);

        // Si no hay rol, usar 'service' como valor por defecto seguro
        var pRole = cmd.CreateParameter();
        pRole.ParameterName = "user_role";
        pRole.Value         = string.IsNullOrEmpty(_tenantCtx.UserRole)
                                  ? (object)"service"
                                  : _tenantCtx.UserRole;
        cmd.Parameters.Add(pRole);

        var pUser = cmd.CreateParameter();
        pUser.ParameterName = "user_id";
        pUser.Value         = _tenantCtx.UserId.HasValue
                                  ? (object)_tenantCtx.UserId.Value
                                  : DBNull.Value;
        cmd.Parameters.Add(pUser);
    }
}
