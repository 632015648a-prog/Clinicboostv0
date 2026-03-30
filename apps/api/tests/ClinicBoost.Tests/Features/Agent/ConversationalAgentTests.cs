using System.Net;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Conversations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// ConversationalAgentTests
//
// Tests de integración del ConversationalAgent con:
//   · IntentClassifier mockeado (FakeHttpMessageHandler)
//   · ToolRegistry sobre EF InMemory
//   · HardLimitGuard real
//   · SystemPromptBuilder real
//
// ESCENARIOS CUBIERTOS
// ─────────────────────
//   · Flujo feliz: mensaje → intención → respuesta de texto
//   · Derivación directa para Complaint/EscalateToHuman sin llamar al LLM
//   · Derivación por confianza baja
//   · Bloqueo de [HL-1]: confirmación directa de reserva
//   · Bloqueo de [HL-2]: descuento excesivo
//   · Escalación desde tool escalate_to_human
//   · Persistencia de AgentTurn en BD
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationalAgentTests
{
    // ── Setup helpers ─────────────────────────────────────────────────────────

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("AgentTests_" + Guid.NewGuid().ToString("N"))
            .Options);

    /// <summary>
    /// Construye un agente con IntentClassifier y ConversationalAgent usando el mismo
    /// FakeHttpMessageHandler para ambas llamadas HTTP a OpenAI.
    /// </summary>
    private static ConversationalAgent BuildAgent(
        AppDbContext db,
        string       classifyJson,
        string       mainResponseJson)
    {
        // El handler sirve las respuestas en orden: 1ª llamada = clasificación, 2ª = respuesta principal
        var handler     = new SequentialFakeHandler([classifyJson, mainResponseJson]);
        var client      = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("OpenAI").Returns(client);

        var classifier = new IntentClassifier(httpFactory, NullLogger<IntentClassifier>.Instance);
        var prompt     = new SystemPromptBuilder();
        var tools      = new ToolRegistry(db, NullLogger<ToolRegistry>.Instance);
        var guard      = new HardLimitGuard(NullLogger<HardLimitGuard>.Instance);

        return new ConversationalAgent(
            classifier, prompt, tools, guard, db, httpFactory,
            NullLogger<ConversationalAgent>.Instance);
    }

    private static AgentContext BuildCtx(
        string  text            = "Quiero pedir una cita para mañana",
        decimal discountMaxPct  = 0m,
        bool    sessionActive   = true) => new()
    {
        TenantId              = Guid.NewGuid(),
        PatientId             = Guid.NewGuid(),
        ConversationId        = Guid.NewGuid(),
        CorrelationId         = "corr-test",
        MessageSid            = "SMagent001",
        InboundText           = text,
        PatientName           = "Pedro Sánchez",
        PatientPhone          = "+34600000002",
        RgpdConsent           = true,
        ConversationStatus    = "open",
        AiContextJson         = "{}",
        IsInsideSessionWindow = sessionActive,
        RecentMessages        = Array.Empty<Message>(),
        DiscountMaxPct        = discountMaxPct,
        ClinicName            = "FisioTest",
        LanguageCode          = "es",
    };

    /// <summary>JSON de respuesta del clasificador de intención (gpt-4o-mini).</summary>
    private static string ClassifyResponse(
        string intent     = "BookAppointment",
        double confidence = 0.90) =>
        OpenAiTextResponse($"{{\"intent\":\"{intent}\",\"confidence\":{confidence},\"reasoning\":\"test\"}}");

    /// <summary>JSON de respuesta de chat sin tool calls (respuesta de texto).</summary>
    private static string MainTextResponse(string text = "Perfecto, voy a buscarte un hueco.") =>
        OpenAiTextResponse(text);

    private static string OpenAiTextResponse(string content) =>
        $$"""
        {
          "choices": [{ "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(content)}} } }],
          "usage": { "prompt_tokens": 150, "completion_tokens": 60 }
        }
        """;

    /// <summary>JSON de respuesta con tool call a escalate_to_human.</summary>
    private static string EscalateToolResponse(string reason = "Queja del paciente") =>
        $$"""
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_001",
                "type": "function",
                "function": {
                  "name": "escalate_to_human",
                  "arguments": "{\"reason\":\"{{reason}}\"}"
                }
              }]
            }
          }],
          "usage": { "prompt_tokens": 200, "completion_tokens": 30 }
        }
        """;

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Flujo feliz
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleAsync_ReturnsSendMessage_ForBookAppointmentWithTextResponse()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db, ClassifyResponse("BookAppointment"), MainTextResponse());

        var result = await sut.HandleAsync(BuildCtx());

        result.Action.Should().Be(AgentAction.SendMessage);
        result.ResponseText.Should().NotBeNullOrWhiteSpace();
        result.WasBlocked.Should().BeFalse();
        result.Intent.Intent.Should().Be(Intent.BookAppointment);
    }

    [Fact]
    public async Task HandleAsync_PersistsAgentTurn_InDatabase()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db, ClassifyResponse("GeneralInquiry"), MainTextResponse());
        var       ctx = BuildCtx();

        await sut.HandleAsync(ctx);

        var turn = await db.AgentTurns
            .FirstOrDefaultAsync(t => t.TenantId == ctx.TenantId);

        turn.Should().NotBeNull("se debe persistir el turno en agent_turns");
        turn!.IntentName.Should().Be("GeneralInquiry");
        turn.ActionName.Should().Be("SendMessage");
        turn.CorrelationId.Should().Be("corr-test");
    }

    [Fact]
    public async Task HandleAsync_UpdatesAiContextJson_AfterTurn()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db, ClassifyResponse("QueryAppointment"), MainTextResponse());

        var result = await sut.HandleAsync(BuildCtx());

        result.UpdatedAiContextJson.Should().Contain("last_intent",
            "el AiContext debe actualizarse con la intención del turno");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Derivación directa sin LLM
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Complaint")]
    [InlineData("EscalateToHuman")]
    public async Task HandleAsync_EscalatesDirectly_WithoutCallingMainLlm(string intentName)
    {
        using var db  = CreateDb();
        // Solo necesitamos la respuesta del clasificador; el LLM principal no se llamará
        var       sut = BuildAgent(db, ClassifyResponse(intentName, 0.9), MainTextResponse());

        var result = await sut.HandleAsync(BuildCtx());

        result.Action.Should().Be(AgentAction.EscalateToHuman);
        result.ModelUsed.Should().Be("none",
            "no se debe llamar al LLM principal para Complaint/EscalateToHuman");
    }

    [Fact]
    public async Task HandleAsync_EscalatesDirectly_WhenConfidenceLow()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db, ClassifyResponse("BookAppointment", 0.4), MainTextResponse());

        var result = await sut.HandleAsync(BuildCtx());

        result.Action.Should().Be(AgentAction.EscalateToHuman,
            "confianza baja debe derivar sin llamar al LLM");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Escalación desde tool call
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleAsync_EscalatesViaToolCall_WhenEscalateToolIsCalled()
    {
        using var db  = CreateDb();
        // El clasificador dice BookAppointment (alta confianza)
        // El LLM principal devuelve una tool call a escalate_to_human
        var sut = BuildAgent(db,
            ClassifyResponse("BookAppointment", 0.9),
            EscalateToolResponse("Paciente insiste en hablar con persona"));

        var result = await sut.HandleAsync(BuildCtx());

        result.Action.Should().Be(AgentAction.EscalateToHuman);
        result.EscalationReason.Should().NotBeNullOrWhiteSpace();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: HardLimitGuard integrado
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleAsync_GuardBlocks_DirectBookingConfirmation()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db,
            ClassifyResponse("BookAppointment", 0.9),
            MainTextResponse("He reservado tu cita para mañana a las 10h."));

        var result = await sut.HandleAsync(BuildCtx());

        result.WasBlocked.Should().BeTrue("[HL-1] confirmación directa debe ser bloqueada");
        result.Action.Should().Be(AgentAction.EscalateToHuman);
        result.BlockReason.Should().Contain("HL-1");
    }

    [Fact]
    public async Task HandleAsync_GuardBlocks_ExcessiveDiscount()
    {
        using var db  = CreateDb();
        var       ctx = BuildCtx(discountMaxPct: 10m);
        var       sut = BuildAgent(db,
            ClassifyResponse("DiscountRequest", 0.9),
            MainTextResponse("Te ofrezco un 30% de descuento en tu primera sesión."));

        var result = await sut.HandleAsync(ctx);

        result.WasBlocked.Should().BeTrue("[HL-2] 30% supera el límite de 10%");
        result.BlockReason.Should().Contain("HL-2");
    }

    [Fact]
    public async Task HandleAsync_GuardBlocks_TextMessageWhenSessionExpired()
    {
        using var db  = CreateDb();
        var       ctx = BuildCtx(sessionActive: false);
        var       sut = BuildAgent(db,
            ClassifyResponse("GeneralInquiry", 0.9),
            MainTextResponse("¿En qué puedo ayudarte hoy?"));

        var result = await sut.HandleAsync(ctx);

        result.WasBlocked.Should().BeTrue("[HL-5] sesión expirada no permite texto libre");
        result.BlockReason.Should().Contain("HL-5");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 5: Tokens y metadatos del turno
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HandleAsync_RecordsTokenUsage_InResult()
    {
        using var db  = CreateDb();
        var       sut = BuildAgent(db, ClassifyResponse("GeneralInquiry"), MainTextResponse());

        var result = await sut.HandleAsync(BuildCtx());

        result.PromptTokens.Should().BeGreaterThan(0,
            "se deben registrar los tokens del prompt");
        result.CompletionTokens.Should().BeGreaterThan(0,
            "se deben registrar los tokens de completion");
        result.ModelUsed.Should().Be("gpt-4o");
    }
}

// ── SequentialFakeHandler ─────────────────────────────────────────────────────

/// <summary>
/// Handler HTTP que devuelve respuestas en orden: primera llamada → responses[0], etc.
/// Cuando se agota la lista, repite la última respuesta.
/// </summary>
internal sealed class SequentialFakeHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<string> _responses;
    private int _callCount;

    public SequentialFakeHandler(IReadOnlyList<string> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var idx  = Math.Min(_callCount, _responses.Count - 1);
        _callCount++;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses[idx], Encoding.UTF8, "application/json"),
        });
    }
}
