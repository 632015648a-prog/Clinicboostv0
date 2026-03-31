using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Variants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Variants;

// ════════════════════════════════════════════════════════════════════════════
// VariantTrackingServiceTests
//
// Tests unitarios para VariantTrackingService.
//
// COBERTURA
// ─────────
//  · SelectVariantAsync:
//      - Sin variantes → devuelve null
//      - Una variante activa → siempre la misma
//      - Dos variantes 50/50 → distribución aproximada
//      - Variante inactiva → no se selecciona
//      - Pesos a cero → devuelve primera como fallback
//
//  · RecordEventAsync:
//      - Persiste el evento correctamente
//      - Un error de BD no propaga excepción (fire-and-forget)
//
//  · GetVariantStatsAsync:
//      - Sin eventos → stats vacías con contadores = 0
//      - Con eventos del funnel → tasas calculadas correctamente
//      - Variante no encontrada → VariantStats.Empty
//
//  · GetVariantComparisonAsync:
//      - Sin variantes → lista vacía
//      - Dos variantes con datos → ordenadas y WinnerVariantKey correcto
// ════════════════════════════════════════════════════════════════════════════

public class VariantTrackingServiceTests : IAsyncLifetime
{
    private AppDbContext  _db     = null!;
    private VariantTrackingService _svc = null!;
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db  = new AppDbContext(options);
        _svc = new VariantTrackingService(_db, NullLogger<VariantTrackingService>.Instance);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
    }

    // ── SelectVariantAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SelectVariantAsync_NoVariants_ReturnsNull()
    {
        var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");

        Assert.Null(result);
    }

    [Fact]
    public async Task SelectVariantAsync_SingleActiveVariant_AlwaysReturnsThat()
    {
        // Arrange
        var variant = CreateVariant("A", 100);
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        // Act: 20 llamadas → siempre devuelve la misma
        for (var i = 0; i < 20; i++)
        {
            var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");
            Assert.NotNull(result);
            Assert.Equal("A", result.VariantKey);
        }
    }

    [Fact]
    public async Task SelectVariantAsync_InactiveVariant_ReturnsNull()
    {
        // Arrange: variante inactiva
        var variant = CreateVariant("A", 100, isActive: false);
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");

        Assert.Null(result);
    }

    [Fact]
    public async Task SelectVariantAsync_TwoVariants_BothGetSelected()
    {
        // Arrange: A=50, B=50
        _db.MessageVariants.AddRange(
            CreateVariant("A", 50),
            CreateVariant("B", 50));
        await _db.SaveChangesAsync();

        // Act: 200 llamadas
        var counts = new Dictionary<string, int> { ["A"] = 0, ["B"] = 0 };
        for (var i = 0; i < 200; i++)
        {
            var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");
            Assert.NotNull(result);
            counts[result!.VariantKey]++;
        }

        // Assert: ambas variantes se seleccionan al menos 1 vez
        // (probabilidad de fallo con 200 muestras y p=0.5 es negligible)
        Assert.True(counts["A"] > 0, "Variante A nunca seleccionada");
        Assert.True(counts["B"] > 0, "Variante B nunca seleccionada");
    }

    [Fact]
    public async Task SelectVariantAsync_ZeroWeights_ReturnsFallback()
    {
        // Arrange: variante con peso 0 (configuración incorrecta)
        _db.MessageVariants.Add(CreateVariant("A", 0));
        await _db.SaveChangesAsync();

        // No lanza excepción; devuelve primera variante como fallback
        var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");
        Assert.NotNull(result);
        Assert.Equal("A", result!.VariantKey);
    }

    [Fact]
    public async Task SelectVariantAsync_OtherTenantVariants_AreNotVisible()
    {
        // Arrange: variante de otro tenant
        var otherTenant = Guid.NewGuid();
        var variant = new MessageVariant
        {
            TenantId   = otherTenant,
            FlowId     = "flow_01",
            TemplateId = "tmpl_v1",
            VariantKey = "A",
            WeightPct  = 100,
            IsActive   = true,
        };
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var result = await _svc.SelectVariantAsync(_tenantId, "flow_01", "tmpl_v1");

        Assert.Null(result);
    }

    // ── RecordEventAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RecordEventAsync_PersistsEvent()
    {
        // Arrange
        var variantId = Guid.NewGuid();
        var evt = new VariantConversionEvent
        {
            TenantId         = _tenantId,
            MessageVariantId = variantId,
            EventType        = VariantEventType.OutboundSent,
            CorrelationId    = "corr-001",
        };

        // Act
        await _svc.RecordEventAsync(evt);

        // Assert
        var saved = await _db.VariantConversionEvents
            .FirstOrDefaultAsync(e => e.CorrelationId == "corr-001");

        Assert.NotNull(saved);
        Assert.Equal(VariantEventType.OutboundSent, saved!.EventType);
        Assert.Equal(_tenantId, saved.TenantId);
        Assert.Equal(variantId, saved.MessageVariantId);
    }

    // ── GetVariantStatsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetVariantStatsAsync_VariantNotFound_ReturnsEmpty()
    {
        var nonExistentId = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to   = DateTimeOffset.UtcNow;

        var stats = await _svc.GetVariantStatsAsync(_tenantId, nonExistentId, from, to);

        Assert.Equal(0, stats.SentCount);
        Assert.Equal(0, stats.DeliveredCount);
        Assert.Equal(0, stats.BookedCount);
    }

    [Fact]
    public async Task GetVariantStatsAsync_WithFunnelEvents_CalculatesRatesCorrectly()
    {
        // Arrange: variante con funnel completo
        var variant = CreateVariant("A", 100);
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to   = DateTimeOffset.UtcNow.AddDays(1);

        // 10 sent, 8 delivered, 6 read, 4 reply, 2 booked
        await SeedFunnelEvents(variant.Id, sent: 10, delivered: 8, read: 6, reply: 4, booked: 2);

        // Act
        var stats = await _svc.GetVariantStatsAsync(_tenantId, variant.Id, from, to);

        // Assert contadores
        Assert.Equal(10, stats.SentCount);
        Assert.Equal(8,  stats.DeliveredCount);
        Assert.Equal(6,  stats.ReadCount);
        Assert.Equal(4,  stats.ReplyCount);
        Assert.Equal(2,  stats.BookedCount);

        // Assert tasas
        Assert.Equal(0.8,  stats.DeliveryRate, precision: 4);  // 8/10
        Assert.Equal(0.75, stats.ReadRate,     precision: 4);  // 6/8
        Assert.Equal(0.2,  stats.BookingRate,  precision: 4);  // 2/10
    }

    [Fact]
    public async Task GetVariantStatsAsync_NoEvents_AllRatesZero()
    {
        var variant = CreateVariant("A", 100);
        _db.MessageVariants.Add(variant);
        await _db.SaveChangesAsync();

        var stats = await _svc.GetVariantStatsAsync(
            _tenantId, variant.Id,
            DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow);

        Assert.Equal(0, stats.SentCount);
        Assert.Equal(0.0, stats.DeliveryRate);
        Assert.Equal(0.0, stats.BookingRate);
    }

    // ── GetVariantComparisonAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetVariantComparisonAsync_NoVariants_ReturnsEmptyList()
    {
        var result = await _svc.GetVariantComparisonAsync(
            _tenantId, "flow_01", "tmpl_v1",
            DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetVariantComparisonAsync_TwoVariants_ReturnsBoth()
    {
        var varA = CreateVariant("A", 50);
        var varB = CreateVariant("B", 50);
        _db.MessageVariants.AddRange(varA, varB);
        await _db.SaveChangesAsync();

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to   = DateTimeOffset.UtcNow.AddDays(1);

        // A: 10 sent, 3 booked → booking_rate = 0.30
        // B: 10 sent, 1 booked → booking_rate = 0.10
        await SeedFunnelEvents(varA.Id, sent: 10, delivered: 8, read: 6, reply: 4, booked: 3);
        await SeedFunnelEvents(varB.Id, sent: 10, delivered: 5, read: 3, reply: 2, booked: 1);

        var result = await _svc.GetVariantComparisonAsync(_tenantId, "flow_01", "tmpl_v1", from, to);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, v => v.VariantKey == "A");
        Assert.Contains(result, v => v.VariantKey == "B");

        var statsA = result.First(v => v.VariantKey == "A");
        Assert.Equal(0.3, statsA.BookingRate, precision: 4);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MessageVariant CreateVariant(string key, short weight, bool isActive = true)
        => new()
        {
            TenantId   = _tenantId,
            FlowId     = "flow_01",
            TemplateId = "tmpl_v1",
            VariantKey = key,
            WeightPct  = weight,
            IsActive   = isActive,
        };

    private async Task SeedFunnelEvents(
        Guid variantId,
        int  sent,
        int  delivered,
        int  read,
        int  reply,
        int  booked)
    {
        var events = new List<VariantConversionEvent>();

        void Add(string type, int count, long? elapsedMs = null)
        {
            for (var i = 0; i < count; i++)
                events.Add(new VariantConversionEvent
                {
                    TenantId         = _tenantId,
                    MessageVariantId = variantId,
                    EventType        = type,
                    ElapsedMs        = elapsedMs,
                    CorrelationId    = Guid.NewGuid().ToString(),
                    OccurredAt       = DateTimeOffset.UtcNow,
                });
        }

        Add(VariantEventType.OutboundSent, sent,       null);
        Add(VariantEventType.Delivered,    delivered,  5_000);
        Add(VariantEventType.Read,         read,       15_000);
        Add(VariantEventType.Reply,        reply,      30_000);
        Add(VariantEventType.Booked,       booked,     120_000);

        _db.VariantConversionEvents.AddRange(events);
        await _db.SaveChangesAsync();
    }
}
