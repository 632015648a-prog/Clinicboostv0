using System.Net;
using System.Text;
using System.Text.Json;
using ClinicBoost.Api.Features.Agent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Agent;

// ════════════════════════════════════════════════════════════════════════════
// IntentClassifierTests
//
// Tests unitarios del IntentClassifier con HttpClient mockeado.
//
// ESTRATEGIA
// ──────────
// · FakeHttpMessageHandler intercepta las llamadas HTTP y devuelve JSON
//   predefinido, sin llamar a la API de OpenAI real.
// · Cada test configura la respuesta del handler para simular el JSON
//   que devolvería OpenAI con response_format=json_object.
// · Se verifica el parsing, la normalización de confianza y el fallback
//   a Intent.Unknown en caso de error o confianza baja.
// ════════════════════════════════════════════════════════════════════════════

public sealed class IntentClassifierTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IntentClassifier BuildSut(string openAiJson)
    {
        var handler = new FakeHttpMessageHandler(openAiJson);
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("OpenAI").Returns(client);

        return new IntentClassifier(factory, NullLogger<IntentClassifier>.Instance);
    }

    private static string OpenAiResponse(string intentJson) =>
        $$"""
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": {{JsonSerializer.Serialize(intentJson)}}
              }
            }
          ]
        }
        """;

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Clasificación correcta de intenciones conocidas
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("BookAppointment",   Intent.BookAppointment)]
    [InlineData("CancelAppointment", Intent.CancelAppointment)]
    [InlineData("QueryAppointment",  Intent.QueryAppointment)]
    [InlineData("GeneralInquiry",    Intent.GeneralInquiry)]
    [InlineData("Complaint",         Intent.Complaint)]
    [InlineData("DiscountRequest",   Intent.DiscountRequest)]
    [InlineData("EscalateToHuman",   Intent.EscalateToHuman)]
    [InlineData("AppointmentConfirm",Intent.AppointmentConfirm)]
    public async Task ClassifyAsync_ReturnsCorrectIntent_ForKnownIntents(
        string intentName, Intent expected)
    {
        var payload = $"{{\"intent\":\"{intentName}\",\"confidence\":0.92,\"reasoning\":\"test\"}}";
        var sut     = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("mensaje de prueba");

        result.Intent.Should().Be(expected);
        result.Confidence.Should().BeApproximately(0.92, 0.001);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Fallback a Unknown
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClassifyAsync_ReturnsUnknown_WhenIntentNameIsUnrecognized()
    {
        var payload = "{\"intent\":\"FlyToTheMoon\",\"confidence\":0.9,\"reasoning\":\"test\"}";
        var sut     = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("algo raro");

        result.Intent.Should().Be(Intent.Unknown,
            "un nombre de intención desconocido debe dar Unknown");
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsUnknown_WhenConfidenceIsBelowThreshold()
    {
        var payload = "{\"intent\":\"BookAppointment\",\"confidence\":0.4,\"reasoning\":\"bajo\"}";
        var sut     = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("quiero algo");

        result.Intent.Should().Be(Intent.Unknown,
            "confianza < umbral debe forzar Unknown");
        result.Confidence.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsUnknown_WhenTextIsEmpty()
    {
        // Sin llamar a OpenAI: texto vacío → Unknown directo
        var sut = BuildSut(OpenAiResponse("{\"intent\":\"BookAppointment\",\"confidence\":0.9,\"reasoning\":\"\"}"));

        var result = await sut.ClassifyAsync(string.Empty);

        result.Intent.Should().Be(Intent.Unknown, "texto vacío → Unknown sin llamar a OpenAI");
        result.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsUnknown_WhenOpenAiReturnsErrorStatus()
    {
        var handler = new FakeHttpMessageHandler("{}", HttpStatusCode.TooManyRequests);
        var client  = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("OpenAI").Returns(client);
        var sut = new IntentClassifier(factory, NullLogger<IntentClassifier>.Instance);

        var result = await sut.ClassifyAsync("quiero una cita");

        result.Intent.Should().Be(Intent.Unknown, "error de API → fallback Unknown");
        result.Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task ClassifyAsync_ReturnsUnknown_WhenJsonIsInvalid()
    {
        var sut = BuildSut(OpenAiResponse("esto no es json"));

        var result = await sut.ClassifyAsync("prueba");

        result.Intent.Should().Be(Intent.Unknown, "JSON inválido → fallback Unknown");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Razonamiento y metadatos
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClassifyAsync_ReturnsReasoning_FromOpenAiResponse()
    {
        var payload = "{\"intent\":\"GeneralInquiry\",\"confidence\":0.88," +
                      "\"reasoning\":\"El usuario pregunta por horarios\"}";
        var sut = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("¿Cuál es el horario?");

        result.Reasoning.Should().Be("El usuario pregunta por horarios");
    }

    [Fact]
    public async Task ClassifyAsync_ClampsConfidence_ToZeroOneRange()
    {
        // Defensivo: aunque OpenAI devuelva >1 o <0, debe clampear
        var payload = "{\"intent\":\"BookAppointment\",\"confidence\":1.5,\"reasoning\":\"\"}";
        var sut     = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("reserva");

        result.Confidence.Should().BeInRange(0.0, 1.0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: IsLowConfidence
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.64, true)]
    [InlineData(0.65, false)]   // exactamente en el umbral → no es bajo
    [InlineData(0.9,  false)]
    public async Task ClassifyAsync_IsLowConfidence_ReflectsThreshold(
        double confidence, bool expectedLow)
    {
        var intentName = confidence >= IntentClassification.MinConfidenceThreshold
            ? "BookAppointment"
            : "Unknown";
        var payload = $"{{\"intent\":\"{intentName}\",\"confidence\":{confidence},\"reasoning\":\"\"}}";
        var sut     = BuildSut(OpenAiResponse(payload));

        var result = await sut.ClassifyAsync("texto");

        result.IsLowConfidence.Should().Be(expectedLow);
    }
}

// ── FakeHttpMessageHandler ────────────────────────────────────────────────────

/// <summary>
/// Handler HTTP fake que devuelve un JSON predefinido sin llamar a la red.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string     _responseJson;
    private readonly HttpStatusCode _statusCode;

    public FakeHttpMessageHandler(
        string        responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _statusCode   = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        });
}
