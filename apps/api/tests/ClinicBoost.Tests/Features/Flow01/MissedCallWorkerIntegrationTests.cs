using System.Net;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Flow01;

// ════════════════════════════════════════════════════════════════════════════
// MissedCallWorkerIntegrationTests
//
// Test de integración del flujo completo:
//   MissedCallJob → Flow01Orchestrator →
//   TwilioOutboundMessageSender (HTTP fake) → FlowMetricsService (real DB)
//
// Verifica el pipeline end-to-end sin llamadas reales a Twilio:
//   · AutomationRun se crea con status=completed/failed/skipped.
//   · FlowMetricsEvents se persisten correctamente.
//   · Message en BD con status=sent o failed.
//   · RevenueEvent creado en RecordAppointmentBookedAsync.
//   · Llamadas no perdidas (answered) son ignoradas.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MissedCallWorkerIntegrationTests : IDisposable
{
    private readonly AppDbContext  _db;
    private readonly Guid          _tenantId = Guid.NewGuid();
    private readonly TwilioOptions _twilioOpts = new()
    {
        AccountSid = "ACtest_integration_sid",
        AuthToken  = "test_auth_token_integration",
    };

    public MissedCallWorkerIntegrationTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new AppDbContext(opts);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IIdempotencyService BuildIdempotencyAllowAll()
    {
        var idempotency = Substitute.For<IIdempotencyService>();
        // Sobrecarga no-genérica
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        // Sobrecarga genérica (usada por Flow01Orchestrator con objeto anónimo)
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        return idempotency;
    }

    private Flow01Orchestrator BuildOrchestrator(
        HttpStatusCode twilioStatus   = HttpStatusCode.Created,
        string?        twilioResponse = null)
    {
        var response = twilioResponse ?? $"{{\"sid\":\"SMintegration_{Guid.NewGuid():N}\"}}";
        var handler  = new FakeHttpMessageHandler(twilioStatus, response);
        var client   = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        var sender = new TwilioOutboundMessageSender(
            _db,
            Options.Create(_twilioOpts),
            factory,
            NullLogger<TwilioOutboundMessageSender>.Instance);

        var metrics    = new FlowMetricsService(_db, NullLogger<FlowMetricsService>.Instance);
        var idempotency = BuildIdempotencyAllowAll();

        return new Flow01Orchestrator(
            _db,
            sender,
            metrics,
            idempotency,
            Options.Create(new Flow01Options
            {
                DefaultTemplateSid = "HXintegration_template",
                MaxDelayMinutes    = 60,
            }),
            NullLogger<Flow01Orchestrator>.Instance);
    }

    private async Task<Patient> AddPatientAsync(
        string phone = "+34600111000", bool rgpd = true)
    {
        var patient = new Patient
        {
            TenantId    = _tenantId,
            FullName    = "Pedro Martínez",
            Phone       = phone,
            Status      = PatientStatus.Active,
            RgpdConsent = rgpd,
        };
        _db.Patients.Add(patient);
        await _db.SaveChangesAsync();
        return patient;
    }

    private MissedCallJob BuildJob(
        string callStatus = "no-answer",
        string phone      = "+34600111000",
        string callSid    = "CAintegration001",
        DateTimeOffset? receivedAt = null)
        => new MissedCallJob(
            TenantId:         _tenantId,
            CallSid:          callSid,
            CallerPhone:      phone,
            ClinicPhone:      "+34900000001",
            CallStatus:       callStatus,
            ReceivedAt:       receivedAt ?? DateTimeOffset.UtcNow.AddSeconds(-10),
            ProcessedEventId: Guid.NewGuid(),
            CorrelationId:    $"corr-{Guid.NewGuid():N}");

    // Simula la lógica de procesamiento de MissedCallWorker.ProcessJobAsync
    private async Task RunJobAsync(
        MissedCallJob      job,
        Flow01Orchestrator orchestrator)
    {
        var missedCallStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "no-answer", "busy", "failed" };

        if (!missedCallStatuses.Contains(job.CallStatus))
            return;

        var run = new AutomationRun
        {
            TenantId      = _tenantId,
            FlowId        = "flow_01",
            TriggerType   = "event",
            TriggerRef    = job.CallSid,
            Status        = "running",
            CorrelationId = Guid.TryParse(job.CorrelationId, out var corrId)
                                ? corrId : Guid.NewGuid(),
        };
        _db.AutomationRuns.Add(run);
        await _db.SaveChangesAsync();

        var result = await orchestrator.ExecuteAsync(
            tenantId:       job.TenantId,
            callSid:        job.CallSid,
            callerPhone:    job.CallerPhone,
            clinicPhone:    job.ClinicPhone,
            callReceivedAt: job.ReceivedAt,
            correlationId:  job.CorrelationId);

        if (result.IsSuccess)
        {
            run.ItemsProcessed = 1;
            run.ItemsSucceeded = 1;
            run.Status         = result.FlowStep == "skipped" ? "skipped" : "completed";
        }
        else
        {
            run.ItemsProcessed = 1;
            run.ItemsFailed    = 1;
            run.Status         = "failed";
            run.ErrorMessage   = result.ErrorMessage;
        }
        run.FinishedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Test 1: Flujo completo OK → AutomationRun=completed, Message=sent ─────

    [Fact]
    public async Task FullFlow_PatientWithConsent_AutomationRunCompletedAndMessageSent()
    {
        // Arrange
        await AddPatientAsync(rgpd: true);
        var orchestrator = BuildOrchestrator();
        var job = BuildJob();

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: AutomationRun completed
        var run = await _db.AutomationRuns.FirstOrDefaultAsync();
        run.Should().NotBeNull();
        run!.Status        .Should().Be("completed");
        run.FinishedAt     .Should().NotBeNull();
        run.ItemsProcessed .Should().Be(1);
        run.ItemsSucceeded .Should().Be(1);
        run.ItemsFailed    .Should().Be(0);

        // Message en BD con status=sent
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull();
        msg!.Status    .Should().Be("sent");
        msg.Direction  .Should().Be("outbound");
        msg.Channel    .Should().Be("whatsapp");
        msg.TenantId   .Should().Be(_tenantId);

        // FlowMetricsEvents: missed_call_received + outbound_sent
        var metrics = await _db.FlowMetricsEvents.ToListAsync();
        metrics.Should().Contain(e => e.MetricType == "missed_call_received");
        metrics.Should().Contain(e => e.MetricType == "outbound_sent");
        metrics.Count(e => e.MetricType == "outbound_failed").Should().Be(0);
    }

    // ── Test 2: Paciente sin consentimiento → AutomationRun=skipped ──────────

    [Fact]
    public async Task FullFlow_PatientWithoutRgpd_AutomationRunSkippedNoMessage()
    {
        // Arrange
        await AddPatientAsync(rgpd: false);
        var orchestrator = BuildOrchestrator();
        var job = BuildJob(callSid: "CA_no_rgpd_integration");

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: AutomationRun skipped
        var run = await _db.AutomationRuns.FirstOrDefaultAsync();
        run!.Status.Should().Be("skipped");

        // Sin mensajes enviados
        var msgCount = await _db.Messages.CountAsync();
        msgCount.Should().Be(0);

        // Métricas: missed_call_received + flow_skipped
        var metrics = await _db.FlowMetricsEvents.ToListAsync();
        metrics.Should().Contain(e => e.MetricType == "flow_skipped");
        metrics.Should().Contain(e => e.MetricType == "missed_call_received");
    }

    // ── Test 3: Twilio falla → AutomationRun=failed, Message=failed ──────────

    [Fact]
    public async Task FullFlow_TwilioFails_AutomationRunFailedAndMessageFailed()
    {
        // Arrange
        await AddPatientAsync(rgpd: true);
        var orchestrator = BuildOrchestrator(
            HttpStatusCode.BadRequest,
            "{\"code\":30006,\"message\":\"Destination unreachable\"}");
        var job = BuildJob(callSid: "CA_twilio_fail_integration");

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: AutomationRun failed
        var run = await _db.AutomationRuns.FirstOrDefaultAsync();
        run!.Status     .Should().Be("failed");
        run.ErrorMessage.Should().Contain("30006");

        // Message en BD con status=failed
        var msg = await _db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull();
        msg!.Status   .Should().Be("failed");
        msg.ErrorCode .Should().Be("30006");

        // Métrica: outbound_failed
        var metrics = await _db.FlowMetricsEvents.ToListAsync();
        metrics.Should().Contain(e => e.MetricType == "outbound_failed" && e.ErrorCode == "30006");
    }

    // ── Test 4: Estado "completed" (llamada contestada) → nada creado ─────────

    [Fact]
    public async Task FullFlow_AnsweredCall_NothingCreated()
    {
        // Arrange
        var orchestrator = BuildOrchestrator();
        var job = BuildJob(callStatus: "completed"); // llamada contestada, no perdida

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: sin AutomationRun, sin mensajes, sin métricas
        (await _db.AutomationRuns.CountAsync()).Should().Be(0);
        (await _db.Messages.CountAsync())      .Should().Be(0);
        (await _db.FlowMetricsEvents.CountAsync()).Should().Be(0);
    }

    // ── Test 5: ResponseTimeMs se calcula desde callReceivedAt ───────────────

    [Fact]
    public async Task FullFlow_ResponseTimeMs_CalculatedFromCallReceivedAt()
    {
        // Arrange: llamada recibida hace ~8 segundos
        await AddPatientAsync(rgpd: true);
        var callReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-8);
        var orchestrator   = BuildOrchestrator();
        var job = BuildJob(callSid: "CA_timing_test", receivedAt: callReceivedAt);

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: DurationMs en outbound_sent > 7000ms
        var metric = await _db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "outbound_sent");
        metric.Should().NotBeNull();
        metric!.DurationMs.Should().BeGreaterThan(7000);
    }

    // ── Test 6: Paciente nuevo sin consentimiento → skipped ───────────────────

    [Fact]
    public async Task FullFlow_UnknownPhone_CreatesNewPatientAndSkipsDueToNoRgpd()
    {
        // Arrange: no hay paciente registrado para este número
        var orchestrator = BuildOrchestrator();
        var job = BuildJob(phone: "+34699888777", callSid: "CA_new_patient_integration");

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: paciente creado sin consentimiento RGPD
        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Phone == "+34699888777" && p.TenantId == _tenantId);
        patient.Should().NotBeNull();
        patient!.RgpdConsent.Should().BeFalse();

        // Run skipped
        var run = await _db.AutomationRuns.FirstOrDefaultAsync();
        run!.Status.Should().Be("skipped");
    }

    // ── Test 7: RecordAppointmentBookedAsync crea RevenueEvent ───────────────

    [Fact]
    public async Task RecordAppointmentBooked_PersistsRevenueEventAndBookingMetric()
    {
        // Arrange
        var orchestrator  = BuildOrchestrator();
        var appointmentId = Guid.NewGuid();
        var patientId     = Guid.NewGuid();

        // Act
        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       _tenantId,
            patientId:      patientId,
            appointmentId:  appointmentId,
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-3),
            revenue:        80m,
            correlationId:  "booking-corr-integration");

        // Assert: RevenueEvent en BD
        var revEvent = await _db.RevenueEvents
            .FirstOrDefaultAsync(r => r.AppointmentId == appointmentId);
        revEvent.Should().NotBeNull();
        revEvent!.Amount             .Should().Be(80m);
        revEvent.SuccessFeeAmount    .Should().Be(12m);      // 15% de 80
        revEvent.EventType           .Should().Be("missed_call_converted");
        revEvent.FlowId              .Should().Be("flow_01");
        revEvent.IsSuccessFeeEligible.Should().BeTrue();
        revEvent.Currency            .Should().Be("EUR");

        // FlowMetricsEvent: appointment_booked con revenue y duración
        var metric = await _db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "appointment_booked");
        metric.Should().NotBeNull();
        metric!.RecoveredRevenue.Should().Be(80m);
        metric.AppointmentId    .Should().Be(appointmentId);
        metric.DurationMs       .Should().BeGreaterThan(0);
    }

    // ── Test 8: Job "busy" también es llamada perdida ─────────────────────────

    [Fact]
    public async Task FullFlow_BusyStatus_TreatedAsMissedCall()
    {
        // Arrange
        await AddPatientAsync(rgpd: true);
        var orchestrator = BuildOrchestrator();
        var job = BuildJob(callStatus: "busy", callSid: "CA_busy_integration");

        // Act
        await RunJobAsync(job, orchestrator);

        // Assert: procesado (no ignorado)
        var run = await _db.AutomationRuns.FirstOrDefaultAsync();
        run.Should().NotBeNull();
        run!.Status.Should().BeOneOf("completed", "failed", "skipped");
    }

    // ── Test 9: Revenue cero → sin RevenueEvent ───────────────────────────────

    [Fact]
    public async Task RecordAppointmentBooked_ZeroRevenue_NoRevenueEventCreated()
    {
        // Arrange
        var orchestrator = BuildOrchestrator();

        // Act
        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       _tenantId,
            patientId:      Guid.NewGuid(),
            appointmentId:  Guid.NewGuid(),
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            revenue:        0m,
            correlationId:  "zero-rev-corr");

        // Assert: sin RevenueEvent (revenue = 0)
        (await _db.RevenueEvents.CountAsync()).Should().Be(0);

        // Pero sí existe la métrica appointment_booked
        var metric = await _db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "appointment_booked");
        metric.Should().NotBeNull();
        metric!.RecoveredRevenue.Should().Be(0m);
    }

    // ── Test 10: Conversión KPI: outbound_sent → appointment_booked ──────────

    [Fact]
    public async Task FullFlow_Conversion_MetricsSummaryReflectsBooking()
    {
        // Arrange: flujo completo + booking
        await AddPatientAsync(rgpd: true);
        var orchestrator = BuildOrchestrator();
        var job = BuildJob(callSid: "CA_conversion_test");

        // Ejecutar flujo → genera missed_call_received + outbound_sent
        await RunJobAsync(job, orchestrator);

        // Simular reserva de cita
        var appointmentId = Guid.NewGuid();
        var outboundMetric = await _db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "outbound_sent");
        var outboundSentAt = outboundMetric?.OccurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-2);

        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       _tenantId,
            patientId:      (await _db.Patients.FirstAsync()).Id,
            appointmentId:  appointmentId,
            outboundSentAt: outboundSentAt,
            revenue:        55m,
            correlationId:  "conversion-corr");

        // Assert: summary de métricas correcta
        var metricsService = new FlowMetricsService(_db, NullLogger<FlowMetricsService>.Instance);
        var summary = await metricsService.GetFlow01SummaryAsync(
            _tenantId,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        summary.MissedCallsReceived  .Should().Be(1);
        summary.OutboundSent         .Should().Be(1);
        summary.AppointmentsBooked   .Should().Be(1);
        summary.ConversionRate       .Should().Be(1.0);  // 1 enviado, 1 reservado
        summary.TotalRecoveredRevenue.Should().Be(55m);
        summary.AvgResponseTimeMs    .Should().BeGreaterThan(0);
    }
}
