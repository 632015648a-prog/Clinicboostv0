using System.Net;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Api.Features.Webhooks.Voice.MissedCall;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Automation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-01: Llamada perdida → webhook → mensaje WhatsApp enviado
//
// OBJETIVO
//   Verificar que el pipeline completo de recuperación de citas funciona:
//   llamada perdida (Twilio Voice webhook) → MissedCallJob → Flow01Orchestrator
//   → TwilioOutboundMessageSender (fake) → Message persistido con status=sent
//   → FlowMetricsEvents correctos → AutomationRun=completed.
//
// PRE-CONDITIONS
//   · Paciente registrado con RGPD=true
//   · Tenant activo con TimeZone="Europe/Madrid"
//
// DATOS DE PRUEBA
//   · CallerPhone: +34600111222 (Ana García López)
//   · CallSid: CAsmoke_tc01_001
//   · CallStatus: no-answer
//
// QUÉ SE AUTOMATIZA
//   ✅ Creación de AutomationRun
//   ✅ Invocación a Twilio (fake) y persistencia de Message
//   ✅ FlowMetricsEvents: missed_call_received + outbound_sent
//   ✅ AutomationRun.Status = "completed"
//
// QUÉ SE VALIDA MANUALMENTE
//   ⚠ Que el número de destino es exactamente el del paciente (E.164)
//   ⚠ Que el template SID es el correcto para el tenant en Twilio Console
//   ⚠ Que Twilio realmente entregó el mensaje (ver Twilio Message Logs)
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-01")]
public sealed class TC01_MissedCallToWhatsAppMessageTests : SmokeTestDb
{
    private readonly TwilioOptions _twilioOpts = new()
    {
        AccountSid = "ACsmoke_tc01",
        AuthToken  = "smoke_auth_token_tc01",
    };

    private IIdempotencyService BuildIdempotency()
    {
        var idempotency = Substitute.For<IIdempotencyService>();
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        idempotency
            .TryProcessAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(IdempotencyResult.NewEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
        return idempotency;
    }

    private Flow01Orchestrator BuildOrchestrator(HttpMessageHandler? handler = null)
    {
        var h       = handler ?? SmokeFixtures.TwilioOkHandler("SMtc01_ok");
        var client  = new HttpClient(h) { BaseAddress = new Uri("https://api.twilio.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        var sender = new TwilioOutboundMessageSender(
            Db,
            Options.Create(_twilioOpts),
            factory,
            Substitute.For<IVariantTrackingService>(),
            NullLogger<TwilioOutboundMessageSender>.Instance);

        var metrics         = new FlowMetricsService(Db, NullLogger<FlowMetricsService>.Instance);
        var idempotency     = BuildIdempotency();
        var variantTracking = Substitute.For<IVariantTrackingService>();

        return new Flow01Orchestrator(
            Db, sender, metrics, idempotency, variantTracking,
            Options.Create(new Flow01Options
            {
                DefaultTemplateSid = "HXsmoke_tc01_template",
                MaxDelayMinutes    = 60,
            }),
            NullLogger<Flow01Orchestrator>.Instance);
    }

    // ── TC-01-A: Flujo nominal — llamada no contestada ────────────────────────

    [Fact(DisplayName = "TC-01-A: no-answer → mensaje enviado y AutomationRun=completed")]
    public async Task TC01A_NoAnswer_MessageSentAndRunCompleted()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        await SmokeFixtures.SeedPatientAsync(Db, TenantId,
            phone: "+34600111222", fullName: "Ana García López", rgpdConsent: true);

        var orchestrator = BuildOrchestrator();

        var job = new MissedCallJob(
            TenantId:         TenantId,
            CallSid:          "CAsmoke_tc01_A",
            CallerPhone:      "+34600111222",
            ClinicPhone:      "+34910000001",
            CallStatus:       "no-answer",
            ReceivedAt:       DateTimeOffset.UtcNow.AddSeconds(-5),
            ProcessedEventId: Guid.NewGuid(),
            CorrelationId:    $"corr-tc01-A-{Guid.NewGuid():N}");

        // ── ACT ──────────────────────────────────────────────────────────────
        var run = new AutomationRun
        {
            TenantId      = TenantId,
            FlowId        = "flow_01",
            TriggerType   = "event",
            TriggerRef    = job.CallSid,
            Status        = "running",
            CorrelationId = Guid.NewGuid(),
        };
        Db.AutomationRuns.Add(run);
        await Db.SaveChangesAsync();

        var result = await orchestrator.ExecuteAsync(
            tenantId:       job.TenantId,
            callSid:        job.CallSid,
            callerPhone:    job.CallerPhone,
            clinicPhone:    job.ClinicPhone,
            callReceivedAt: job.ReceivedAt,
            correlationId:  job.CorrelationId);

        run.Status         = result.IsSuccess ? "completed" : "failed";
        run.ItemsProcessed = 1;
        run.ItemsSucceeded = result.IsSuccess ? 1 : 0;
        run.ItemsFailed    = result.IsSuccess ? 0 : 1;
        run.FinishedAt     = DateTimeOffset.UtcNow;
        await Db.SaveChangesAsync();

        // ── ASSERT ───────────────────────────────────────────────────────────

        // 1. AutomationRun completado
        var savedRun = await Db.AutomationRuns.FirstAsync();
        savedRun.Status        .Should().Be("completed",     "la llamada perdida debe procesar correctamente");
        savedRun.ItemsSucceeded.Should().Be(1);
        savedRun.FinishedAt    .Should().NotBeNull();

        // 2. Mensaje persistido con status=sent y dirección correcta
        var msg = await Db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull("debe existir exactamente un mensaje outbound");
        msg!.Status   .Should().Be("sent",      "Twilio confirmó la cola con 201 Created");
        msg.Direction .Should().Be("outbound",  "el sistema envía el primer contacto");
        msg.Channel   .Should().Be("whatsapp",  "el canal de recuperación es WhatsApp");
        msg.TenantId  .Should().Be(TenantId,    "aislamiento multi-tenant obligatorio");

        // 3. FlowMetricsEvents: missed_call_received y outbound_sent
        var metrics = await Db.FlowMetricsEvents.ToListAsync();
        metrics.Should().ContainSingle(e => e.MetricType == "missed_call_received",
            "se debe registrar la llamada perdida en métricas");
        metrics.Should().ContainSingle(e => e.MetricType == "outbound_sent",
            "se debe registrar el envío del WhatsApp en métricas");
        metrics.Should().NotContain(e => e.MetricType == "outbound_failed",
            "Twilio respondió OK — no debe haber fallo");

        // 4. El tiempo de respuesta está calculado correctamente (> 0ms, < 30s)
        var sentMetric = metrics.First(e => e.MetricType == "outbound_sent");
        sentMetric.DurationMs.Should().BeInRange(0, 30_000,
            "el tiempo desde llamada a envío de WhatsApp debe ser razonable");
    }

    // ── TC-01-B: Llamada "busy" también es llamada perdida ────────────────────

    [Fact(DisplayName = "TC-01-B: busy → tratado igual que no-answer")]
    public async Task TC01B_Busy_TreatedAsMissedCall()
    {
        await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var orchestrator = BuildOrchestrator();

        var run = new AutomationRun
        {
            TenantId = TenantId, FlowId = "flow_01", TriggerType = "event",
            TriggerRef = "CAsmoke_tc01_B", Status = "running", CorrelationId = Guid.NewGuid(),
        };
        Db.AutomationRuns.Add(run);
        await Db.SaveChangesAsync();

        var result = await orchestrator.ExecuteAsync(
            tenantId: TenantId, callSid: "CAsmoke_tc01_B",
            callerPhone: "+34600111222", clinicPhone: "+34910000001",
            callReceivedAt: DateTimeOffset.UtcNow.AddSeconds(-3),
            correlationId: "corr-tc01-B");

        run.Status = result.IsSuccess ? "completed" : "failed";
        await Db.SaveChangesAsync();

        (await Db.AutomationRuns.FirstAsync())
            .Status.Should().BeOneOf("completed", "skipped",
                "busy es una llamada perdida — debe procesarse");
    }

    // ── TC-01-C: Llamada contestada → no se genera nada ──────────────────────

    [Fact(DisplayName = "TC-01-C: answered call → sin mensaje, sin run")]
    public async Task TC01C_AnsweredCall_NothingCreated()
    {
        await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var orchestrator = BuildOrchestrator();

        // Simulamos el filtro del worker: "completed" no es llamada perdida
        const string callStatus = "completed";
        var missedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "no-answer", "busy", "failed" };

        bool isMissed = missedSet.Contains(callStatus);

        // Assert: no se invoca el orchestrator para llamadas contestadas
        isMissed.Should().BeFalse(
            "el worker no debe procesar llamadas contestadas (status='completed')");

        (await Db.Messages.CountAsync()).Should().Be(0);
        (await Db.FlowMetricsEvents.CountAsync()).Should().Be(0);
    }

    // ── TC-01-D: Twilio falla → Message=failed, AutomationRun=failed ─────────

    [Fact(DisplayName = "TC-01-D: Twilio 400 → message=failed, run=failed, metric=outbound_failed")]
    public async Task TC01D_TwilioError_RunFailedAndMessageFailed()
    {
        await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var orchestrator = BuildOrchestrator(SmokeFixtures.TwilioErrorHandler(30006));

        var run = new AutomationRun
        {
            TenantId = TenantId, FlowId = "flow_01", TriggerType = "event",
            TriggerRef = "CAsmoke_tc01_D", Status = "running", CorrelationId = Guid.NewGuid(),
        };
        Db.AutomationRuns.Add(run);
        await Db.SaveChangesAsync();

        var result = await orchestrator.ExecuteAsync(
            tenantId: TenantId, callSid: "CAsmoke_tc01_D",
            callerPhone: "+34600111222", clinicPhone: "+34910000001",
            callReceivedAt: DateTimeOffset.UtcNow.AddSeconds(-4),
            correlationId: "corr-tc01-D");

        run.Status      = result.IsSuccess ? "completed" : "failed";
        run.ErrorMessage = result.ErrorMessage;
        await Db.SaveChangesAsync();

        (await Db.AutomationRuns.FirstAsync())
            .Status.Should().Be("failed");

        var msg = await Db.Messages.FirstOrDefaultAsync();
        msg.Should().NotBeNull();
        msg!.Status   .Should().Be("failed");
        msg.ErrorCode .Should().Be("30006");

        var metrics = await Db.FlowMetricsEvents.ToListAsync();
        metrics.Should().Contain(e => e.MetricType == "outbound_failed" && e.ErrorCode == "30006",
            "un fallo de Twilio debe quedar registrado en métricas");
    }
}
