using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinicBoost.Tests.Infrastructure.Idempotency;

// ════════════════════════════════════════════════════════════════════════════
// TestableIdempotencyService
//
// Test double de IdempotencyService que reemplaza AtomicInsertAsync
// (que usa SQL crudo de Postgres) con una implementación basada en
// EF Core InMemory (SELECT + conditional INSERT, no atómico pero
// suficiente para tests sin Postgres real).
//
// USO:
//   · Tests unitarios de IdempotencyService (IdempotencyServiceTests.cs)
//   · Tests de integración de endpoints que usan InMemory DB
//     (MissedCallEndpointTests.cs y similares)
//
// NOTA sobre semántica nullable:
//   EF InMemory tiene problemas con Guid? == Guid? en LINQ to Entities.
//   Se hace .ToList() + LINQ to Objects para garantizar semántica correcta.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Test double de <see cref="IdempotencyService"/> compatible con EF InMemory.
/// Reemplaza la inserción SQL atómica (Postgres) por lógica EF pura.
/// Registrar como <b>Scoped</b> igual que <see cref="IdempotencyService"/>.
/// </summary>
public sealed class TestableIdempotencyService : IdempotencyService
{
    private readonly AppDbContext _dbCtx;

    public TestableIdempotencyService(
        AppDbContext                db,
        ITenantContext              tenant,
        ILogger<IdempotencyService> logger)
        : base(db, tenant, logger)
    {
        _dbCtx = db;
    }

    /// <summary>
    /// Sobrescribe el INSERT SQL con una versión EF pura (SELECT + conditional INSERT).
    /// No es atómico, pero es suficiente para tests sin Postgres.
    ///
    /// NOTA: EF InMemory tiene limitaciones con Guid? nullable en comparaciones LINQ.
    /// Usamos .ToList() + LINQ to Objects para garantizar semántica correcta de null.
    /// </summary>
    protected override async Task<(bool inserted, ProcessedEvent? existing)>
        AtomicInsertAsync(
            ProcessedEvent    evt,
            CancellationToken ct)
    {
        // Cargar candidatos por (event_type, event_id) — sin filtrar por tenant_id
        // para evitar problemas de semántica nullable en EF InMemory.
        var candidates = await _dbCtx.ProcessedEvents
            .AsNoTracking()
            .Where(e => e.EventType == evt.EventType && e.EventId == evt.EventId)
            .ToListAsync(ct);

        // Filtrar por tenant_id con semántica null correcta en LINQ to Objects
        var existing = candidates.FirstOrDefault(e =>
            e.TenantId.Equals(evt.TenantId) ||
            (!e.TenantId.HasValue && !evt.TenantId.HasValue));

        if (existing is not null)
            return (false, existing);

        _dbCtx.ProcessedEvents.Add(evt);
        await _dbCtx.SaveChangesAsync(ct);
        return (true, null);
    }
}
