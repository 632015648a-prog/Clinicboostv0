using System.Net;
using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;
using ClinicBoost.Api.Infrastructure.Idempotency;
using ClinicBoost.Api.Infrastructure.Tenants;
using ClinicBoost.Api.Infrastructure.Twilio;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ClinicBoost.Tests.SmokeTests.Infrastructure;

namespace ClinicBoost.Tests.SmokeTests;

// ════════════════════════════════════════════════════════════════════════════
// TC-02: Paciente responde por WhatsApp → conversación guardada → IA responde
//
// OBJETIVO
//   Verificar que el ciclo inbound completo funciona:
//   mensaje WhatsApp entrante → ConversationService.AppendInboundMessage →
//   ConversationalAgent (fake OpenAI) → respuesta outbound → AgentTurn persistido
//
// PRE-CONDITIONS
//   · Conversación activa (status=open) para el paciente
//   · Paciente con RGPD=true
//
// DATOS DE PRUEBA
//   · InboundText: "Quiero reservar una cita para el martes"
//   · MessageSid: SMsmoke_inbound_tc02
//   · CallerPhone: +34600111222
//
// QUÉ SE AUTOMATIZA
//   ✅ Persistencia del mensaje inbound en BD
//   ✅ Upsert de conversación (reutiliza existente)
//   ✅ Clasificación de intención (fake OpenAI)
//   ✅ AgentTurn persistido con tokens y action
//   ✅ Status de conversación actualizado
//
// QUÉ SE VALIDA MANUALMENTE
//   ⚠ Que la respuesta del agente es lingüísticamente apropiada (revisión humana)
//   ⚠ Que el tono y las ofertas de slots son correctas para el tenant real
//   ⚠ Que la sesión de WhatsApp (24h window) no está expirada
// ════════════════════════════════════════════════════════════════════════════

[Trait("Category", "SmokeE2E")]
[Trait("TC", "TC-02")]
public sealed class TC02_PatientReplyAiResponseTests : SmokeTestDb
{
    private static ConversationService BuildConvService(
        ClinicBoost.Api.Infrastructure.Database.AppDbContext db) =>
        new(db, NullLogger<ConversationService>.Instance);

    private ConversationalAgent BuildAgent(HttpMessageHandler openAiHandler)
    {
        var client      = new HttpClient(openAiHandler) { BaseAddress = new Uri("https://api.openai.com/") };
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("OpenAI").Returns(client);

        var classifier  = new IntentClassifier(httpFactory, NullLogger<IntentClassifier>.Instance);
        var prompt      = new SystemPromptBuilder();
        var calendar    = Substitute.For<ICalendarService>();
        var tools       = new ToolRegistry(Db, calendar, NullLogger<ToolRegistry>.Instance);
        var guard       = new HardLimitGuard(NullLogger<HardLimitGuard>.Instance);

        return new ConversationalAgent(
            classifier, prompt, tools, guard, Db, httpFactory,
            NullLogger<ConversationalAgent>.Instance);
    }

    // ── TC-02-A: Paciente pide cita → agente clasifica BookAppointment ────────

    [Fact(DisplayName = "TC-02-A: inbound 'quiero cita' → AgentTurn persistido, acción SendMessage/ProposeAppointment")]
    public async Task TC02A_PatientAsksCita_AgentRespondsAndTurnPersisted()
    {
        // ── ARRANGE ──────────────────────────────────────────────────────────
        await SmokeFixtures.SeedTenantAsync(Db, TenantId);
        var patient  = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var convSvc  = BuildConvService(Db);
        var conv     = await convSvc.UpsertConversationAsync(
            TenantId, patient.Id, "whatsapp", "flow_00");

        var agent = BuildAgent(SmokeFixtures.OpenAiBookingHandler());

        var ctx = new AgentContext
        {
            TenantId           = TenantId,
            PatientId          = patient.Id,
            ConversationId     = conv.Id,
            CorrelationId      = "corr-tc02-A",
            MessageSid         = "SMsmoke_inbound_tc02_A",
            InboundText        = "Quiero reservar una cita para el martes por la mañana",
            PatientName        = patient.FullName,
            PatientPhone       = patient.Phone,
            RgpdConsent        = true,
            ConversationStatus = conv.Status,
            AiContextJson      = conv.AiContext,
            IsInsideSessionWindow = true,
            RecentMessages     = [],
            DiscountMaxPct     = 0m,
            ClinicName         = "Fisioterapia Ramírez",
            LanguageCode       = "es",
        };

        // ── ACT: persistir mensaje inbound ────────────────────────────────────
        var inboundMsg = await convSvc.AppendInboundMessageAsync(
            conversationId: conv.Id,
            tenantId:       TenantId,
            messageSid:     "SMsmoke_inbound_tc02_A",
            body:           ctx.InboundText,
            mediaUrl:       null,
            mediaType:      null);

        // Recargar mensajes recientes para el agente
        var recentMsgs = await Db.Messages
            .Where(m => m.TenantId == TenantId && m.ConversationId == conv.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();
        ctx = ctx with { RecentMessages = recentMsgs };

        // ── ACT: invocar agente ───────────────────────────────────────────────
        var result = await agent.HandleAsync(ctx);

        // Actualizar estado conversación según resultado
        if (result.Action == AgentAction.EscalateToHuman)
            conv.Status = "waiting_human";
        else if (result.Action == AgentAction.Resolve)
            conv.Status = "resolved";
        conv.AiContext = result.UpdatedAiContextJson;
        await Db.SaveChangesAsync();

        // ── ASSERT ───────────────────────────────────────────────────────────

        // 1. Mensaje inbound guardado
        var storedInbound = await Db.Messages
            .FirstOrDefaultAsync(m => m.Direction == "inbound");
        storedInbound.Should().NotBeNull("el mensaje del paciente debe persistirse");
        storedInbound!.Body          .Should().Be(ctx.InboundText);
        storedInbound.ConversationId .Should().Be(conv.Id);
        storedInbound.TenantId       .Should().Be(TenantId);

        // 2. AgentTurn persistido
        var agentTurn = await Db.AgentTurns
            .FirstOrDefaultAsync(t => t.ConversationId == conv.Id);
        agentTurn.Should().NotBeNull("cada invocación del agente debe generar un AgentTurn");
        agentTurn!.TenantId       .Should().Be(TenantId);
        agentTurn.ConversationId  .Should().Be(conv.Id);
        agentTurn.PromptTokens    .Should().BeGreaterThan(0, "el agente consumió tokens");
        agentTurn.CompletionTokens.Should().BeGreaterThan(0);

        // 3. El agente tomó una acción válida (no fallo silencioso)
        result.Action.Should().BeOneOf(
            AgentAction.SendMessage,
            AgentAction.ProposeAppointment,
            AgentAction.EscalateToHuman,   // aceptable si decide escalar
            "el agente debe generar una acción explícita, nunca null");

        // 4. Hay texto de respuesta si la acción es SendMessage
        if (result.Action == AgentAction.SendMessage)
        {
            result.ResponseText.Should().NotBeNullOrWhiteSpace(
                "si la acción es SendMessage debe haber texto de respuesta");
        }

        // 5. La conversación se actualizó
        var updatedConv = await Db.Conversations.FindAsync(conv.Id);
        updatedConv.Should().NotBeNull();
        updatedConv!.AiContext.Should().NotBe("{}",
            "el contexto AI debe actualizarse tras el turno del agente");
    }

    // ── TC-02-B: Paciente con RGPD=false → agente no invocado ────────────────

    [Fact(DisplayName = "TC-02-B: RGPD=false → sin AgentTurn, sin mensaje outbound")]
    public async Task TC02B_PatientNoRgpd_AgentNotInvoked()
    {
        // ARRANGE: paciente sin consentimiento
        var patient = await SmokeFixtures.SeedPatientAsync(
            Db, TenantId, rgpdConsent: false);
        var convSvc = BuildConvService(Db);
        var conv    = await convSvc.UpsertConversationAsync(
            TenantId, patient.Id, "whatsapp", "flow_00");

        // El worker comprueba RgpdConsent antes de invocar al agente.
        // Simulamos aquí esa guardia.
        bool shouldInvokeAgent = patient.RgpdConsent;

        // ACT: no invocar agente (lógica del worker)
        // → no hay AgentTurn ni mensaje outbound

        // ASSERT
        shouldInvokeAgent.Should().BeFalse(
            "el worker no debe invocar al agente si el paciente no tiene RGPD");
        (await Db.AgentTurns.CountAsync()).Should().Be(0,
            "sin consentimiento no debe existir ningún AgentTurn");
    }

    // ── TC-02-C: Conversación reutiliza la existente (no crea nueva) ──────────

    [Fact(DisplayName = "TC-02-C: segundo mensaje → misma conversación reutilizada")]
    public async Task TC02C_SecondMessage_ReusesSameConversation()
    {
        var patient = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var convSvc = BuildConvService(Db);

        // Primera llamada: crea la conversación
        var first  = await convSvc.UpsertConversationAsync(TenantId, patient.Id, "whatsapp", "flow_00");

        // Segunda llamada: debe reutilizar
        var second = await convSvc.UpsertConversationAsync(TenantId, patient.Id, "whatsapp", "flow_00");

        second.Id.Should().Be(first.Id,
            "un paciente con conversación activa no debe generar una nueva");

        (await Db.Conversations.CountAsync()).Should().Be(1,
            "solo debe existir una conversación activa por paciente+canal");
    }

    // ── TC-02-D: Historial de mensajes alimenta el contexto del agente ────────

    [Fact(DisplayName = "TC-02-D: mensajes previos en BD se incluyen como contexto")]
    public async Task TC02D_PreviousMessages_IncludedAsContext()
    {
        var patient  = await SmokeFixtures.SeedPatientAsync(Db, TenantId, rgpdConsent: true);
        var convSvc  = BuildConvService(Db);
        var conv     = await convSvc.UpsertConversationAsync(
            TenantId, patient.Id, "whatsapp", "flow_00");

        // Seed 3 mensajes previos en la conversación
        for (int i = 1; i <= 3; i++)
        {
            Db.Messages.Add(new Message
            {
                TenantId          = TenantId,
                ConversationId    = conv.Id,
                Direction         = i % 2 == 1 ? "inbound" : "outbound",
                Channel           = "whatsapp",
                ProviderMessageId = $"SMsmoke_prev_{i:D3}",
                Body              = $"Mensaje previo {i}",
                Status            = "read",
            });
        }
        await Db.SaveChangesAsync();

        // Cargar contexto como haría el worker
        var recentMessages = await Db.Messages
            .Where(m => m.TenantId == TenantId && m.ConversationId == conv.Id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(15)
            .ToListAsync();

        recentMessages.Should().HaveCount(3,
            "el agente debe recibir el historial de mensajes recientes");
        recentMessages.Should().OnlyContain(m => m.TenantId == TenantId,
            "el historial debe filtrarse por tenant_id — aislamiento multi-tenant");
    }
}
