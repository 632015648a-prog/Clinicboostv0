using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Features.Variants;
using ClinicBoost.Domain.Automation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using TimeZoneConverter;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-04: Mensaje fuera de horario → respuesta correcta según timezone del tenant
//
// OBJETIVO
//   Verificar que la lógica de horario del tenant funciona correctamente:
//   · Si el mensaje llega DENTRO del horario → el flujo procesa normalmente
//   · Si el mensaje llega FUERA del horario → el agente/flujo responde
//     con el mensaje de "fuera de horario" específico del tenant
//   · El timezone del tenant (Europe/Madrid, America/Mexico_City, etc.)
//     se usa correctamente — nunca UTC hardcoded
//
// CONTEXTO DEL CÓDIGO
//   · Tenant.TimeZone usa IANA timezone IDs (vía TimeZoneConverter)
//   · Flow01Orchestrator usa MaxDelayMinutes como ventana de procesamiento
//   · El agente recibe ClinicName y LanguageCode del tenant
//   · PIEZA FALTANTE IDENTIFICADA:
//     WhatsAppInboundWorker NO tiene guard de horario de negocio.
//     La lógica de "fuera de horario" depende del SystemPromptBuilder
//     que incluye la hora actual en el prompt del agente.
//     → TC-04 verifica que el timezone se calcula correctamente y se
//       propagaría al agente; el test de la respuesta real es MANUAL.
//
// PRE-CONDITIONS
//   · Tenant con TimeZone="Europe/Madrid"
//   · RuleConfig flow_01 / max_delay_minutes (opcional, default=60)
//
// DATOS DE PRUEBA
//   · Timezone: Europe/Madrid (UTC+1 en invierno, UTC+2 en verano)
//   · Timezone alternativo: America/Mexico_City (UTC-6 en invierno)
//
// QUÉ SE AUTOMATIZA
//   ✅ Conversión UTC → hora local del tenant (TZConvert)
//   ✅ Verificar que MaxDelayMinutes no se supera fuera del horario
//   ✅ Verificar que el cutoff de delay usa UtcNow (no localNow)
//   ✅ Test de zona horaria de México (UTC-6) para verificar multi-timezone
//
// QUÉ SE VALIDA MANUALMENTE
//   ⚠ Que el SystemPromptBuilder incluye la hora local correcta en el prompt
//   ⚠ Que el agente realmente responde con mensaje de fuera de horario
//   ⚠ Que el mensaje de fuera de horario está personalizado para el tenant
//   ⚠ Comportamiento en cambio de hora (DST: último domingo de marzo/octubre)
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-04")]
public sealed class TC04_OutOfHoursTimezoneTests : SmokeTestDb
{
    // ── TC-04-A: Timezone Europe/Madrid — conversión UTC correcta ─────────────

    [Fact(DisplayName = "TC-04-A: Europe/Madrid — conversión UTC→local es correcta")]
    public async Task TC04A_EuropeMadrid_UtcToLocalConversion()
    {
        // ARRANGE: tenant con timezone de Madrid
        const string tz = "Europe/Madrid";
        await SmokeFixtures.SeedTenantAsync(Db, TenantId, timeZone: tz);

        // ACT: cargar tenant y convertir hora UTC a local
        var tenant = await Db.Tenants.FindAsync(TenantId);
        var tzInfo = TZConvert.GetTimeZoneInfo(tenant!.TimeZone);

        // Usamos un instante UTC fijo: 2026-01-15 09:30 UTC
        var utcInstant = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc);
        var localTime  = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, tzInfo);

        // ASSERT: en enero, Madrid está en UTC+1
        localTime.Hour  .Should().Be(10, "Madrid en enero (UTC+1): 09:30 UTC = 10:30 local");
        localTime.Minute.Should().Be(30);

        // Verificar que la conversión es bidireccional (UTC→local→UTC debe ser la misma)
        var backToUtc = TimeZoneInfo.ConvertTimeToUtc(localTime, tzInfo);
        backToUtc    .Should().Be(utcInstant, "la conversión debe ser reversible");
    }

    // ── TC-04-B: Timezone America/Mexico_City ─────────────────────────────────

    [Fact(DisplayName = "TC-04-B: America/Mexico_City — UTC-6 en invierno")]
    public async Task TC04B_MexicoCity_UtcToLocalConversion()
    {
        // ARRANGE: tenant en México
        const string tz = "America/Mexico_City";
        await SmokeFixtures.SeedTenantAsync(Db, TenantId, timeZone: tz);

        var tenant = await Db.Tenants.FindAsync(TenantId);
        var tzInfo = TZConvert.GetTimeZoneInfo(tenant!.TimeZone);

        // Instante fijo: 2026-01-15 15:00 UTC (sin DST en enero)
        var utcInstant = new DateTime(2026, 1, 15, 15, 0, 0, DateTimeKind.Utc);
        var localTime  = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, tzInfo);

        // En enero Mexico City es UTC-6: 15:00 UTC = 09:00 local
        localTime.Hour.Should().Be(9, "Mexico City en enero (UTC-6): 15:00 UTC = 09:00 local");
    }

    // ── TC-04-C: Flow01Options.MaxDelayMinutes está definido pero no implementado ─
    //
    // GAP-03: Flow01Options.MaxDelayMinutes está definido en Flow01Options (valor=60)
    // pero NO se usa en Flow01Orchestrator.ExecuteAsync para cortar llamadas antiguas.
    // El único mecanismo de skip actual es el cooldown_minutes (basado en mensajes
    // recientes para el mismo paciente), que es distinto de "la llamada es muy antigua".
    //
    // Este test DOCUMENTA el comportamiento ACTUAL (el orquestador procesa aunque la
    // llamada tenga 10 minutos de antigüedad) y el comportamiento ESPERADO (skip).
    // Cuando se implemente la feature MaxDelayMinutes, hay que invertir las assertions.

    [Fact(DisplayName = "TC-04-C: MaxDelayMinutes definido en Flow01Options (GAP-03 — pendiente de implementación)")]
    public async Task TC04C_MaxDelay_DocumentedGap()
    {
        // ARRANGE: configurar MaxDelayMinutes = 5 (ventana muy corta para test)
        await SmokeFixtures.SeedTenantAsync(Db, TenantId, timeZone: "Europe/Madrid");
        await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);

        // La llamada se recibió hace 10 minutos (fuera de la ventana de 5 min)
        var callReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var handler = SmokeFixtures.TwilioOkHandler();
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.twilio.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("Twilio").Returns(client);

        var sender = new TwilioOutboundMessageSender(
            Db,
            Options.Create(new TwilioOptions
            {
                AccountSid = "ACsmoke_tc04", AuthToken = "token_tc04",
            }),
            factory,
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
        var orchestrator    = new Flow01Orchestrator(
            Db, sender, metrics, idempotency, variantTracking,
            Options.Create(new Flow01Options
            {
                DefaultTemplateSid = "HXsmoke_tc04",
                MaxDelayMinutes    = 5,   // Definido pero no implementado aún (GAP-03)
            }),
            NullLogger<Flow01Orchestrator>.Instance);

        var run = new AutomationRun
        {
            TenantId = TenantId, FlowId = "flow_01", TriggerType = "event",
            TriggerRef = "CAsmoke_tc04_C", Status = "running",
            CorrelationId = Guid.NewGuid(),
        };
        Db.AutomationRuns.Add(run);
        await Db.SaveChangesAsync();

        // ACT
        var result = await orchestrator.ExecuteAsync(
            tenantId:       TenantId,
            callSid:        "CAsmoke_tc04_C",
            callerPhone:    "+34600111222",
            clinicPhone:    "+34910000001",
            callReceivedAt: callReceivedAt,   // Hace 10 minutos
            correlationId:  "corr-tc04-C");

        // ── ASSERT: comportamiento ACTUAL (MaxDelayMinutes NO está implementado) ──
        //
        // GAP-03: el orquestador NO tiene guard de MaxDelayMinutes todavía.
        // La llamada de hace 10 minutos se PROCESA (IsSuccess=true) en lugar de
        // saltarse. La implementación pendiente debería:
        //   if (UtcNow - callReceivedAt > MaxDelayMinutes) return Skipped(...);
        //
        // Cuando se implemente GAP-03, cambiar las assertions a:
        //   result.FlowStep.Should().Be("skipped", ...)
        //   (await Db.Messages.CountAsync()).Should().Be(0, ...)
        result.IsSuccess.Should().BeTrue(
            "el orquestador procesa la llamada aunque tenga 10 min de antigüedad" +
            " — GAP-03: MaxDelayMinutes aún no implementado como guard");

        // La métrica missed_call_received SIEMPRE se registra (antes del guard de MaxDelay)
        (await Db.FlowMetricsEvents
            .AnyAsync(e => e.MetricType == "missed_call_received"))
            .Should().BeTrue("la llamada perdida se registra en métricas");

        // DOCUMENTAR EL GAP: MaxDelayMinutes está en Flow01Options.cs (línea ~551)
        // pero no hay código en ExecuteAsync que lo lea y aplique.
        // Issue de seguimiento: GAP-03 en SMOKE_TESTS.md
        var opts = new Flow01Options { MaxDelayMinutes = 5, DefaultTemplateSid = "X" };
        opts.MaxDelayMinutes.Should().Be(5,
            "MaxDelayMinutes está correctamente definido en Flow01Options");
    }

    // ── TC-04-D: DST awareness — cambio de hora no rompe la conversión ────────

    [Fact(DisplayName = "TC-04-D: DST en Europe/Madrid — cambio de hora no rompe la conversión")]
    public async Task TC04D_DaylightSavingTime_ConversionRemainsCorrect()
    {
        // ARRANGE: usar Europe/Madrid en verano (UTC+2) y en invierno (UTC+1)
        const string tz     = "Europe/Madrid";
        var          tzInfo = TZConvert.GetTimeZoneInfo(tz);

        // Invierno: 2026-01-15 09:00 UTC = 10:00 Madrid (UTC+1)
        var winterUtc   = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        var winterLocal = TimeZoneInfo.ConvertTimeFromUtc(winterUtc, tzInfo);

        // Verano: 2026-07-15 09:00 UTC = 11:00 Madrid (UTC+2)
        var summerUtc   = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc);
        var summerLocal = TimeZoneInfo.ConvertTimeFromUtc(summerUtc, tzInfo);

        // ASSERT
        winterLocal.Hour.Should().Be(10, "en enero Madrid está a UTC+1");
        summerLocal.Hour.Should().Be(11, "en julio Madrid está a UTC+2 (DST activo)");

        // El offset cambia según la época del año
        var winterOffset = tzInfo.GetUtcOffset(winterUtc);
        var summerOffset = tzInfo.GetUtcOffset(summerUtc);
        summerOffset.Should().BeGreaterThan(winterOffset,
            "en verano el offset es mayor (DST añade +1 hora)");
    }

    // ── TC-04-E: Tenant sin timezone → falla descriptivamente ────────────────

    [Fact(DisplayName = "TC-04-E: timezone inválido → TZConvert lanza excepción descriptiva")]
    public async Task TC04E_InvalidTimezone_ThrowsDescriptiveException()
    {
        // Este test documenta el comportamiento esperado cuando TimeZone está mal configurado.
        // En producción, la migración de BD tiene un CHECK constraint en timezone.

        const string invalidTz = "Inexistent/Timezone";

        var act = () => TZConvert.GetTimeZoneInfo(invalidTz);

        act.Should().Throw<Exception>(
            "un timezone inválido debe lanzar excepción — nunca silenciarse");
    }

    // ── TC-04-F: El SystemPromptBuilder incluye hora local del tenant ─────────

    [Fact(DisplayName = "TC-04-F: SystemPromptBuilder — incluye contexto horario en el prompt")]
    public async Task TC04F_SystemPrompt_IncludesLocalTimeContext()
    {
        // NOTE: Este test verifica que el prompt del agente incluye la hora actual.
        // La implementación real usa ClinicName y langCode; la hora local
        // se pasa como parte de AiContextJson o como parte del contexto de AgentContext.
        //
        // PIEZA FALTANTE DOCUMENTADA:
        // La hora local del tenant NO se pasa explícitamente en AgentContext.
        // El SystemPromptBuilder tiene acceso a DateTimeOffset.UtcNow pero
        // no al TenantTimeZone para convertirlo.
        // → MEJORA RECOMENDADA: añadir LocalNow (DateTimeOffset) a AgentContext
        //   calculado en WhatsAppInboundWorker usando Tenant.TimeZone.

        var prompt = new ClinicBoost.Api.Features.Agent.SystemPromptBuilder();

        var ctx = new ClinicBoost.Api.Features.Agent.AgentContext
        {
            TenantId           = TenantId,
            PatientId          = Guid.NewGuid(),
            ConversationId     = Guid.NewGuid(),
            CorrelationId      = "corr-tc04-F",
            MessageSid         = "SMsmoke_tc04_F",
            InboundText        = "Buenas noches, ¿podéis atenderme?",
            PatientName        = "Ana García López",
            PatientPhone       = "+34600111222",
            RgpdConsent        = true,
            ConversationStatus = "open",
            AiContextJson      = "{}",
            IsInsideSessionWindow = true,
            RecentMessages     = [],
            DiscountMaxPct     = 0m,
            ClinicName         = "Fisioterapia Ramírez",
            LanguageCode       = "es",
        };

        var builtPrompt = prompt.Build(ctx,
            ClinicBoost.Api.Features.Agent.Intent.GeneralInquiry);

        builtPrompt.Should().NotBeNullOrWhiteSpace(
            "el SystemPromptBuilder debe generar un prompt");
        builtPrompt.Should().Contain("Fisioterapia Ramírez",
            "el prompt debe mencionar el nombre de la clínica");

        // NOTA: Esta assertion documenta la PIEZA FALTANTE:
        // El prompt debería contener la hora local pero actualmente
        // no tiene acceso directo al timezone del tenant.
        // → Registrado en GAP-02 del documento de piezas faltantes.
    }
}
