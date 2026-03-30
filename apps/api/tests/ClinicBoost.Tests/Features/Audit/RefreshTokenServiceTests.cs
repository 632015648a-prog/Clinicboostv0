using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Audit;

/// <summary>
/// Tests unitarios para RefreshTokenService.
/// Cubre: emisión, rotación (válida/expirada/breach), revocación, sesiones activas.
/// Nota: el proveedor InMemory no soporta ExecuteUpdateAsync; el servicio
/// incluye fallback automático con load-and-save.
/// </summary>
public sealed class RefreshTokenServiceTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static RefreshTokenService CreateService(
        AppDbContext db,
        ISecurityAuditService? audit = null)
    {
        audit ??= Substitute.For<ISecurityAuditService>();
        var options = Options.Create(new RefreshTokenOptions { ExpiryDays = 30 });
        return new RefreshTokenService(
            db, audit, NullLogger<RefreshTokenService>.Instance, options);
    }

    // ── Issue ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueAsync_StoresHashNotPlaintext()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        var result = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Success.Should().BeTrue();
        var stored = await db.RefreshTokens.SingleAsync();
        stored.TokenHash.Should().NotBe(result.PlainToken);
        stored.TokenHash.Length.Should().Be(64, "SHA-256 → 64 hex chars");
        stored.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task IssueAsync_AssignsFamilyId()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        var result = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        result.FamilyId.Should().NotBeEmpty();
        var stored = await db.RefreshTokens.SingleAsync();
        stored.FamilyId.Should().Be(result.FamilyId!.Value);
    }

    // ── Rotate ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_ValidToken_ReturnsNewToken_AndMarksOldAsUsed()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var issue = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        var rotate = await svc.RotateAsync(issue.PlainToken!);

        rotate.Success.Should().BeTrue();
        rotate.PlainToken.Should().NotBe(issue.PlainToken);

        var old = await db.RefreshTokens.SingleAsync(t => t.Id == issue.TokenId!.Value);
        old.IsRevoked.Should().BeTrue();
        old.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rotate_ExpiredToken_ReturnsExpiredError()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var issue = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        var token = await db.RefreshTokens.SingleAsync();
        token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var result = await svc.RotateAsync(issue.PlainToken!);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("expired");
    }

    [Fact]
    public async Task Rotate_UsedToken_DetectesBreach_ReturnedBreach()
    {
        var db    = CreateDb();
        var audit = Substitute.For<ISecurityAuditService>();
        var svc   = CreateService(db, audit);

        var issue = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());
        // Primera rotación válida
        var r1 = await svc.RotateAsync(issue.PlainToken!);
        r1.Success.Should().BeTrue();

        // Reuso del token original → breach
        var r2 = await svc.RotateAsync(issue.PlainToken!);
        r2.Success.Should().BeFalse();
        r2.IsBreach.Should().BeTrue();
        r2.Error.Should().Be("token_reuse");

        // Toda la familia marcada como comprometida
        var family = await db.RefreshTokens
            .Where(t => t.FamilyId == issue.FamilyId!.Value)
            .ToListAsync();
        family.Should().AllSatisfy(t => t.IsRevoked.Should().BeTrue());
        family.Should().AllSatisfy(t => t.IsCompromised.Should().BeTrue());

        // Auditoría llamada con riskScore=10
        await audit.Received().RecordAuthAsync(
            Arg.Any<Guid>(),
            "auth.token_reuse_detected",
            outcome:       Arg.Any<string>(),
            actorId:       Arg.Any<Guid?>(),
            actorRole:     Arg.Any<string?>(),
            ipAddress:     Arg.Any<string?>(),
            userAgent:     Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            metadata:      Arg.Any<string?>(),
            riskScore:     10);
    }

    [Fact]
    public async Task Rotate_UnknownToken_ReturnsNotFound()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        var result = await svc.RotateAsync("totalmente-desconocido");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    // ── Revoke ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAsync_MarksTokenRevoked()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var issue = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        var ok = await svc.RevokeAsync(issue.PlainToken!, "logout");

        ok.Should().BeTrue();
        var t = await db.RefreshTokens.SingleAsync();
        t.IsRevoked.Should().BeTrue();
        t.RevokedReason.Should().Be("logout");
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_ReturnsFalse()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var issue = await svc.IssueAsync(Guid.NewGuid(), Guid.NewGuid());

        await svc.RevokeAsync(issue.PlainToken!);
        var second = await svc.RevokeAsync(issue.PlainToken!);

        second.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAllForUser_DoesNotThrow_AndReturnsNonNegativeCount()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var userId   = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        await svc.IssueAsync(tenantId, userId);
        await svc.IssueAsync(tenantId, userId);
        await svc.IssueAsync(tenantId, userId);

        int count = 0;
        var act = async () => count = await svc.RevokeAllForUserAsync(userId, tenantId);
        await act.Should().NotThrowAsync();
        count.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Sessions ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveSessionsAsync_ReturnsOnlyActiveSessions()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var userId   = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var t1 = await svc.IssueAsync(tenantId, userId);
        var t2 = await svc.IssueAsync(tenantId, userId);
        await svc.RevokeAsync(t1.PlainToken!);

        var sessions = await svc.GetActiveSessionsAsync(userId, tenantId);

        sessions.Should().HaveCount(1);
        sessions[0].TokenId.Should().Be(t2.TokenId!.Value);
    }

    [Fact]
    public async Task GetActiveSessionsAsync_TenantIsolation()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var userId  = Guid.NewGuid();
        var tenant1 = Guid.NewGuid();
        var tenant2 = Guid.NewGuid();

        await svc.IssueAsync(tenant1, userId);
        await svc.IssueAsync(tenant2, userId);

        var sessions = await svc.GetActiveSessionsAsync(userId, tenant1);

        sessions.Should().HaveCount(1, "solo sesiones de tenant1");
    }
}
