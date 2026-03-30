using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// FlowMetricsServiceTests
//
// Prueba la implementación real de FlowMetricsService contra AppDbContext InMemory.
//
// KPIs verificados:
//   · Conteo de missed_call_received, outbound_sent/failed, appointment_booked.
//   · ConversionRate = bookings / outbound_sent.
//   · AvgResponseTimeMs y P95ResponseTimeMs sobre outbound_sent con DurationMs.
//   · TotalRecoveredRevenue sumado desde appointment_booked con RecoveredRevenue.
//   · Filtro por tenant (aislamiento multi-tenant).
//   · Filtro por rango de fechas.
//   · RecordAsync con error no propaga excepción (fire-and-forget).
// ════════════════════════════════════════════════════════════════════════════

public sealed class FlowMetricsServiceTests : IDisposable
{
    private readonly AppDbContext       _db;
    private readonly FlowMetricsService _svc;
    private readonly Guid               _tenantId = Guid.NewGuid();

    public FlowMetricsServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db  = new AppDbContext(opts);
        _svc = new FlowMetricsService(_db, NullLogger<FlowMetricsService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FlowMetricsEvent MakeEvent(
        string            metricType,
        long?             durationMs       = null,
        decimal?          recoveredRevenue = null,
        string?           twilioSid        = null,
        string?           errorCode        = null,
        DateTimeOffset?   occurredAt       = null,
        Guid?             tenantId         = null)
        => new()
        {
            TenantId         = tenantId ?? _tenantId,
            FlowId           = "flow_01",
            MetricType       = metricType,
            DurationMs       = durationMs,
            RecoveredRevenue = recoveredRevenue,
            TwilioMessageSid = twilioSid,
            ErrorCode        = errorCode,
            CorrelationId    = $"corr-{Guid.NewGuid():N}",
            OccurredAt       = occurredAt ?? DateTimeOffset.UtcNow,
        };

    private async Task SeedAsync(params FlowMetricsEvent[] events)
    {
        _db.FlowMetricsEvents.AddRange(events);
        await _db.SaveChangesAsync();
    }

    private DateTimeOffset From => DateTimeOffset.UtcNow.AddDays(-1);
    private DateTimeOffset To   => DateTimeOffset.UtcNow.AddDays(+1);

    // ── Test 1: RecordAsync persiste el evento en BD ───────────────────────────

    [Fact]
    public async Task RecordAsync_ValidEvent_PersistsToDatabase()
    {
        // Arrange
        var evt = MakeEvent("missed_call_received");

        // Act
        await _svc.RecordAsync(evt);

        // Assert
        var stored = await _db.FlowMetricsEvents.FirstOrDefaultAsync();
        stored.Should().NotBeNull();
        stored!.MetricType    .Should().Be("missed_call_received");
        stored.FlowId         .Should().Be("flow_01");
        stored.TenantId       .Should().Be(_tenantId);
        stored.CorrelationId  .Should().Be(evt.CorrelationId);
    }

    // ── Test 2: GetFlow01SummaryAsync conteos básicos ─────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_BasicCounts_ReturnsCorrectValues()
    {
        // Arrange
        await SeedAsync(
            MakeEvent("missed_call_received"),
            MakeEvent("missed_call_received"),
            MakeEvent("outbound_sent",  durationMs: 5000),
            MakeEvent("outbound_sent",  durationMs: 7000),
            MakeEvent("outbound_failed", errorCode: "30006"),
            MakeEvent("patient_replied"),
            MakeEvent("appointment_booked", recoveredRevenue: 50m),
            MakeEvent("flow_skipped"));

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert
        summary.MissedCallsReceived .Should().Be(2);
        summary.OutboundSent        .Should().Be(2);
        summary.OutboundFailed      .Should().Be(1);
        summary.PatientReplies      .Should().Be(1);
        summary.AppointmentsBooked  .Should().Be(1);
    }

    // ── Test 3: ConversionRate ────────────────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_ConversionRate_IsBookingsOverOutboundSent()
    {
        // Arrange: 4 enviados, 1 reservado → 0.25
        await SeedAsync(
            MakeEvent("outbound_sent",  durationMs: 3000),
            MakeEvent("outbound_sent",  durationMs: 4000),
            MakeEvent("outbound_sent",  durationMs: 5000),
            MakeEvent("outbound_sent",  durationMs: 6000),
            MakeEvent("appointment_booked", recoveredRevenue: 60m));

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert
        summary.ConversionRate.Should().BeApproximately(0.25, precision: 0.001);
    }

    // ── Test 4: ConversionRate es 0 si no hay outbound_sent ───────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_NoOutboundSent_ConversionRateIsZero()
    {
        // Arrange
        await SeedAsync(MakeEvent("missed_call_received"));

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert
        summary.ConversionRate   .Should().Be(0.0);
        summary.AvgResponseTimeMs.Should().Be(0.0);
        summary.P95ResponseTimeMs.Should().Be(0.0);
    }

    // ── Test 5: AvgResponseTimeMs ─────────────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_AvgResponseTime_IsAverageOfOutboundSentDurations()
    {
        // Arrange: 3 eventos con durations 2000, 4000, 6000 → avg = 4000
        await SeedAsync(
            MakeEvent("outbound_sent", durationMs: 2000),
            MakeEvent("outbound_sent", durationMs: 4000),
            MakeEvent("outbound_sent", durationMs: 6000),
            MakeEvent("outbound_sent", durationMs: null)); // sin duración: excluido del avg

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert
        summary.AvgResponseTimeMs.Should().BeApproximately(4000.0, precision: 1.0);
    }

    // ── Test 6: P95ResponseTimeMs ─────────────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_P95ResponseTime_IsCorrectPercentile()
    {
        // Arrange: 20 valores de 1000 a 20000 ms
        var events = Enumerable.Range(1, 20)
            .Select(i => MakeEvent("outbound_sent", durationMs: i * 1000L))
            .ToArray();
        await SeedAsync(events);

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert: p95 de 20 valores ordenados [1000..20000]
        // índice = Min(Floor(20 * 0.95), 19) = Min(19, 19) = 19 → valor en índice 19 = 20000
        summary.P95ResponseTimeMs.Should().Be(20000);
    }

    // ── Test 7: TotalRecoveredRevenue ─────────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_RecoveredRevenue_SumsAppointmentBookedEvents()
    {
        // Arrange
        await SeedAsync(
            MakeEvent("appointment_booked", recoveredRevenue: 50m),
            MakeEvent("appointment_booked", recoveredRevenue: 75.50m),
            MakeEvent("appointment_booked", recoveredRevenue: 0m),   // sin revenue
            MakeEvent("appointment_booked", recoveredRevenue: null),  // null
            MakeEvent("outbound_sent"));

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert
        summary.TotalRecoveredRevenue.Should().Be(125.50m);
        summary.Currency             .Should().Be("EUR");
    }

    // ── Test 8: Filtro por rango de fechas ────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_DateFilter_ExcludesEventsOutsideRange()
    {
        // Arrange: evento de hace 5 días (fuera del rango)
        var old = MakeEvent("outbound_sent", durationMs: 5000,
            occurredAt: DateTimeOffset.UtcNow.AddDays(-5));
        var recent = MakeEvent("outbound_sent", durationMs: 3000,
            occurredAt: DateTimeOffset.UtcNow);

        await SeedAsync(old, recent);

        // Act: rango de los últimos 2 días
        var from    = DateTimeOffset.UtcNow.AddDays(-2);
        var to      = DateTimeOffset.UtcNow.AddDays(1);
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, from, to);

        // Assert: solo el evento reciente
        summary.OutboundSent         .Should().Be(1);
        summary.AvgResponseTimeMs    .Should().BeApproximately(3000.0, 1.0);
    }

    // ── Test 9: Aislamiento multi-tenant ─────────────────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_MultiTenant_OnlyReturnsTenantOwnEvents()
    {
        // Arrange: eventos de otro tenant
        var otherTenant = Guid.NewGuid();
        await SeedAsync(
            MakeEvent("outbound_sent",       durationMs: 4000),
            MakeEvent("appointment_booked",  recoveredRevenue: 80m),
            MakeEvent("outbound_sent",       durationMs: 6000,  tenantId: otherTenant),
            MakeEvent("appointment_booked",  recoveredRevenue: 999m, tenantId: otherTenant));

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert: solo los eventos del tenant correcto
        summary.OutboundSent           .Should().Be(1);
        summary.AppointmentsBooked     .Should().Be(1);
        summary.TotalRecoveredRevenue  .Should().Be(80m);
    }

    // ── Test 10: Resumen vacío cuando no hay eventos ──────────────────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_NoEvents_ReturnsZeroSummary()
    {
        // Capturar los rangos ANTES de llamar al servicio para evitar desfase de tiempo
        var from = From;
        var to   = To;

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, from, to);

        // Assert
        summary.MissedCallsReceived  .Should().Be(0);
        summary.OutboundSent         .Should().Be(0);
        summary.OutboundFailed       .Should().Be(0);
        summary.PatientReplies       .Should().Be(0);
        summary.AppointmentsBooked   .Should().Be(0);
        summary.ConversionRate       .Should().Be(0.0);
        summary.AvgResponseTimeMs    .Should().Be(0.0);
        summary.P95ResponseTimeMs    .Should().Be(0.0);
        summary.TotalRecoveredRevenue.Should().Be(0m);
        summary.Currency             .Should().Be("EUR");
        summary.From                 .Should().Be(from);
        summary.To                   .Should().Be(to);
    }

    // ── Test 11: Eventos de otro flow_id no contaminan flow_01 ───────────────

    [Fact]
    public async Task GetFlow01SummaryAsync_OtherFlowId_NotIncludedInFlow01Summary()
    {
        // Arrange: insertar directamente eventos con flow_id diferente
        _db.FlowMetricsEvents.Add(new FlowMetricsEvent
        {
            TenantId      = _tenantId,
            FlowId        = "flow_02",  // diferente
            MetricType    = "outbound_sent",
            DurationMs    = 10000,
            CorrelationId = "other-flow",
        });
        _db.FlowMetricsEvents.Add(new FlowMetricsEvent
        {
            TenantId      = _tenantId,
            FlowId        = "flow_01",
            MetricType    = "outbound_sent",
            DurationMs    = 2000,
            CorrelationId = "flow01-event",
        });
        await _db.SaveChangesAsync();

        // Act
        var summary = await _svc.GetFlow01SummaryAsync(_tenantId, From, To);

        // Assert: solo el evento de flow_01
        summary.OutboundSent         .Should().Be(1);
        summary.AvgResponseTimeMs    .Should().BeApproximately(2000.0, 1.0);
    }

    // ── Test 12: RecordAsync con DbContext fallido no lanza excepción ─────────

    [Fact]
    public async Task RecordAsync_DatabaseThrows_DoesNotPropagateException()
    {
        // Arrange: usar un DB ya dispuesto para provocar error
        var badOpts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("bad_db")
            .Options;
        var badDb  = new AppDbContext(badOpts);
        badDb.Dispose(); // forzar error

        var svc = new FlowMetricsService(badDb, NullLogger<FlowMetricsService>.Instance);
        var evt = MakeEvent("outbound_sent");

        // Act & Assert: NO debe lanzar excepción
        var act = async () => await svc.RecordAsync(evt);
        await act.Should().NotThrowAsync();
    }
}
