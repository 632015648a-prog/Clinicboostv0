using System.Security.Cryptography;
using System.Text;
using ClinicBoost.Api.Infrastructure.Twilio;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Twilio;

// ════════════════════════════════════════════════════════════════════════════
// TwilioSignatureValidatorTests
//
// Valida el algoritmo HMAC-SHA1 según la especificación oficial de Twilio.
// Ref: https://www.twilio.com/docs/usage/webhooks/webhooks-security
//
// ESTRATEGIA:
//   · Calculamos la firma esperada con el mismo algoritmo que usa Twilio,
//     y la comparamos con la que produce nuestro validador.
//   · Los casos negativos cubren todas las rutas de fallo posibles.
// ════════════════════════════════════════════════════════════════════════════

public sealed class TwilioSignatureValidatorTests
{
    private const string TestAuthToken = "test_auth_token_32chars_minimumxx";
    private const string TestUrl       = "https://api.clinicboost.es/webhooks/twilio/voice";

    // ── Helper: genera la firma correcta para un conjunto de params ───────────

    private static string ComputeExpectedSignature(
        string authToken,
        string url,
        IEnumerable<KeyValuePair<string, string>> formParams)
    {
        var sb = new StringBuilder(url);
        foreach (var kv in formParams.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }

        var keyBytes  = Encoding.UTF8.GetBytes(authToken);
        var dataBytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash      = HMACSHA1.HashData(keyBytes, dataBytes);
        return Convert.ToBase64String(hash);
    }

    private static TwilioSignatureValidator BuildSut(string? authToken = null) =>
        new(authToken ?? TestAuthToken);

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Firmas válidas
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsValid_ReturnsTrue_ForCorrectSignature()
    {
        var sut    = BuildSut();
        var @params = new[]
        {
            new KeyValuePair<string, string>("CallSid",    "CA12345"),
            new KeyValuePair<string, string>("From",       "+34612345678"),
            new KeyValuePair<string, string>("To",         "+34910123456"),
            new KeyValuePair<string, string>("CallStatus", "no-answer"),
        };

        var signature = ComputeExpectedSignature(TestAuthToken, TestUrl, @params);
        var result    = sut.IsValid(@params, TestUrl, signature);

        result.Should().BeTrue("la firma generada con el mismo token debe ser válida");
    }

    [Fact]
    public void IsValid_ReturnsTrue_WithEmptyFormParams()
    {
        // Twilio puede enviar webhooks sin form params (p.ej. algunos status callbacks)
        var sut    = BuildSut();
        var @params = Enumerable.Empty<KeyValuePair<string, string>>();

        var signature = ComputeExpectedSignature(TestAuthToken, TestUrl, @params);
        var result    = sut.IsValid(@params, TestUrl, signature);

        result.Should().BeTrue("firma válida con params vacíos");
    }

    [Fact]
    public void IsValid_ReturnsTrue_WhenParamsAreUnordered()
    {
        // El validador debe ordenar los params internamente; no depende del orden del caller
        var sut = BuildSut();

        // Params en orden inverso
        var paramsOrdered = new[]
        {
            new KeyValuePair<string, string>("AccountSid", "ACxxx"),
            new KeyValuePair<string, string>("CallSid",    "CAyyy"),
            new KeyValuePair<string, string>("From",       "+1"),
            new KeyValuePair<string, string>("To",         "+2"),
        };
        var paramsUnordered = paramsOrdered.Reverse().ToList();

        var signature = ComputeExpectedSignature(TestAuthToken, TestUrl, paramsOrdered);
        var result    = sut.IsValid(paramsUnordered, TestUrl, signature);

        result.Should().BeTrue("el validador ordena los params antes de calcular el hash");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Firmas inválidas — rechazar siempre
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsValid_ReturnsFalse_WhenSignatureIsEmpty()
    {
        var sut    = BuildSut();
        var result = sut.IsValid([], TestUrl, string.Empty);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenSignatureIsWhitespace()
    {
        var sut    = BuildSut();
        var result = sut.IsValid([], TestUrl, "   ");
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenSignatureIsInvalidBase64()
    {
        var sut    = BuildSut();
        var result = sut.IsValid([], TestUrl, "not-valid-base64!!!");
        result.Should().BeFalse("Base64 inválido debe devolver false, no exception");
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenUrlIsEmpty()
    {
        var sut    = BuildSut();
        var sig    = ComputeExpectedSignature(TestAuthToken, TestUrl, []);
        var result = sut.IsValid([], string.Empty, sig);
        result.Should().BeFalse("URL vacía → inválido");
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenUrlDiffers()
    {
        var sut        = BuildSut();
        var @params    = new[] { new KeyValuePair<string, string>("CallSid", "CA1") };
        var correctSig = ComputeExpectedSignature(TestAuthToken, TestUrl, @params);

        // Mismo params, distinta URL (attacker cambió el host)
        var result = sut.IsValid(@params, "https://evil.com/webhooks", correctSig);
        result.Should().BeFalse("la URL forma parte del string firmado");
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenParamValueTampered()
    {
        var sut = BuildSut();
        var originalParams = new[]
        {
            new KeyValuePair<string, string>("CallSid", "CA123"),
            new KeyValuePair<string, string>("From",    "+34600000000"),
        };
        var sig = ComputeExpectedSignature(TestAuthToken, TestUrl, originalParams);

        // Atacante modifica el número de teléfono en tránsito
        var tamperedParams = new[]
        {
            new KeyValuePair<string, string>("CallSid", "CA123"),
            new KeyValuePair<string, string>("From",    "+34999999999"),  // modificado
        };

        var result = sut.IsValid(tamperedParams, TestUrl, sig);
        result.Should().BeFalse("param modificado invalida la firma");
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenAuthTokenIsWrong()
    {
        // Firma generada con otro token
        var sigFromOtherToken = ComputeExpectedSignature("other_token_32chars_minimum_xxx", TestUrl, []);
        var sut    = BuildSut(TestAuthToken);
        var result = sut.IsValid([], TestUrl, sigFromOtherToken);
        result.Should().BeFalse("firma de otro AuthToken debe ser inválida");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Construcción
    // ════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Throws_WhenAuthTokenIsNullOrWhitespace(string? token)
    {
        var act = () => new TwilioSignatureValidator(token!);
        act.Should().Throw<ArgumentException>();
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 4: Resistencia a timing attacks
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void IsValid_DoesNotShortCircuit_OnHashMismatch()
    {
        // Este test verifica el comportamiento observable (devuelve false).
        // No podemos medir timing directamente en un unit test, pero al menos
        // verificamos que el método no lanza excepción ante firma incorrecta.
        var sut    = BuildSut();
        var result = sut.IsValid([], TestUrl, "aGVsbG8=");  // "hello" en Base64
        result.Should().BeFalse();
    }
}
