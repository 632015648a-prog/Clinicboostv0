using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClinicBoost.Api.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// IntentClassifier
//
// Clasifica la intención del mensaje inbound usando OpenAI.
//
// ESTRATEGIA
// ──────────
// · Llama a Chat Completions con response_format = json_object.
// · El modelo devuelve { "intent": "BookAppointment", "confidence": 0.92,
//   "reasoning": "..." } en un único objeto JSON.
// · Si la confianza es < MinConfidenceThreshold, la intención se
//   sobrescribe a Intent.Unknown para forzar derivación a humano.
// · Si OpenAI falla (timeout, rate-limit), devuelve Intent.Unknown con
//   confidence 0.0 para que el guard fuerce derivación.
//
// MODELO
// ──────
// Usamos gpt-4o-mini para clasificación (barato y rápido).
// El modelo principal (gpt-4o) se reserva para el ciclo de tool calling.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Clasifica la intención del mensaje inbound usando OpenAI.
/// Registrar como Scoped.
/// </summary>
public sealed class IntentClassifier
{
    private const string ClassificationModel = "gpt-4o-mini";

    private static readonly string ClassificationSystemPrompt = """
        Eres un clasificador de intenciones para una clínica de fisioterapia.
        Analiza el mensaje del paciente y devuelve EXCLUSIVAMENTE un JSON con este esquema:
        {
          "intent": "<IntentName>",
          "confidence": <0.0-1.0>,
          "reasoning": "<una frase corta>"
        }

        Valores válidos para "intent":
          BookAppointment      — solicita nueva cita o cambio de horario
          CancelAppointment    — quiere cancelar una cita existente
          QueryAppointment     — pregunta sobre sus citas (cuándo, dónde, etc.)
          GeneralInquiry       — pregunta sobre servicios, precios, ubicación
          Complaint            — queja o insatisfacción
          DiscountRequest      — pide descuento o promoción
          EscalateToHuman      — quiere hablar con una persona
          AppointmentConfirm   — confirma o rechaza una cita recordada
          Unknown              — no se puede determinar

        Reglas:
        - Si el mensaje tiene menos de 3 palabras inteligibles, devuelve Unknown con confidence 0.3.
        - La confianza debe reflejar tu certeza real, no ser siempre alta.
        - Responde SOLO con el JSON, sin texto adicional.
        """;

    private readonly IHttpClientFactory              _httpFactory;
    private readonly ILogger<IntentClassifier>       _logger;

    public IntentClassifier(
        IHttpClientFactory        httpFactory,
        ILogger<IntentClassifier> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Clasifica la intención del <paramref name="text"/> del paciente.
    /// Nunca lanza excepción: ante cualquier error devuelve Unknown.
    /// </summary>
    public async Task<IntentClassification> ClassifyAsync(
        string            text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new IntentClassification
            {
                Intent     = Intent.Unknown,
                Confidence = 0.0,
                Reasoning  = "Mensaje vacío",
            };
        }

        try
        {
            var client   = _httpFactory.CreateClient("OpenAI");
            var payload  = BuildPayload(text);
            var response = await client.PostAsJsonAsync(
                "v1/chat/completions", payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[IntentClassifier] OpenAI devolvió {Status}. Fallback a Unknown.",
                    response.StatusCode);
                return UnknownFallback("OpenAI status " + (int)response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(
                cancellationToken: ct);

            var content = body?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
                return UnknownFallback("Respuesta vacía de OpenAI");

            return ParseClassification(content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "[IntentClassifier] Error llamando a OpenAI. Fallback a Unknown.");
            return UnknownFallback(ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static object BuildPayload(string text) => new
    {
        model    = ClassificationModel,
        messages = new[]
        {
            new { role = "system", content = ClassificationSystemPrompt },
            new { role = "user",   content = text },
        },
        response_format = new { type = "json_object" },
        temperature     = 0,        // máxima determinismo para clasificación
        max_tokens      = 120,
    };

    private IntentClassification ParseClassification(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ClassificationDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (dto is null)
                return UnknownFallback("JSON nulo al parsear clasificación");

            if (!Enum.TryParse<Intent>(dto.Intent, ignoreCase: true, out var intent))
            {
                _logger.LogWarning(
                    "[IntentClassifier] Valor de intent desconocido: '{Raw}'. Usando Unknown.",
                    dto.Intent);
                intent = Intent.Unknown;
            }

            var confidence = Math.Clamp(dto.Confidence, 0.0, 1.0);

            // Si la confianza es baja, forzar Unknown para que el guard actúe
            if (confidence < IntentClassification.MinConfidenceThreshold)
                intent = Intent.Unknown;

            return new IntentClassification
            {
                Intent     = intent,
                Confidence = confidence,
                Reasoning  = dto.Reasoning ?? string.Empty,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "[IntentClassifier] Error parseando JSON de clasificación. Fallback a Unknown.");
            return UnknownFallback("JSON inválido");
        }
    }

    private static IntentClassification UnknownFallback(string reason) =>
        new()
        {
            Intent     = Intent.Unknown,
            Confidence = 0.0,
            Reasoning  = reason,
        };

    // ── DTOs internos de OpenAI ───────────────────────────────────────────

    private sealed record ClassificationDto(
        [property: JsonPropertyName("intent")]     string   Intent,
        [property: JsonPropertyName("confidence")] double   Confidence,
        [property: JsonPropertyName("reasoning")]  string?  Reasoning);

    internal sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")]  List<Choice>?  Choices);

    internal sealed record Choice(
        [property: JsonPropertyName("message")]  ChatMessage?  Message);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")]     string? Role,
        [property: JsonPropertyName("content")]  string? Content,
        [property: JsonPropertyName("tool_calls")] List<ToolCall>? ToolCalls);

    internal sealed record ToolCall(
        [property: JsonPropertyName("id")]       string   Id,
        [property: JsonPropertyName("type")]     string   Type,
        [property: JsonPropertyName("function")] ToolCallFunction Function);

    internal sealed record ToolCallFunction(
        [property: JsonPropertyName("name")]      string  Name,
        [property: JsonPropertyName("arguments")] string  Arguments);
}
