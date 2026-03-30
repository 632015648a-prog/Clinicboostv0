using System.Net;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Domain.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// Flow01OrchestratorTests
//
// Tests del flujo end-to-end:
//   Llamada perdida → WhatsApp saliente → métricas de tiempo y revenue.
//
// Estrategia:
//  · AppDbContext InMemory (sin transacciones).
//  · IOutboundMessageSender mockeado con NSubstitute.
//  · IFlowMetricsService mockeado para verificar registros de KPI.
//  · IIdempotencyService mockeado para probar idempotencia.
// ════════════════════════════════════════════════════════════════════════════

public sealed class Flow01OrchestratorTests : IDisposable
{
    private readonly AppDbContext            _db;
    private readonly IOutboundMessageSender  _sender;
    private readonly IFlowMetricsService     _metrics;
    private readonly IIdempotencyService     _idempotency;
    private readonly Guid                    _tenantId;

    public Flow01OrchestratorTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db          = new AppDbContext(opts);
        _sender      = Substitute.For<IOutboundMessageSender>();
        _metrics     = Substitute.For<IFlowMetricsService>();
        _idempotency = Substitute.For<IIdempotencyService>();
        _tenantId    = Guid.NewGuid();

        // Por defecto: idempotency permite el procesamiento (intercepta ambas sobrecargas)
        _idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        // Sobrecarga genérica (usada por Flow01Orchestrator con objeto anónimo como payload)
        _idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    public void Dispose() => _db.Dispose();

    private Flow01Orchestrator CreateOrchestrator(Flow01Options? opts = null)
        => new(
            _db,
            _sender,
            _metrics,
            _idempotency,
            Options.Create(opts ?? new Flow01Options { DefaultTemplateSid = "HXtest_template" }),
            NullLogger<Flow01Orchestrator>.Instance);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Patient> AddPatientWithConsent(
        bool rgpd = true, string phone = "+34600000001")
    {
        var p = new Patient
        {
            TenantId    = _tenantId,
            FullName    = "María García",
            Phone       = phone,
            Status      = PatientStatus.Active,
            RgpdConsent = rgpd,
        };
        _db.Patients.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private static OutboundSendResult OkSendResult()
        => OutboundSendResult.Success(Guid.NewGuid(), "SMtesttwiliosid123");

    private static OutboundSendResult FailedSendResult()
        => OutboundSendResult.TwilioFailure(Guid.NewGuid(), "30006", "Destination unreachable");

    // ── Test 1: flujo OK con consentimiento ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PatientWithConsent_SendsWhatsAppAndRecordsMetrics()
    {
        // Arrange
        await AddPatientWithConsent(rgpd: true);
        _sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
               .Returns(OkSendResult());

        var orchestrator = CreateOrchestrator();
        var callReceived = DateTimeOffset.UtcNow.AddSeconds(-5);

        // Act
        var result = await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_test_001",
            callerPhone:    "+34600000001",
            clinicPhone:    "+34900000000",
            callReceivedAt: callReceived,
            correlationId:  "corr-001");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("completed");
        result.TwilioMessageSid.Should().Be("SMtesttwiliosid123");
        result.ResponseTimeMs.Should().BeGreaterThan(0);

        // El sender debe haber sido llamado exactamente una vez
        await _sender.Received(1).SendAsync(
            Arg.Is<OutboundMessageRequest>(r =>
                r.Channel   == "whatsapp" &&
                r.FlowId    == "flow_01"  &&
                r.TenantId  == _tenantId),
            Arg.Any<CancellationToken>());

        // Métricas: missed_call_received + outbound_sent
        await _metrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e => e.MetricType == "missed_call_received"),
            Arg.Any<CancellationToken>());

        await _metrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e =>
                e.MetricType    == "outbound_sent"     &&
                e.DurationMs    != null                &&
                e.DurationMs    >= 0                   &&
                e.TwilioMessageSid == "SMtesttwiliosid123"),
            Arg.Any<CancellationToken>());
    }

    // ── Test 2: flujo sin consentimiento RGPD ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PatientWithoutRgpd_SkipsFlowAndRecordsSkippedMetric()
    {
        // Arrange
        await AddPatientWithConsent(rgpd: false);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_no_rgpd",
            callerPhone:    "+34600000001",
            clinicPhone:    "+34900000000",
            callReceivedAt: DateTimeOffset.UtcNow,
            correlationId:  "corr-002");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Contain("RGPD");

        // El sender NO debe haberse llamado
        await _sender.DidNotReceive().SendAsync(
            Arg.Any<OutboundMessageRequest>(),
            Arg.Any<CancellationToken>());

        // Métrica: flow_skipped
        await _metrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e => e.MetricType == "flow_skipped"),
            Arg.Any<CancellationToken>());
    }

    // ── Test 3: Twilio falla ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TwilioFails_RecordsOutboundFailedAndReturnsFailure()
    {
        // Arrange
        await AddPatientWithConsent(rgpd: true);
        _sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
               .Returns(FailedSendResult());

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_twilio_fail",
            callerPhone:    "+34600000001",
            clinicPhone:    "+34900000000",
            callReceivedAt: DateTimeOffset.UtcNow,
            correlationId:  "corr-003");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.FlowStep.Should().Be("outbound_send");
        result.ErrorMessage.Should().Contain("30006");

        // Métrica: outbound_failed
        await _metrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e =>
                e.MetricType == "outbound_failed" &&
                e.ErrorCode  == "30006"),
            Arg.Any<CancellationToken>());
    }

    // ── Test 4: Idempotencia ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AlreadyProcessed_SkipsWithoutSending()
    {
        // Arrange: idempotency dice "ya procesado"
        _idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.Duplicate(Guid.NewGuid(), DateTimeOffset.UtcNow));
        _idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.Duplicate(Guid.NewGuid(), DateTimeOffset.UtcNow));

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_duplicate",
            callerPhone:    "+34600000001",
            clinicPhone:    "+34900000000",
            callReceivedAt: DateTimeOffset.UtcNow,
            correlationId:  "corr-004");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("skipped");

        // Nada debe enviarse
        await _sender.DidNotReceive().SendAsync(
            Arg.Any<OutboundMessageRequest>(),
            Arg.Any<CancellationToken>());

        // No debe haber métricas de llamada recibida (ya contadas en el primer intento)
        await _metrics.DidNotReceive().RecordAsync(
            Arg.Is<FlowMetricsEvent>(e => e.MetricType == "missed_call_received"),
            Arg.Any<CancellationToken>());
    }

    // ── Test 5: Paciente nuevo se crea automáticamente ────────────────────────

    [Fact]
    public async Task ExecuteAsync_NewCallerPhone_CreatesPatientAndSends()
    {
        // Arrange: no hay paciente en BD para este teléfono
        _sender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
               .Returns(OkSendResult());

        var orchestrator = CreateOrchestrator();

        // Act — teléfono desconocido
        var result = await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_new_patient",
            callerPhone:    "+34655999888",
            clinicPhone:    "+34900000000",
            callReceivedAt: DateTimeOffset.UtcNow,
            correlationId:  "corr-005");

        // Assert: flujo skipped porque el paciente nuevo no tiene consentimiento RGPD
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Contain("RGPD");

        // Verificar que el paciente fue creado en BD
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Phone == "+34655999888" && p.TenantId == _tenantId);
        patient.Should().NotBeNull();
        patient!.RgpdConsent.Should().BeFalse(); // sin consentimiento hasta el flujo WA
    }

    // ── Test 6: Número de teléfono incluido en el mensaje de envío ────────────

    [Fact]
    public async Task ExecuteAsync_WhatsAppPrefix_AddedCorrectly()
    {
        // Arrange
        await AddPatientWithConsent(rgpd: true);
        OutboundMessageRequest? capturedRequest = null;
        _sender.SendAsync(
                Arg.Do<OutboundMessageRequest>(r => capturedRequest = r),
                Arg.Any<CancellationToken>())
               .Returns(OkSendResult());

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.ExecuteAsync(
            tenantId:       _tenantId,
            callSid:        "CA_prefix",
            callerPhone:    "+34600000001",
            clinicPhone:    "+34900000000",
            callReceivedAt: DateTimeOffset.UtcNow,
            correlationId:  "corr-006");

        // Assert: el prefijo "whatsapp:" se añade correctamente
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ToPhone  .Should().Be("whatsapp:+34600000001");
        capturedRequest.FromPhone .Should().Be("whatsapp:+34900000000");
        capturedRequest.Channel   .Should().Be("whatsapp");
    }

    // ── Test 7: Revenue booking ───────────────────────────────────────────────

    [Fact]
    public async Task RecordAppointmentBookedAsync_PersistsRevenueEvent()
    {
        // Arrange
        var patientId     = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        var outboundAt    = DateTimeOffset.UtcNow.AddMinutes(-5);

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       _tenantId,
            patientId:      patientId,
            appointmentId:  appointmentId,
            outboundSentAt: outboundAt,
            revenue:        60m,
            correlationId:  "corr-007");

        // Assert: métrica appointment_booked registrada
        await _metrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e =>
                e.MetricType       == "appointment_booked" &&
                e.RecoveredRevenue == 60m                  &&
                e.AppointmentId    == appointmentId),
            Arg.Any<CancellationToken>());

        // RevenueEvent persiste en BD (success fee 15%)
        var revenueEvent = await _db.RevenueEvents
            .FirstOrDefaultAsync(r =>
                r.TenantId      == _tenantId     &&
                r.AppointmentId == appointmentId &&
                r.EventType     == "missed_call_converted");

        revenueEvent.Should().NotBeNull();
        revenueEvent!.Amount          .Should().Be(60m);
        revenueEvent.SuccessFeeAmount .Should().Be(9m);  // 15% de 60
        revenueEvent.FlowId           .Should().Be("flow_01");
        revenueEvent.IsSuccessFeeEligible.Should().BeTrue();
    }

    [Fact]
    public async Task RecordAppointmentBookedAsync_ZeroRevenue_NoRevenueEventCreated()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       _tenantId,
            patientId:      Guid.NewGuid(),
            appointmentId:  Guid.NewGuid(),
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            revenue:        0m,
            correlationId:  "corr-008");

        // Assert: sin revenue, no se crea RevenueEvent
        var count = await _db.RevenueEvents.CountAsync();
        count.Should().Be(0);
    }
}
