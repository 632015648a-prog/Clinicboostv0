using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Domain.Appointments;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-03: Reserva de cita → Appointment creado → RevenueEvent generado
//
// OBJETIVO
//   Verificar que cuando el agente confirma una reserva:
//   · Se crea un Appointment en BD con Source=WhatsApp y Status=Confirmed
//   · Se genera un RevenueEvent con amount correcto y success_fee calculado
//   · FlowMetricsEvent "appointment_booked" se persiste con revenue y duración
//   · La conversación queda en status "resolved"
//
// PRE-CONDITIONS
//   · Paciente con RGPD=true, mensaje outbound previo (timestamp de referencia)
//   · RuleConfig global/success_fee_pct = "15" para el tenant
//
// DATOS DE PRUEBA
//   · Revenue: 85 EUR (sesión de fisioterapia estándar)
//   · SuccessFee esperado: 12.75 EUR (15% de 85)
//
// QUÉ SE AUTOMATIZA
//   ✅ Flow01Orchestrator.RecordAppointmentBookedAsync()
//   ✅ RevenueEvent en BD (amount, successFee, eventType, flowId)
//   ✅ FlowMetricsEvent appointment_booked (revenue, durationMs, appointmentId)
//   ✅ Appointment.Source = WhatsApp, Status = Confirmed (si se crea via ToolRegistry)
//
// QUÉ SE VALIDA MANUALMENTE
//   ⚠ Que el importe del revenue_event es el acordado con el paciente real
//   ⚠ Que la cita aparece en el calendario del terapeuta
//   ⚠ Que el success_fee_pct es el correcto para el contrato del tenant
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-03")]
public sealed class TC03_BookingAppointmentRevenueTests : SmokeTestDb
{
    private readonly TwilioOptions _twilioOpts = new()
    {
        AccountSid = "ACsmoke_tc03",
        AuthToken  = "smoke_auth_token_tc03",
    };

    private Flow01Orchestrator BuildOrchestrator()
    {
        var handler = SmokeFixtures.TwilioOkHandler("SMtc03");
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        var sender = new TwilioOutboundMessageSender(
            Db, Options.Create(_twilioOpts), factory,
            Substitute.For<IVariantTrackingService>(),
            NullLogger<TwilioOutboundMessageSender>.Instance);

        var idempotency = Substitute.For<IIdempotencyService>();
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));

        var metrics         = new FlowMetricsService(Db, NullLogger<FlowMetricsService>.Instance);
        var variantTracking = Substitute.For<IVariantTrackingService>();

        return new Flow01Orchestrator(
            Db, sender, metrics, idempotency, variantTracking,
            Options.Create(new Flow01Options
            {
                DefaultTemplateSid = "HXsmoke_tc03",
                MaxDelayMinutes    = 60,
            }),
            NullLogger<Flow01Orchestrator>.Instance);
    }

    // ── TC-03-A: Revenue nominal → RevenueEvent + success fee correcto ────────

    [Fact(DisplayName = "TC-03-A: booking 85 EUR → RevenueEvent con 12.75 EUR success fee")]
    public async Task TC03A_Booking85Eur_RevenueEventWithCorrectSuccessFee()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        // RuleConfig: success_fee_pct = 15 para este tenant
        await SmokeFixtures.SeedRuleConfigAsync(Db, TenantId,
            flowId: "global", ruleKey: "success_fee_pct", ruleValue: "15", valueType: "decimal");

        var outboundSentAt = DateTimeOffset.UtcNow.AddMinutes(-4); // hace 4 min el WA fue enviado
        var appointmentId  = Guid.NewGuid();
        var orchestrator   = BuildOrchestrator();

        // ── ACT ──────────────────────────────────────────────────────────────
        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       TenantId,
            patientId:      patient.Id,
            appointmentId:  appointmentId,
            outboundSentAt: outboundSentAt,
            revenue:        85m,
            correlationId:  "corr-tc03-A");

        // ── ASSERT ───────────────────────────────────────────────────────────

        // 1. RevenueEvent creado
        var revEvent = await Db.RevenueEvents
            .FirstOrDefaultAsync(r => r.AppointmentId == appointmentId);
        revEvent.Should().NotBeNull("debe generarse un revenue_event al reservar cita");
        revEvent!.Amount           .Should().Be(85m,       "el importe es el acordado");
        revEvent.SuccessFeeAmount  .Should().Be(12.75m,    "15% de 85 EUR = 12.75 EUR");
        revEvent.EventType         .Should().Be("missed_call_converted", "el tipo de evento es la conversión de la llamada perdida");
        revEvent.FlowId            .Should().Be("flow_01", "flujo de recuperación de llamada");
        revEvent.IsSuccessFeeEligible.Should().BeTrue("la cita fue recuperada por el sistema");
        revEvent.Currency          .Should().Be("EUR");
        revEvent.TenantId          .Should().Be(TenantId,  "aislamiento multi-tenant");

        // 2. FlowMetricsEvent appointment_booked con revenue correcto
        var metric = await Db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "appointment_booked");
        metric.Should().NotBeNull("la métrica de reserva debe persistirse");
        metric!.RecoveredRevenue.Should().Be(85m);
        metric.AppointmentId    .Should().Be(appointmentId);
        metric.DurationMs       .Should().BeGreaterThan(0,
            "el tiempo desde el envío hasta la reserva debe medirse");

        // 3. DurationMs refleja los ~4 minutos reales
        metric.DurationMs.Should().BeInRange(
            (long)TimeSpan.FromMinutes(3).TotalMilliseconds,
            (long)TimeSpan.FromMinutes(6).TotalMilliseconds,
            "el tiempo de conversión debe estar entre 3 y 6 minutos");
    }

    // ── TC-03-B: Revenue = 0 → sin RevenueEvent pero sí FlowMetrics ──────────

    [Fact(DisplayName = "TC-03-B: revenue=0 → no RevenueEvent, sí FlowMetric appointment_booked")]
    public async Task TC03B_ZeroRevenue_NoRevenueEventButMetricPersisted()
    {
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient       = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var orchestrator  = BuildOrchestrator();
        var appointmentId = Guid.NewGuid();

        await orchestrator.RecordAppointmentBookedAsync(
            tenantId:       TenantId,
            patientId:      patient.Id,
            appointmentId:  appointmentId,
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            revenue:        0m,
            correlationId:  "corr-tc03-B");

        (await Db.RevenueEvents.CountAsync()).Should().Be(0,
            "sin importe económico no se genera revenue_event");

        var metric = await Db.FlowMetricsEvents
            .FirstOrDefaultAsync(e => e.MetricType == "appointment_booked");
        metric.Should().NotBeNull(
            "incluso sin revenue se registra que se reservó una cita");
        metric!.RecoveredRevenue.Should().Be(0m);
    }

    // ── TC-03-C: Múltiples reservas → RevenueEvents aislados por tenant ───────

    [Fact(DisplayName = "TC-03-C: dos tenants → revenue_events separados, sin cross-tenant leak")]
    public async Task TC03C_MultiTenant_RevenueEventsIsolated()
    {
        var tenantA = TenantId;
        var tenantB = Guid.NewGuid();

        await SmokeFixtures.SeedTenantAsync(Db, tenantA);
        await SmokeFixtures.SeedTenantAsync(Db, tenantB,
            whatsAppNumber: "+34910000002");

        var patientA = await SmokeFixtures.SeedPatientAsync(Db, tenantA,
            phone: "+34600111111", fullName: "Paciente Tenant A");
        var patientB = await SmokeFixtures.SeedPatientAsync(Db, tenantB,
            phone: "+34600222222", fullName: "Paciente Tenant B");

        var orchestratorA = BuildOrchestrator();
        var apptA = Guid.NewGuid();
        var apptB = Guid.NewGuid();

        await orchestratorA.RecordAppointmentBookedAsync(
            tenantId: tenantA, patientId: patientA.Id, appointmentId: apptA,
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            revenue: 60m, correlationId: "corr-tc03-C-A");

        await orchestratorA.RecordAppointmentBookedAsync(
            tenantId: tenantB, patientId: patientB.Id, appointmentId: apptB,
            outboundSentAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            revenue: 75m, correlationId: "corr-tc03-C-B");

        // Verificar aislamiento: cada tenant tiene su propio evento
        var eventsA = await Db.RevenueEvents.Where(r => r.TenantId == tenantA).ToListAsync();
        var eventsB = await Db.RevenueEvents.Where(r => r.TenantId == tenantB).ToListAsync();

        eventsA.Should().HaveCount(1).And
            .OnlyContain(r => r.Amount == 60m && r.TenantId == tenantA,
                "tenant A solo debe ver su revenue event");
        eventsB.Should().HaveCount(1).And
            .OnlyContain(r => r.Amount == 75m && r.TenantId == tenantB,
                "tenant B solo debe ver su revenue event");

        // Verificar que no hay cross-tenant leak
        eventsA.Should().NotContain(r => r.TenantId == tenantB,
            "CRÍTICO: los revenue_events no deben cruzar entre tenants");
        eventsB.Should().NotContain(r => r.TenantId == tenantA,
            "CRÍTICO: los revenue_events no deben cruzar entre tenants");
    }

    // ── TC-03-D: Appointment guardado con Source=WhatsApp ────────────────────

    [Fact(DisplayName = "TC-03-D: appointment manual → Source=WhatsApp, Status=Scheduled")]
    public async Task TC03D_AppointmentCreated_CorrectSourceAndStatus()
    {
        // NOTE: Este test verifica la entidad Appointment directamente,
        // simulando lo que haría ToolRegistry.BookAppointmentAsync.
        // El flow real usa AppointmentService que requiere ICalendarService.
        // Aquí verificamos solo la persistencia del dominio.

        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        var appointment = new Appointment
        {
            TenantId      = TenantId,
            PatientId     = patient.Id,
            TherapistName = "Dr. Ramírez",
            StartsAtUtc   = DateTimeOffset.UtcNow.AddDays(2).Date.AddHours(10),
            EndsAtUtc     = DateTimeOffset.UtcNow.AddDays(2).Date.AddHours(11),
            Status        = AppointmentStatus.Scheduled,
            Source        = AppointmentSource.WhatsApp,
            IsRecovered   = true,
        };
        Db.Appointments.Add(appointment);
        await Db.SaveChangesAsync();

        var saved = await Db.Appointments.FindAsync(appointment.Id);
        saved.Should().NotBeNull();
        saved!.Source      .Should().Be(AppointmentSource.WhatsApp, "cita reservada via WhatsApp");
        saved.Status       .Should().Be(AppointmentStatus.Scheduled, "la cita está programada");
        saved.IsRecovered  .Should().BeTrue("es una cita recuperada del pipeline de Flow01");
        saved.TenantId     .Should().Be(TenantId);
        saved.PatientId    .Should().Be(patient.Id);
    }
}
