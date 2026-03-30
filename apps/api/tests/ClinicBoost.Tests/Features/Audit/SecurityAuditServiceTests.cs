using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Audit;

/// <summary>
/// Tests unitarios para SecurityAuditService.
/// Cubre: persistencia, asignación de severidad/riesgo, clamping, never-throws.
/// </summary>
public sealed class SecurityAuditServiceTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static SecurityAuditService CreateService(AppDbContext db)
    {
        // Scope factory that wraps the test DB
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();
        var scopeFactory = new TestScopeFactory(provider);
        return new SecurityAuditService(scopeFactory, NullLogger<SecurityAuditService>.Instance);
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordAuthAsync_PersistsAuditLog()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        await svc.RecordAuthAsync(Guid.NewGuid(), "auth.login", actorId: Guid.NewGuid());
        await Task.Delay(150); // fire-and-forget

        db.AuditLogs.Should().HaveCount(1);
        var log = await db.AuditLogs.FirstAsync();
        log.Action.Should().Be("auth.login");
    }

    // ── Severity / risk resolution ─────────────────────────────────────────────

    [Theory]
    [InlineData("auth", "auth.login",                 "success",  "info",     1)]
    [InlineData("auth", "auth.login_failed",          "failure",  "warning",  5)]
    [InlineData("auth", "auth.token_reuse_detected",  "failure",  "critical", 10)]
    [InlineData("data", "data.delete",                "success",  "warning",  3)]
    public void ResolveSeverityAndRisk_ReturnsExpected(
        string category, string action, string outcome,
        string expectedSeverity, int expectedRisk)
    {
        var (severity, risk) = SecurityAuditService
            .ResolveSeverityAndRisk(category, action, outcome, suppliedRisk: -1);

        severity.Should().Be(expectedSeverity,
            $"action '{action}' outcome '{outcome}' → severity '{expectedSeverity}'");
        risk.Should().Be(expectedRisk,
            $"action '{action}' outcome '{outcome}' → risk {expectedRisk}");
    }

    [Fact]
    public async Task RecordSecurityAsync_IsCriticalAndClampsRisk()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        await svc.RecordSecurityAsync(Guid.NewGuid(), "intrusion.attempt", riskScore: 99);
        await Task.Delay(150);

        var log = await db.AuditLogs.FirstAsync();
        // NewValues contains the JSON with severity and riskScore
        log.NewValues.Should().Contain("\"severity\":\"critical\"");
        log.NewValues.Should().Contain("\"riskScore\":10");
    }

    [Fact]
    public async Task RecordAuthAsync_NeverThrows_WithMalformedInput()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        var act = async () => await svc.RecordAuthAsync(
            Guid.Empty, "", metadata: new string('x', 100_000));
        await act.Should().NotThrowAsync();
    }
}

// ── Test helper ────────────────────────────────────────────────────────────────

internal sealed class TestScopeFactory : IServiceScopeFactory
{
    private readonly IServiceProvider _provider;
    public TestScopeFactory(IServiceProvider p) => _provider = p;
    public IServiceScope CreateScope() => new TestScope(_provider);
}

internal sealed class TestScope : IServiceScope
{
    public IServiceProvider ServiceProvider { get; }
    public TestScope(IServiceProvider p) => ServiceProvider = p;
    public void Dispose() { }
}
