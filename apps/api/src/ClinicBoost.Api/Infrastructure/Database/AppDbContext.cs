using Microsoft.EntityFrameworkCore;
using ClinicBoost.Domain.Tenants;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Common;

namespace ClinicBoost.Api.Infrastructure.Database;

/// <summary>
/// DbContext principal. NO usa repositorios genéricos.
/// Cada feature accede directamente al contexto o a sus propios métodos de consulta.
/// RLS está activa en Postgres; el app_user NO puede desactivarla.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ─── DbSets ───────────────────────────────────────────────────────────────
    public DbSet<Tenant>          Tenants          => Set<Tenant>();
    public DbSet<Appointment>     Appointments     => Set<Appointment>();
    public DbSet<Patient>         Patients         => Set<Patient>();
    public DbSet<ProcessedEvent>  ProcessedEvents  => Set<ProcessedEvent>();
    public DbSet<AuditLog>        AuditLogs        => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Aplicar configuraciones de todas las entidades automáticamente
        model.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Convención: todas las entidades con tenant_id aplican query filter global
        // ⚠ El tenant_id real se inyecta vía ITenantContext en el middleware
        base.OnModelCreating(model);
    }
}
