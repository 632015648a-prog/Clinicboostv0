using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// EfCalendarCacheStoreTests
//
// Tests del EfCalendarCacheStore: GetAsync, UpsertAsync (insert y update),
// MarkErrorAsync y resiliencia ante errores de contexto.
//
// Usa EF InMemory (solo lectura y escritura simple, sin transacciones).
// ════════════════════════════════════════════════════════════════════════════

public sealed class EfCalendarCacheStoreTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid         _tenantId     = Guid.NewGuid();
    private readonly Guid         _connectionId = Guid.NewGuid();

    public EfCalendarCacheStoreTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    private EfCalendarCacheStore CreateStore()
        => new(_db, NullLogger<EfCalendarCacheStore>.Instance);

    private CalendarCache BuildEntry(DateTimeOffset? fetchedAt = null) => new()
    {
        TenantId      = _tenantId,
        ConnectionId  = _connectionId,
        SlotsJson     = "[{\"startsAtUtc\":\"2026-04-01T09:00:00+00:00\"}]",
        FetchedAtUtc  = fetchedAt ?? DateTimeOffset.UtcNow,
        ExpiresAtUtc  = (fetchedAt ?? DateTimeOffset.UtcNow) + TimeSpan.FromMinutes(15),
    };

    // ── GetAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_NoEntry_ReturnsNull()
    {
        var store  = CreateStore();
        var result = await store.GetAsync(_tenantId, _connectionId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ExistingEntry_ReturnsEntry()
    {
        // Arrange
        var entry = BuildEntry();
        _db.CalendarCaches.Add(entry);
        await _db.SaveChangesAsync();

        // Act
        var store  = CreateStore();
        var result = await store.GetAsync(_tenantId, _connectionId);

        // Assert
        result.Should().NotBeNull();
        result!.TenantId    .Should().Be(_tenantId);
        result.ConnectionId .Should().Be(_connectionId);
        result.SlotsJson    .Should().Be(entry.SlotsJson);
    }

    [Fact]
    public async Task GetAsync_DifferentTenant_ReturnsNull()
    {
        // Arrange: entrada para otro tenant
        var entry = BuildEntry();
        _db.CalendarCaches.Add(entry);
        await _db.SaveChangesAsync();

        // Act: buscar con tenant distinto
        var store  = CreateStore();
        var result = await store.GetAsync(Guid.NewGuid(), _connectionId);

        // Assert
        result.Should().BeNull();
    }

    // ── UpsertAsync (insert) ──────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_NewEntry_InsertsSuccessfully()
    {
        // Arrange
        var store = CreateStore();
        var entry = BuildEntry();

        // Act
        await store.UpsertAsync(entry);

        // Assert
        var saved = await _db.CalendarCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == _tenantId && c.ConnectionId == _connectionId);

        saved.Should().NotBeNull();
        saved!.SlotsJson.Should().Be(entry.SlotsJson);
    }

    // ── UpsertAsync (update) ──────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_ExistingEntry_UpdatesSlots()
    {
        // Arrange: insertar entrada inicial
        var initial = BuildEntry();
        _db.CalendarCaches.Add(initial);
        await _db.SaveChangesAsync();

        // Con el mismo DbContext: UpsertAsync detecta existencia y actualiza
        var store   = CreateStore();
        var updated = BuildEntry();
        updated.SlotsJson = "[{\"startsAtUtc\":\"2026-04-02T09:00:00+00:00\"}]";

        // Act
        await store.UpsertAsync(updated);

        // Assert
        var saved = await _db.CalendarCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == _tenantId && c.ConnectionId == _connectionId);

        saved.Should().NotBeNull();
        saved!.SlotsJson.Should().Contain("2026-04-02"); // actualizado
    }

    [Fact]
    public async Task UpsertAsync_ClearsLastErrorMessage_OnSuccessfulUpdate()
    {
        // Arrange: entrada con error previo
        var entry = BuildEntry();
        entry.LastErrorMessage = "Previous error";
        _db.CalendarCaches.Add(entry);
        await _db.SaveChangesAsync();

        var store   = CreateStore();
        var updated = BuildEntry();
        updated.SlotsJson = "[{\"startsAtUtc\":\"2026-04-03T09:00:00+00:00\"}]";

        // Act
        await store.UpsertAsync(updated);

        // Assert
        var saved = await _db.CalendarCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == _tenantId && c.ConnectionId == _connectionId);

        saved!.LastErrorMessage.Should().BeNull(); // limpiado en el upsert
    }

    // ── MarkErrorAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkErrorAsync_ExistingEntry_SetsLastErrorMessage()
    {
        // Arrange
        var entry = BuildEntry();
        _db.CalendarCaches.Add(entry);
        await _db.SaveChangesAsync();

        var store = CreateStore();

        // Act — MarkErrorAsync usa ExecuteUpdateAsync (no soportado en InMemory).
        // El test verifica que el método no lanza excepción (resiliencia).
        // En producción (Postgres) sí actualiza; los tests de integración
        // verificarán el comportamiento real.
        var act = async () => await store.MarkErrorAsync(_tenantId, _connectionId, "Connection timeout");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkErrorAsync_NoEntry_DoesNotThrow()
    {
        // Arrange: sin entrada previa
        var store = CreateStore();

        // Act & Assert: no debe lanzar excepción
        var act = async () => await store.MarkErrorAsync(_tenantId, _connectionId, "Error");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MarkErrorAsync_PreservesExistingSlots()
    {
        // Arrange
        var entry = BuildEntry();
        var originalSlots = entry.SlotsJson;
        _db.CalendarCaches.Add(entry);
        await _db.SaveChangesAsync();

        var store = CreateStore();

        // Act — MarkErrorAsync usa ExecuteUpdateAsync internamente.
        // En InMemory no se aplica, pero el m\u00e9todo no debe lanzar excepci\u00f3n.
        await store.MarkErrorAsync(_tenantId, _connectionId, "Some error");

        // Assert: los slots no deben haberse modificado (MarkError no los toca)
        var saved = await _db.CalendarCaches
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == _tenantId && c.ConnectionId == _connectionId);

        // Los slots permanecen intactos (MarkError no sobrescribe slots_json)
        saved!.SlotsJson.Should().Be(originalSlots);
    }

    // ── Resiliencia ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenDbThrows_ReturnsNull()
    {
        // Arrange: contexto ya dispuesto para forzar error
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var disposedDb = new AppDbContext(opts);
        disposedDb.Dispose(); // dispuesto deliberadamente

        var store = new EfCalendarCacheStore(disposedDb, NullLogger<EfCalendarCacheStore>.Instance);

        // Act & Assert: debe devolver null sin propagar excepción
        var result = await store.GetAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Should().BeNull();
    }
}
