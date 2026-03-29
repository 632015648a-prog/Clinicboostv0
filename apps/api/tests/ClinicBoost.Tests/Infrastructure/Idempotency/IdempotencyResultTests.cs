using ClinicBoost.Api.Infrastructure.Idempotency;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Idempotency;

/// <summary>
/// Tests unitarios de IdempotencyResult.
/// Verifica fábricas, propiedades derivadas y deconstrucción.
/// Sin dependencias de infraestructura (BD, red).
/// </summary>
public sealed class IdempotencyResultTests
{
    private static readonly Guid           AnyId  = Guid.NewGuid();
    private static readonly DateTimeOffset AnyTs  = DateTimeOffset.UtcNow;

    // ── NewEvent ──────────────────────────────────────────────────────────────

    [Fact]
    public void NewEvent_ShouldProcess_IsTrue()
    {
        var result = IdempotencyResult.NewEvent(AnyId, AnyTs);

        result.AlreadyProcessed.Should().BeFalse();
        result.IsPayloadMismatch.Should().BeFalse();
        result.IsError.Should().BeFalse();
        result.ShouldProcess.Should().BeTrue();
        result.ShouldSkip.Should().BeFalse();
        result.ProcessedEventId.Should().Be(AnyId);
        result.FirstProcessedAt.Should().Be(AnyTs);
        result.Error.Should().BeNull();
    }

    // ── Duplicate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_ShouldSkip_IsTrue()
    {
        var result = IdempotencyResult.Duplicate(AnyId, AnyTs);

        result.AlreadyProcessed.Should().BeTrue();
        result.IsPayloadMismatch.Should().BeFalse();
        result.IsError.Should().BeFalse();
        result.ShouldProcess.Should().BeFalse();
        result.ShouldSkip.Should().BeTrue();
        result.ProcessedEventId.Should().Be(AnyId);
        result.FirstProcessedAt.Should().Be(AnyTs);
    }

    // ── PayloadMismatch ───────────────────────────────────────────────────────

    [Fact]
    public void PayloadMismatch_HasBothFlags()
    {
        var result = IdempotencyResult.PayloadMismatch(AnyId, AnyTs);

        result.AlreadyProcessed.Should().BeTrue();
        result.IsPayloadMismatch.Should().BeTrue();
        result.IsError.Should().BeFalse();
        result.ShouldProcess.Should().BeFalse();
        result.ShouldSkip.Should().BeTrue("el caller debe rechazarlo, no reintentarlo");
        result.ProcessedEventId.Should().Be(AnyId);
    }

    // ── Failure ───────────────────────────────────────────────────────────────

    [Fact]
    public void Failure_HasErrorAndNoIds()
    {
        var ex     = new InvalidOperationException("DB error");
        var result = IdempotencyResult.Failure(ex);

        result.IsError.Should().BeTrue();
        result.Error.Should().BeSameAs(ex);
        result.AlreadyProcessed.Should().BeFalse();
        result.ShouldProcess.Should().BeFalse("ante error no se debe procesar");
        result.ShouldSkip.Should().BeFalse("no es un skip limpio; es un error");
        result.ProcessedEventId.Should().BeNull();
        result.FirstProcessedAt.Should().BeNull();
    }

    // ── Deconstruct ───────────────────────────────────────────────────────────

    [Fact]
    public void Deconstruct_NewEvent_GivesCorrectValues()
    {
        var (shouldProcess, id) = IdempotencyResult.NewEvent(AnyId, AnyTs);

        shouldProcess.Should().BeTrue();
        id.Should().Be(AnyId);
    }

    [Fact]
    public void Deconstruct_Duplicate_GivesCorrectValues()
    {
        var (shouldProcess, id) = IdempotencyResult.Duplicate(AnyId, AnyTs);

        shouldProcess.Should().BeFalse();
        id.Should().Be(AnyId);
    }

    // ── ToString ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToString_ContainsRelevantInfo()
    {
        var r1 = IdempotencyResult.NewEvent(AnyId, AnyTs);
        var r2 = IdempotencyResult.Failure(new Exception("boom"));

        r1.ToString().Should().Contain("AlreadyProcessed=False");
        r2.ToString().Should().Contain("Error");
    }
}
