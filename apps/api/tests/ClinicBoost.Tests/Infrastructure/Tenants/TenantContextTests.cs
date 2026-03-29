using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Tenants;

public sealed class TenantContextTests
{
    private static readonly Guid   ValidTenantId = Guid.NewGuid();
    private static readonly Guid   ValidUserId   = Guid.NewGuid();
    private const           string ValidRole     = TenantRole.Admin;

    // ── Estado inicial ────────────────────────────────────────────────────────

    [Fact]
    public void New_IsNotInitialized()
    {
        var ctx = new TenantContext();

        ctx.IsInitialized.Should().BeFalse();
        ctx.TenantId.Should().BeNull();
        ctx.UserRole.Should().BeNull();
        ctx.UserId.Should().BeNull();
    }

    // ── Initialize happy path ─────────────────────────────────────────────────

    [Fact]
    public void Initialize_SetsAllProperties()
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, ValidRole, ValidUserId);

        ctx.IsInitialized.Should().BeTrue();
        ctx.TenantId.Should().Be(ValidTenantId);
        ctx.UserRole.Should().Be(ValidRole);
        ctx.UserId.Should().Be(ValidUserId);
    }

    [Fact]
    public void Initialize_WithNullUserId_IsValid()
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, ValidRole, null);

        ctx.IsInitialized.Should().BeTrue();
        ctx.UserId.Should().BeNull();
    }

    [Fact]
    public void Initialize_WithUnknownRole_SetsRoleToNull()
    {
        // ClaimsExtractor ya valida el rol, pero TenantContext también lo desecha si es inválido
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, "root", ValidUserId);

        ctx.UserRole.Should().BeNull("un rol desconocido debe descartarse silenciosamente");
    }

    // ── Double-init guard ─────────────────────────────────────────────────────

    [Fact]
    public void Initialize_Twice_ThrowsContextAlreadyInitialized()
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, ValidRole, ValidUserId);

        var act = () => ctx.Initialize(Guid.NewGuid(), "owner", null);

        act.Should().Throw<TenantContextException>()
           .Where(e => e.Code == TenantContextErrorCode.ContextAlreadyInitialized);
    }

    // ── RequireTenantId ───────────────────────────────────────────────────────

    [Fact]
    public void RequireTenantId_ReturnsGuid_WhenInitialized()
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, ValidRole, null);

        ctx.RequireTenantId().Should().Be(ValidTenantId);
    }

    [Fact]
    public void RequireTenantId_Throws_WhenNotInitialized()
    {
        var ctx = new TenantContext();

        var act = () => ctx.RequireTenantId();

        act.Should().Throw<TenantContextException>()
           .Where(e => e.Code == TenantContextErrorCode.ContextNotInitialized);
    }

    // ── HasAtLeastRole ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TenantRole.Admin,       TenantRole.Admin,       true)]
    [InlineData(TenantRole.Admin,       TenantRole.Therapist,   true)]
    [InlineData(TenantRole.Admin,       TenantRole.Owner,       false)]
    [InlineData(TenantRole.Therapist,   TenantRole.Owner,       false)]
    [InlineData(TenantRole.Owner,       TenantRole.Owner,       true)]
    public void HasAtLeastRole_RespectsHierarchy(string userRole, string minRole, bool expected)
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, userRole, null);

        ctx.HasAtLeastRole(minRole).Should().Be(expected);
    }

    [Fact]
    public void HasAtLeastRole_ReturnsFalse_WhenNotInitialized()
    {
        var ctx = new TenantContext();
        ctx.HasAtLeastRole(TenantRole.Service).Should().BeFalse();
    }

    // ── IsRole ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsRole_ReturnsTrue_ForExactMatch()
    {
        var ctx = new TenantContext();
        ctx.Initialize(ValidTenantId, TenantRole.Owner, null);

        ctx.IsRole(TenantRole.Owner).Should().BeTrue();
        ctx.IsRole(TenantRole.Admin).Should().BeFalse();
    }

    [Fact]
    public void IsRole_ReturnsFalse_WhenNotInitialized()
    {
        var ctx = new TenantContext();
        ctx.IsRole(TenantRole.Owner).Should().BeFalse();
    }
}
