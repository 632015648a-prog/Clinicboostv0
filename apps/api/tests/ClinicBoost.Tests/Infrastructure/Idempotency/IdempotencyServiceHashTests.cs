using ClinicBoost.Api.Infrastructure.Idempotency;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Idempotency;

/// <summary>
/// Tests unitarios del helper ComputeHash de IdempotencyService.
/// No requieren BD ni infraestructura.
/// </summary>
public sealed class IdempotencyServiceHashTests
{
    // ── ComputeHash ───────────────────────────────────────────────────────────

    [Fact]
    public void ComputeHash_ReturnsSha256Hex_Of64Chars()
    {
        var hash = IdempotencyService.ComputeHash("hello world");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "debe ser hex lowercase");
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        const string input = "twilio:SMxxxxxxxx";

        var h1 = IdempotencyService.ComputeHash(input);
        var h2 = IdempotencyService.ComputeHash(input);

        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        var h1 = IdempotencyService.ComputeHash("{\"MessageSid\":\"SM001\",\"Body\":\"Hola\"}");
        var h2 = IdempotencyService.ComputeHash("{\"MessageSid\":\"SM001\",\"Body\":\"Adios\"}");

        h1.Should().NotBe(h2, "cuerpos distintos deben producir hashes distintos");
    }

    [Fact]
    public void ComputeHash_EmptyString_IsValid()
    {
        var hash = IdempotencyService.ComputeHash(string.Empty);

        // SHA-256 de string vacío es e3b0c44298fc1c149afbf4c8996fb924...
        hash.Should().HaveLength(64);
        hash.Should().StartWith("e3b0c4");
    }

    [Fact]
    public void ComputeHash_IsLowercase()
    {
        var hash = IdempotencyService.ComputeHash("ClinicBoost");
        hash.Should().Be(hash.ToLowerInvariant(), "el hash siempre es lowercase");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sid\":\"SM001\"}")]
    [InlineData("MessageSid=SM001&From=%2B34600000000&Body=Hola")]  // form-encoded Twilio
    public void ComputeHash_CommonPayloadFormats_Return64Chars(string payload)
    {
        var hash = IdempotencyService.ComputeHash(payload);
        hash.Should().HaveLength(64);
    }
}
