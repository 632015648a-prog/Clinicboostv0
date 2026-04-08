using ClinicBoost.Api.Features.Flow03;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Domain.Appointments;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-07: Flow03 — Recordatorio automático de cita próxima por WhatsApp
//
// OBJETIVO
//   Verificar que el Flow03Orchestrator envía el recordatorio en el momento
//   correcto, con idempotencia robusta y respeto por RGPD.
//
// ESCENARIOS
//   TC-07-A: Cita en ventana de 24 h → recordatorio enviado, ReminderSentAt establecido
//   TC-07-B: ReminderSentAt ya establecido → skip (idempotencia de dominio)
//   TC-07-C: Paciente sin consentimiento RGPD → skip
//   TC-07-D: Cita fuera de ventana (demasiado pronto) → skip not_yet_in_window
//   TC-07-E: Cita ya comenzó → skip appointment_already_started
//   TC-07-F: Config per-tenant reminder_hours_before sobreescribe el default
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-07")]
public sealed class TC07_AppointmentReminderFlow03Tests : SmokeTestDb
{
    private readonly Flow03Options _defaultOpts = new()
    {
        DefaultReminderHoursBeforeAppointment = 24,
        PollIntervalMinutes                    = 15,
        DefaultTemplateSid                     = null,   // sin template → texto libre
    };

    private Flow03Orchestrator BuildOrchestrator(
        IOutboundMessageSender?  sender      = null,
        IFlowMetricsService?     metrics     = null,
        Flow03Options?           opts        = null)
    {
        sender  ??= Substitute.For<IOutboundMessageSender>();
        metrics ??= Substitute.For<IFlowMetricsService>();

        var idempotency = SmokeFixtures.BuildIdempotencyAllowAll();

        return new Flow03Orchestrator(
            Db,
            sender,
            metrics,
            idempotency,
            Options.Create(opts ?? _defaultOpts),
            NullLogger<Flow03Orchestrator>.Instance);
    }

    // ── TC-07-A: Happy path ───────────────────────────────────────────────────

    [Fact(DisplayName = "TC-07-A: cita en ventana de 24 h → recordatorio enviado, ReminderSentAt guardado")]
    public async Task TC07A_AppointmentInWindow_SendsAndSetsReminderSentAt()
    {
        // ARRANGE
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        // Cita justo en la ventana: empieza en ahora + 23 h 45 min
        // ReminderTarget = ahora + 23h45 - 24h = ahora - 15min < ahora + tolerance → en ventana
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(23).AddMinutes(45));

        var fakeSid    = "SMflow03_tc07a";
        var mockSender = Substitute.For<IOutboundMessageSender>();
        mockSender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
                  .Returns(OutboundSendResult.Success(Guid.NewGuid(), fakeSid));

        var mockMetrics = Substitute.For<IFlowMetricsService>();
        var sut = BuildOrchestrator(sender: mockSender, metrics: mockMetrics);

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-A");

        // ASSERT 1: resultado correcto
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("reminder_sent");
        result.TwilioMessageSid.Should().Be(fakeSid);
        result.AppointmentId.Should().Be(apt.Id);

        // ASSERT 2: ReminderSentAt persistido en BD
        var updated = await Db.Appointments.FindAsync(apt.Id);
        updated!.ReminderSentAt.Should().NotBeNull(
            "el orchestrator debe marcar ReminderSentAt tras enviar el recordatorio");
        updated.ReminderSentAt!.Value.Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        // ASSERT 3: métrica reminder_sent registrada
        await mockMetrics.Received(1).RecordAsync(
            Arg.Is<FlowMetricsEvent>(e =>
                e.TenantId    == TenantId   &&
                e.FlowId      == "flow_03"  &&
                e.MetricType  == "reminder_sent"),
            Arg.Any<CancellationToken>());
    }

    // ── TC-07-B: Idempotencia de dominio ─────────────────────────────────────

    [Fact(DisplayName = "TC-07-B: ReminderSentAt ya establecido → skip, no se reenvía")]
    public async Task TC07B_ReminderAlreadySent_IsSkipped()
    {
        // ARRANGE
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc:    DateTimeOffset.UtcNow.AddHours(23),
            reminderSentAt: DateTimeOffset.UtcNow.AddHours(-1));  // ya enviado

        var mockSender = Substitute.For<IOutboundMessageSender>();
        var sut = BuildOrchestrator(sender: mockSender);

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-B");

        // ASSERT: skip, sin envío Twilio
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Be("already_reminded");
        await mockSender.DidNotReceive().SendAsync(
            Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>());
    }

    // ── TC-07-C: Sin consentimiento RGPD ─────────────────────────────────────

    [Fact(DisplayName = "TC-07-C: paciente sin RGPD consent → skip, sin envío")]
    public async Task TC07C_NoRgpdConsent_IsSkipped()
    {
        // ARRANGE
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(
            Db, TenantId, rgpdConsent: false);   // ← sin consentimiento
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(23).AddMinutes(45));

        var mockSender = Substitute.For<IOutboundMessageSender>();
        var sut = BuildOrchestrator(sender: mockSender);

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-C");

        // ASSERT
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Be("no_rgpd_consent");
        await mockSender.DidNotReceive().SendAsync(
            Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>());

        // ReminderSentAt NO debe haberse modificado
        var apt2 = await Db.Appointments.FindAsync(apt.Id);
        apt2!.ReminderSentAt.Should().BeNull();
    }

    // ── TC-07-D: Cita fuera de ventana ────────────────────────────────────────

    [Fact(DisplayName = "TC-07-D: cita en 48 h con default 24 h → still too early → skipped")]
    public async Task TC07D_AppointmentTooFarAhead_IsSkipped()
    {
        // ARRANGE
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        // Cita en 48 h: reminderTarget = now + 48h - 24h = now + 24h
        // tolerance = 15*2+5 = 35 min
        // now + 24h > now + 35min → fuera de ventana
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(48));

        var mockSender = Substitute.For<IOutboundMessageSender>();
        var sut = BuildOrchestrator(sender: mockSender);

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-D");

        // ASSERT
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Be("not_yet_in_window",
            "la cita está a 48 h y el reminder es a 24 h: aún es pronto");
        await mockSender.DidNotReceive().SendAsync(
            Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>());
    }

    // ── TC-07-E: Cita ya comenzó ──────────────────────────────────────────────

    [Fact(DisplayName = "TC-07-E: cita ya comenzó → skip appointment_already_started")]
    public async Task TC07E_AppointmentAlreadyStarted_IsSkipped()
    {
        // ARRANGE
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(-1));  // ya empezó hace 1h

        var sut = BuildOrchestrator();

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-E");

        // ASSERT
        result.FlowStep.Should().Be("skipped");
        result.ErrorMessage.Should().Be("appointment_already_started");
    }

    // ── TC-07-F: RuleConfig por tenant sobreescribe el default ────────────────

    [Fact(DisplayName = "TC-07-F: reminder_hours_before=2 por tenant — ventana calculada con el valor correcto")]
    public async Task TC07F_PerTenantReminderHours_OverridesDefault()
    {
        // ARRANGE: tenant con reminder_hours_before = 2 (en lugar del default 24)
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        await SmokeFixtures.SeedRuleConfigAsync(
            Db, TenantId,
            flowId:    "flow_03",
            ruleKey:   "reminder_hours_before",
            ruleValue: "2",
            valueType: "integer");

        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        // Cita en 1 hora y media:
        // reminderTarget = now + 1.5h - 2h = now - 0.5h (hace 30 min)
        // → now - 0.5h < now + tolerance → EN VENTANA → debe enviar
        var apt = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(1).AddMinutes(30));

        var mockSender = Substitute.For<IOutboundMessageSender>();
        mockSender.SendAsync(Arg.Any<OutboundMessageRequest>(), Arg.Any<CancellationToken>())
                  .Returns(OutboundSendResult.Success(Guid.NewGuid(), "SMflow03_tc07f"));

        var sut = BuildOrchestrator(sender: mockSender);

        // ACT
        var result = await sut.ExecuteAsync(TenantId, apt.Id, "corr-tc07-F");

        // ASSERT: con 2h de config, cita en 1.5h → está en ventana → envía
        result.IsSuccess.Should().BeTrue();
        result.FlowStep.Should().Be("reminder_sent",
            "con reminder_hours_before=2 y cita en 1.5h, el reminder está en ventana");

        // Verificar que cita en 3h queda fuera de la ventana de 2h
        // (reminderTarget = now + 3h - 2h = now + 1h > tolerance de 35 min)
        var aptTooEarly = await SmokeFixtures.SeedAppointmentAsync(
            Db, TenantId, patient.Id,
            startsAtUtc: DateTimeOffset.UtcNow.AddHours(3));

        var sut2 = new Flow03Orchestrator(
            Db, mockSender, Substitute.For<IFlowMetricsService>(),
            SmokeFixtures.BuildIdempotencyAllowAll(),
            Options.Create(new Flow03Options
            {
                DefaultReminderHoursBeforeAppointment = 24,
                PollIntervalMinutes                    = 15,
            }),
            NullLogger<Flow03Orchestrator>.Instance);

        var resultTooEarly = await sut2.ExecuteAsync(TenantId, aptTooEarly.Id, "corr-tc07-F2");

        // Con la RuleConfig de 2h en BD: reminderTarget = now+1h → > now+35min → pronto
        resultTooEarly.FlowStep.Should().Be("skipped",
            "cita en 3h con reminder_hours_before=2: debe esperar ~1h más");
        resultTooEarly.ErrorMessage.Should().Be("not_yet_in_window");
    }
}
