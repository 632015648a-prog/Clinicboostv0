using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClinicBoost.Api.Infrastructure.Database;

namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// ConversationalAgent
//
// Implementación de IConversationalAgent.
//
// CICLO DE EJECUCIÓN POR MENSAJE
// ──────────────────────────────
//  1. Clasificar intención con IntentClassifier (gpt-4o-mini, json_object).
//  2. Construir prompt de sistema con SystemPromptBuilder.
//  3. Construir historial de mensajes (system + historial reciente + inbound).
//  4. Llamar a OpenAI Chat Completions con tools activas.
//  5. Si la respuesta contiene tool_calls:
//       a. Ejecutar cada tool con ToolRegistry.
//       b. Si alguna tool es escalate_to_human → salir del loop y escalar.
//       c. Si alguna es propose_appointment → guardar proposal y continuar.
//       d. Añadir resultados al historial y volver a llamar a OpenAI.
//       e. Máximo MAX_TOOL_ROUNDS rondas para evitar loops infinitos.
//  6. Extraer ResponseText de la última respuesta del asistente.
//  7. Pasar el resultado por HardLimitGuard.
//  8. Persistir AgentTurn en BD.
//  9. Devolver AgentResult al worker.
//
// MODELO
// ──────
// · Clasificación : gpt-4o-mini  (barato, rápido, json_object)
// · Razonamiento  : gpt-4o        (tool calling + respuesta final)
//
// GESTIÓN DE ERRORES
// ──────────────────
// · Si OpenAI falla en cualquier ronda → EscalateToHuman.
// · Si JSON es inválido → EscalateToHuman.
// · Si se alcanzan MAX_TOOL_ROUNDS → EscalateToHuman.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Orquestador del agente conversacional.
/// Registrar como <b>Scoped</b>.
/// </summary>
public sealed class ConversationalAgent : IConversationalAgent
{
    private const string MainModel      = "gpt-4o";
    private const int    MaxToolRounds  = 5;

    private readonly IntentClassifier              _classifier;
    private readonly SystemPromptBuilder           _promptBuilder;
    private readonly ToolRegistry                  _tools;
    private readonly HardLimitGuard                _guard;
    private readonly AppDbContext                  _db;
    private readonly IHttpClientFactory            _httpFactory;
    private readonly ILogger<ConversationalAgent>  _logger;

    public ConversationalAgent(
        IntentClassifier              classifier,
        SystemPromptBuilder           promptBuilder,
        ToolRegistry                  tools,
        HardLimitGuard                guard,
        AppDbContext                  db,
        IHttpClientFactory            httpFactory,
        ILogger<ConversationalAgent>  logger)
    {
        _classifier    = classifier;
        _promptBuilder = promptBuilder;
        _tools         = tools;
        _guard         = guard;
        _db            = db;
        _httpFactory   = httpFactory;
        _logger        = logger;
    }

    // ── Punto de entrada ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AgentResult> HandleAsync(
        AgentContext      context,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Agent] Iniciando turno. MessageSid={Sid} ConvId={ConvId} TenantId={TenantId}",
            context.MessageSid, context.ConversationId, context.TenantId);

        // ── 1. Clasificar intención ────────────────────────────────────────
        var intent = await _classifier.ClassifyAsync(context.InboundText, ct);

        _logger.LogInformation(
            "[Agent] Intención: {Intent} ({Conf:P0}) — {Reason}",
            intent.Intent, intent.Confidence, intent.Reasoning);

        // Atajo rápido para intenciones que siempre derivan (sin llamar al LLM principal)
        if (intent.Intent is Intent.Complaint or Intent.EscalateToHuman
            || intent.IsLowConfidence)
        {
            return await BuildAndPersistEscalation(context, intent,
                $"Derivación directa por intención {intent.Intent} " +
                $"(confianza {intent.Confidence:P0}).", ct);
        }

        // ── 2. Construir prompt de sistema ────────────────────────────────
        var systemPrompt = _promptBuilder.Build(context, intent.Intent);

        // ── 3. Construir historial de mensajes ────────────────────────────
        var messages = BuildMessageHistory(context, systemPrompt);

        // ── 4-6. Ciclo de tool calling ────────────────────────────────────
        var (responseText, proposal, escalationReason, promptTok, completionTok) =
            await RunToolCallingLoopAsync(messages, context, ct);

        // ── 7. Construir AgentResult crudo ────────────────────────────────
        var updatedContext = BuildUpdatedContext(context, intent, responseText);

        AgentResult raw;

        if (escalationReason is not null)
        {
            raw = new AgentResult
            {
                Action               = AgentAction.EscalateToHuman,
                ResponseText         = "Voy a ponerte en contacto con nuestro equipo. 👋",
                Intent               = intent,
                EscalationReason     = escalationReason,
                Proposal             = null,
                UpdatedAiContextJson = updatedContext,
                ModelUsed            = MainModel,
                PromptTokens         = promptTok,
                CompletionTokens     = completionTok,
            };
        }
        else if (proposal is not null)
        {
            raw = new AgentResult
            {
                Action               = AgentAction.ProposeAppointment,
                ResponseText         = responseText,
                Intent               = intent,
                Proposal             = proposal,
                UpdatedAiContextJson = updatedContext,
                ModelUsed            = MainModel,
                PromptTokens         = promptTok,
                CompletionTokens     = completionTok,
            };
        }
        else
        {
            raw = new AgentResult
            {
                Action               = string.IsNullOrWhiteSpace(responseText)
                                           ? AgentAction.NoAction
                                           : AgentAction.SendMessage,
                ResponseText         = responseText,
                Intent               = intent,
                UpdatedAiContextJson = updatedContext,
                ModelUsed            = MainModel,
                PromptTokens         = promptTok,
                CompletionTokens     = completionTok,
            };
        }

        // ── 8. Hard limit guard ───────────────────────────────────────────
        var result = _guard.Evaluate(raw, context);

        // ── 9. Persistir turno ────────────────────────────────────────────
        await PersistTurnAsync(context, result, ct);

        _logger.LogInformation(
            "[Agent] Turno completado. Action={Action} WasBlocked={Blocked} " +
            "Tokens={Prompt}+{Completion} ConvId={ConvId}",
            result.Action, result.WasBlocked,
            result.PromptTokens, result.CompletionTokens,
            context.ConversationId);

        return result;
    }

    // ── Ciclo de tool calling ─────────────────────────────────────────────

    private async Task<(string? responseText,
                         AppointmentProposal? proposal,
                         string? escalationReason,
                         int promptTokens,
                         int completionTokens)>
        RunToolCallingLoopAsync(
            List<OpenAiMessage> messages,
            AgentContext        ctx,
            CancellationToken   ct)
    {
        var client         = _httpFactory.CreateClient("OpenAI");
        var tools          = ToolRegistry.GetToolDefinitions();
        int promptTok      = 0;
        int completionTok  = 0;
        AppointmentProposal? finalProposal  = null;
        string?              escalationReason = null;

        for (int round = 0; round < MaxToolRounds; round++)
        {
            var payload = new
            {
                model    = MainModel,
                messages,
                tools,
                tool_choice  = "auto",
                temperature  = 0.3,
                max_tokens   = 1024,
            };

            OpenAiChatResponse? body;
            try
            {
                var response = await client.PostAsJsonAsync(
                    "v1/chat/completions", payload, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError(
                        "[Agent] OpenAI error {Status}: {Body}",
                        response.StatusCode, err[..Math.Min(err.Length, 200)]);
                    return (null, null, $"OpenAI status {(int)response.StatusCode}", 0, 0);
                }

                body = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Agent] Error llamando a OpenAI en ronda {Round}.", round);
                return (null, null, "Error de red con OpenAI", 0, 0);
            }

            var choice  = body?.Choices?.FirstOrDefault();
            if (choice is null)
                return (null, null, "Respuesta vacía de OpenAI", 0, 0);

            promptTok     += body?.Usage?.PromptTokens     ?? 0;
            completionTok += body?.Usage?.CompletionTokens ?? 0;

            // ── Sin tool calls: respuesta final ───────────────────────────
            if (choice.Message?.ToolCalls is null or { Count: 0 })
            {
                return (choice.Message?.Content, finalProposal, escalationReason,
                        promptTok, completionTok);
            }

            // ── Con tool calls: ejecutar y continuar ──────────────────────
            // Añadir el mensaje del asistente con las tool calls al historial
            messages.Add(new OpenAiMessage
            {
                Role      = "assistant",
                Content   = choice.Message?.Content,
                ToolCalls = choice.Message?.ToolCalls,
            });

            foreach (var tc in choice.Message!.ToolCalls!)
            {
                var toolResult = await _tools.ExecuteAsync(
                    tc.Function.Name, tc.Function.Arguments, ctx, ct);

                // ── Escalation tool → salir del loop ──────────────────────
                if (toolResult.IsEscalation)
                {
                    escalationReason = toolResult.EscalationReason;
                    messages.Add(ToolResultMessage(tc.Id, toolResult.Content));
                    goto LoopEnd;
                }

                // ── Proposal tool → guardar propuesta ─────────────────────
                if (toolResult.IsProposal && toolResult.Proposal is not null)
                    finalProposal = toolResult.Proposal;

                messages.Add(ToolResultMessage(tc.Id, toolResult.Content));
            }

            // Si se alcanzó el máximo de rondas → escalar
            if (round == MaxToolRounds - 1)
            {
                escalationReason = $"Se alcanzó el límite de {MaxToolRounds} rondas de tool calling.";
                _logger.LogWarning(
                    "[Agent] Límite de rondas alcanzado. Escalando. ConvId={ConvId}",
                    ctx.ConversationId);
            }
        }

        LoopEnd:
        // Última respuesta del asistente (puede ser null si salimos por escalation)
        var lastAssistantMsg = messages
            .LastOrDefault(m => m.Role == "assistant" && m.Content is not null)
            ?.Content;

        return (lastAssistantMsg, finalProposal, escalationReason, promptTok, completionTok);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<OpenAiMessage> BuildMessageHistory(
        AgentContext context,
        string       systemPrompt)
    {
        var messages = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        // Historial reciente (máximo 10 mensajes para no superar ventana de contexto)
        foreach (var msg in context.RecentMessages.TakeLast(10))
        {
            messages.Add(new OpenAiMessage
            {
                Role    = msg.Direction == "inbound" ? "user" : "assistant",
                Content = msg.Body ?? "[media]",
            });
        }

        // Mensaje actual del paciente
        messages.Add(new OpenAiMessage
        {
            Role    = "user",
            Content = string.IsNullOrWhiteSpace(context.InboundText)
                          ? "[El paciente envió un archivo multimedia]"
                          : context.InboundText,
        });

        return messages;
    }

    private static OpenAiMessage ToolResultMessage(string toolCallId, string content) =>
        new()
        {
            Role       = "tool",
            Content    = content,
            ToolCallId = toolCallId,
        };

    private static string BuildUpdatedContext(
        AgentContext        ctx,
        IntentClassification intent,
        string?              responseText)
    {
        // Actualiza el AiContext JSON con el último turno para que el próximo
        // turno sepa qué intención se procesó y cuál fue la respuesta.
        try
        {
            var existing = JsonDocument.Parse(ctx.AiContextJson).RootElement;
            var dict     = new Dictionary<string, object>();

            // Preservar campos existentes
            foreach (var prop in existing.EnumerateObject())
                dict[prop.Name] = prop.Value.GetRawText();

            // Actualizar con el último turno
            dict["last_intent"]    = $"\"{intent.Intent}\"";
            dict["last_confidence"]= intent.Confidence.ToString("F2");
            dict["last_response"]  = responseText is not null
                ? $"\"{responseText.Replace("\"", "\\\"")[..Math.Min(responseText.Length, 200)]}\""
                : "null";

            var sb = new StringBuilder("{");
            foreach (var (k, v) in dict)
                sb.Append($"\"{k}\":{v},");
            if (sb.Length > 1) sb.Length--;
            sb.Append('}');
            return sb.ToString();
        }
        catch
        {
            return ctx.AiContextJson;
        }
    }

    private async Task<AgentResult> BuildAndPersistEscalation(
        AgentContext         ctx,
        IntentClassification intent,
        string               reason,
        CancellationToken    ct)
    {
        var result = new AgentResult
        {
            Action               = AgentAction.EscalateToHuman,
            ResponseText         = "Voy a ponerte en contacto con nuestro equipo para " +
                                   "que puedan ayudarte mejor. 👋",
            Intent               = intent,
            EscalationReason     = reason,
            UpdatedAiContextJson = ctx.AiContextJson,
            ModelUsed            = "none",
            PromptTokens         = 0,
            CompletionTokens     = 0,
        };

        await PersistTurnAsync(ctx, result, ct);
        return result;
    }

    private async Task PersistTurnAsync(
        AgentContext      ctx,
        AgentResult       result,
        CancellationToken ct)
    {
        try
        {
            // N-P2-01: el Take(15) puede excluir el mensaje inbound actual si la
            // conversación es muy larga. Usar null en lugar de Guid.Empty para que
            // el AgentTurn no tenga una FK huérfana que apunta a un ID inexistente.
            var matchedMsg = ctx.RecentMessages.LastOrDefault(m =>
                m.Direction        == "inbound" &&
                m.ProviderMessageId == ctx.MessageSid);

            var turn = new AgentTurn
            {
                TenantId         = ctx.TenantId,
                ConversationId   = ctx.ConversationId,
                MessageId        = matchedMsg?.Id ?? Guid.Empty,
                IntentName       = result.Intent.Intent.ToString(),
                IntentConfidence = result.Intent.Confidence,
                ActionName       = result.Action.ToString(),
                ResponseText     = result.ResponseText,
                EscalationReason = result.EscalationReason,
                WasBlocked       = result.WasBlocked,
                BlockReason      = result.BlockReason,
                ModelUsed        = result.ModelUsed,
                PromptTokens     = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                CorrelationId    = ctx.CorrelationId,
                OccurredAt       = DateTimeOffset.UtcNow,
            };

            _db.AgentTurns.Add(turn);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // El turno es observabilidad; un fallo aquí no debe bloquear la respuesta
            _logger.LogError(ex,
                "[Agent] Error persistiendo AgentTurn. ConvId={ConvId}",
                ctx.ConversationId);
        }
    }

    // ── DTOs de OpenAI ────────────────────────────────────────────────────

    internal sealed class OpenAiMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<IntentClassifier.ToolCall>? ToolCalls { get; init; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; init; }
    }

    internal sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] List<OpenAiChoice>?  Choices,
        [property: JsonPropertyName("usage")]   OpenAiUsage?         Usage);

    internal sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessageResponse? Message);

    internal sealed record OpenAiMessageResponse(
        [property: JsonPropertyName("role")]       string?                        Role,
        [property: JsonPropertyName("content")]    string?                        Content,
        [property: JsonPropertyName("tool_calls")] List<IntentClassifier.ToolCall>? ToolCalls);

    internal sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")]     int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
