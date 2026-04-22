using ClinicBoost.Api.Features.Audit;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClinicBoost.Tests.Features.Audit;

/// <summary>
/// Tests unitarios para SessionInvalidationService.
/// Cubre: revocación, caché, idempotencia, limpieza de expirados.
/// </summary>
public sealed class SessionInvalidationServiceTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static SessionInvalidationService CreateService(AppDbContext db)
    {
        var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        return new SessionInvalidationService(
            db, cache, NullLogger<SessionInvalidationService>.Instance);
    }

    [Fact]
    public async Task RevokeJtiAsync_StoresRevocation()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var jti = Guid.NewGuid().ToString();

        await svc.RevokeJtiAsync(Guid.NewGuid(), Guid.NewGuid(), jti,
            DateTimeOffset.UtcNow.AddHours(1), "logout");

        db.SessionRevocations.Should().HaveCount(1);
        (await db.SessionRevocations.SingleAsync()).Jti.Should().Be(jti);
    }

    [Fact]
    public async Task RevokeJtiAsync_IsIdempotent()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var jti = Guid.NewGuid().ToString();
        var exp = DateTimeOffset.UtcNow.AddHours(1);
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();

        await svc.RevokeJtiAsync(tenantId, userId, jti, exp);
        await svc.RevokeJtiAsync(tenantId, userId, jti, exp);

        db.SessionRevocations.Should().HaveCount(1, "duplicado debe ignorarse");
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsTrueForRevokedJti()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var jti = Guid.NewGuid().ToString();
        await svc.RevokeJtiAsync(Guid.NewGuid(), Guid.NewGuid(), jti,
            DateTimeOffset.UtcNow.AddHours(1));

        (await svc.IsRevokedAsync(jti)).Should().BeTrue();
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsFalseForUnknownJti()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        (await svc.IsRevokedAsync(Guid.NewGuid().ToString())).Should().BeFalse();
    }

    [Fact]
    public async Task IsRevokedAsync_ReturnsTrueFromCache_EvenAfterDbDelete()
    {
        var db  = CreateDb();
        var svc = CreateService(db);
        var jti = Guid.NewGuid().ToString();
        await svc.RevokeJtiAsync(Guid.NewGuid(), Guid.NewGuid(), jti,
            DateTimeOffset.UtcNow.AddHours(1));

        // Primera llamada → carga caché
        var first = await svc.IsRevokedAsync(jti);

        // Eliminar de BD para forzar uso de caché
        db.SessionRevocations.RemoveRange(await db.SessionRevocations.ToListAsync());
        await db.SaveChangesAsync();

        var second = await svc.IsRevokedAsync(jti);

        first.Should().BeTrue();
        second.Should().BeTrue("la caché debe mantener la revocación");
    }

    [Fact]
    public async Task CleanupExpiredAsync_DeletesExpiredRecords()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        db.SessionRevocations.Add(new SessionRevocation
        {
            Jti          = "expired-jti",
            TenantId     = Guid.NewGuid(),
            UserId       = Guid.NewGuid(),
            JwtExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
        db.SessionRevocations.Add(new SessionRevocation
        {
            Jti          = "valid-jti",
            TenantId     = Guid.NewGuid(),
            UserId       = Guid.NewGuid(),
            JwtExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        var deleted = await svc.CleanupExpiredAsync();

        deleted.Should().Be(1, "solo el JTI expirado debe eliminarse");
        db.SessionRevocations.Should().HaveCount(1);
        (await db.SessionRevocations.SingleAsync()).Jti.Should().Be("valid-jti");
    }

    [Fact]
    public async Task CleanupExpiredAsync_ReturnsZeroWhenNoneExpired()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        db.SessionRevocations.Add(new SessionRevocation
        {
            Jti          = "valid-jti",
            TenantId     = Guid.NewGuid(),
            UserId       = Guid.NewGuid(),
            JwtExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
        });
        await db.SaveChangesAsync();

        (await svc.CleanupExpiredAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CleanupExpiredAsync_NeverThrows()
    {
        var db  = CreateDb();
        var svc = CreateService(db);

        var act = async () => await svc.CleanupExpiredAsync();
        await act.Should().NotThrowAsync();
    }
}
