using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// CalendarServiceTests
//
// Tests del CalendarService: política cache-aside, freshness, fallback stale,
// Unavailable y 304 Not Modified.
//
// Estrategia de tests:
//  · AppDbContext con InMemory (no-transaction — solo lectura en este feature).
//  · ICalendarCacheStore mockeado con NSubstitute.
//  · IICalReader mockeado con NSubstitute.
//  · Se verifica el comportamiento del orquestador sin depender de la BD real.
// ════════════════════════════════════════════════════════════════════════════

public sealed class CalendarServiceTests : IDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly AppDbContext          _db;
    private readonly ICalendarCacheStore   _cacheStore;
    private readonly IICalReader           _reader;
    private readonly ICalOptions           _opts;
    private readonly Guid                  _tenantId;
    private readonly Guid                  _connectionId;

    public CalendarServiceTests()
    {
        var dbOpts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db           = new AppDbContext(dbOpts);
        _cacheStore   = Substitute.For<ICalendarCacheStore>();
        _reader       = Substitute.For<IICalReader>();
        _tenantId     = Guid.NewGuid();
        _connectionId = Guid.NewGuid();

        _opts = new ICalOptions
        {
            FreshnessTtl     = TimeSpan.FromMinutes(15),
            HttpTimeout      = TimeSpan.FromSeconds(10),
            MaxStaleAge      = TimeSpan.FromHours(24),
            MaxEventsPerFeed = 5_000,
        };

        // Registrar CalendarConnection en la BD InMemory
        var tenant = new Tenant
        {
            Id           = _tenantId,
            Name         = "Clínica Test",
            Slug         = "clinica-test",
            TimeZone     = "Europe/Madrid",
            WhatsAppNumber = "+34600000000",
        };
        _db.Set<Tenant>().Add(tenant);

        var connection = new CalendarConnection
        {
            Id           = _connectionId,
            TenantId     = _tenantId,
            Provider     = "ical",
            DisplayName  = "Google Calendar",
            IcalUrl      = "http://test.example/cal.ics",
            SyncStatus   = "ok",
            IsActive     = true,
            IsPrimary    = true,
        };
        _db.Set<CalendarConnection>().Add(connection);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ── Factory ───────────────────────────────────────────────────────────────

    private CalendarService CreateService() => new(
        _db,
        _cacheStore,
        _reader,
        Options.Create(_opts),
        NullLogger<CalendarService>.Instance);

    // ── Slots de prueba ───────────────────────────────────────────────────────

    private static IReadOnlyList<ICalSlot> SomeSlots() =>
    [
        new ICalSlot
        {
            StartsAtUtc = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
            EndsAtUtc   = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero),
            Summary     = "Paciente A",
            Uid         = "uid-1",
            IsOpaque    = true,
        },
    ];

    private static string SlotsJson() =>
        System.Text.Json.JsonSerializer.Serialize(SomeSlots(),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

    // ── Tests de conexión ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_ConnectionNotFound_ReturnsUnavailable()
    {
        // Arrange
        var service = CreateService();

        // Act — ID de conexión que no existe
        var result = await service.GetSlotsAsync(_tenantId, Guid.NewGuid());

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Unavailable);
        result.ErrorMessage.Should().Contain("no encontrada");
    }

    [Fact]
    public async Task GetSlotsAsync_ConnectionWithoutUrl_ReturnsUnavailable()
    {
        // Arrange: añadir conexión sin IcalUrl
        var noUrlConnection = new CalendarConnection
        {
            Id          = Guid.NewGuid(),
            TenantId    = _tenantId,
            Provider    = "ical",
            DisplayName = "Sin URL",
            IcalUrl     = null,  // sin URL
            SyncStatus  = "pending",
            IsActive    = true,
        };
        _db.Set<CalendarConnection>().Add(noUrlConnection);
        await _db.SaveChangesAsync();

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, noUrlConnection.Id);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Unavailable);
        result.ErrorMessage.Should().Contain("URL iCal");
    }

    // ── Tests de caché fresca ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_FreshCache_ReturnsFreshWithoutCallingReader()
    {
        // Arrange: caché con edad de 5 min (< 15 min TTL)
        var cachedEntry = new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            ExpiresAtUtc  = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10),
        };

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns(cachedEntry);

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        result.Slots.Should().HaveCount(1);

        // El reader NO debe haberse llamado
        await _reader.DidNotReceive().ReadAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // ── Tests de caché stale (necesita refrescar) ─────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_StaleCache_ReaderOk_ReturnsFreshAndUpserts()
    {
        // Arrange: caché con edad de 20 min (> 15 min TTL)
        var cachedEntry = new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
            ExpiresAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            ETag          = "\"old-etag\"",
        };

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns(cachedEntry);

        var newSlots = new List<ICalSlot>
        {
            new() { StartsAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    EndsAtUtc   = DateTimeOffset.UtcNow.AddHours(2),
                    Summary     = "Nuevo slot", Uid = "uid-new", IsOpaque = true },
        };

        _reader.ReadAsync(
                "http://test.example/cal.ics",
                Arg.Any<string?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Success(newSlots, etag: "\"new-etag\""));

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        result.Slots.Should().HaveCount(1);
        result.Slots[0].Summary.Should().Be("Nuevo slot");

        // La caché debe haberse actualizado
        await _cacheStore.Received(1).UpsertAsync(
            Arg.Is<CalendarCache>(c => c.TenantId == _tenantId && c.ConnectionId == _connectionId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSlotsAsync_StaleCache_ReaderFails_ReturnsStale()
    {
        // Arrange: caché con edad de 20 min pero < MaxStaleAge (24 h)
        var cachedEntry = new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20),
            ExpiresAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        };

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns(cachedEntry);

        _reader.ReadAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Failure("Connection refused"));

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Stale);
        result.Slots.Should().HaveCount(1);  // datos de la caché stale
        result.ErrorMessage.Should().Be("Connection refused");

        await _cacheStore.Received(1).MarkErrorAsync(
            _tenantId, _connectionId,
            "Connection refused",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSlotsAsync_StaleCache_TooOld_ReturnsUnavailable()
    {
        // Arrange: caché con edad mayor que MaxStaleAge
        var cachedEntry = new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromHours(25), // > 24h MaxStaleAge
            ExpiresAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromHours(24),
        };

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns(cachedEntry);

        _reader.ReadAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Failure("Timeout"));

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Unavailable);
        result.Slots.Should().BeEmpty();
    }

    // ── Tests de 304 Not Modified ─────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_304NotModified_ExtendsTtlAndReturnsFresh()
    {
        // Arrange: caché stale (necesita refrescar) pero 304 al refrescar
        var originalFetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        var cachedEntry = new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = originalFetchedAt,
            ExpiresAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            ETag          = "\"current-etag\"",
        };

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns(cachedEntry);

        _reader.ReadAsync(
                "http://test.example/cal.ics",
                "\"current-etag\"",
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.NotModified());

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        result.Slots.Should().HaveCount(1); // slots de la caché original

        // Se debería haber actualizado el TTL
        await _cacheStore.Received(1).UpsertAsync(
            Arg.Is<CalendarCache>(c => c.ExpiresAtUtc > DateTimeOffset.UtcNow),
            Arg.Any<CancellationToken>());
    }

    // ── Tests sin caché previa ────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_NoCachePreviousAndReaderOk_ReturnsFresh()
    {
        // Arrange: sin caché
        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns((CalendarCache?)null);

        _reader.ReadAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Success(SomeSlots()));

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        result.Slots.Should().HaveCount(1);

        await _cacheStore.Received(1).UpsertAsync(
            Arg.Any<CalendarCache>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSlotsAsync_NoCacheAndReaderFails_ReturnsUnavailable()
    {
        // Arrange: sin caché y reader falla
        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns((CalendarCache?)null);

        _reader.ReadAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Failure("DNS resolution failed"));

        var service = CreateService();

        // Act
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Unavailable);
        result.Slots.Should().BeEmpty();
        result.ErrorMessage.Should().Be("DNS resolution failed");
    }

    // ── Test de InvalidateCacheAsync ──────────────────────────────────────────

    [Fact]
    public async Task InvalidateCacheAsync_DeletesEntry_NextCallForcesRead()
    {
        // Arrange: insertar entrada de caché en la BD InMemory
        _db.CalendarCaches.Add(new CalendarCache
        {
            TenantId      = _tenantId,
            ConnectionId  = _connectionId,
            SlotsJson     = SlotsJson(),
            FetchedAtUtc  = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            ExpiresAtUtc  = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10),
        });
        await _db.SaveChangesAsync();

        _cacheStore.GetAsync(_tenantId, _connectionId, Arg.Any<CancellationToken>())
                   .Returns((CalendarCache?)null); // simula que se borró

        _reader.ReadAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
               .Returns(ICalReadResult.Success(SomeSlots()));

        var service = CreateService();

        // Act
        await service.InvalidateCacheAsync(_tenantId, _connectionId);
        var result = await service.GetSlotsAsync(_tenantId, _connectionId);

        // Assert
        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        // El reader fue llamado porque no había caché
        await _reader.Received(1).ReadAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // ── Test de CalendarResult factories ─────────────────────────────────────

    [Fact]
    public void CalendarResult_Fresh_HasCorrectStatus()
    {
        var slots  = SomeSlots();
        var now    = DateTimeOffset.UtcNow;
        var result = CalendarResult.Fresh(slots, now);

        result.Status.Should().Be(CalendarCacheStatus.Fresh);
        result.Slots.Should().HaveCount(1);
        result.FetchedAtUtc.Should().Be(now);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void CalendarResult_Stale_HasCorrectStatus()
    {
        var slots  = SomeSlots();
        var now    = DateTimeOffset.UtcNow;
        var result = CalendarResult.Stale(slots, now, "Timeout");

        result.Status.Should().Be(CalendarCacheStatus.Stale);
        result.ErrorMessage.Should().Be("Timeout");
    }

    [Fact]
    public void CalendarResult_Unavailable_EmptySlotsAndError()
    {
        var result = CalendarResult.Unavailable("Feed unreachable");

        result.Status.Should().Be(CalendarCacheStatus.Unavailable);
        result.Slots.Should().BeEmpty();
        result.ErrorMessage.Should().Be("Feed unreachable");
    }
}
