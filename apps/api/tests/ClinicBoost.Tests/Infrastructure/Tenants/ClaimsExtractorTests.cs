using System.Security.Claims;
using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Tenants;

public sealed class ClaimsExtractorTests
{
    private readonly ClaimsExtractor _sut = new();

    // ── Helpers de construcción de ClaimsPrincipal ────────────────────────────

    private static ClaimsPrincipal BuildPrincipal(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.type, c.value)),
            "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal EmptyPrincipal() =>
        new(new ClaimsIdentity());

    // ════════════════════════════════════════════════════════════════
    // ExtractTenantId
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractTenantId_Succeeds_WithDirectClaim()
    {
        var tenantId = Guid.NewGuid();
        var principal = BuildPrincipal(("tenant_id", tenantId.ToString()));

        var result = _sut.ExtractTenantId(principal);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(tenantId);
    }

    [Fact]
    public void ExtractTenantId_Succeeds_WithAppMetadataClaim()
    {
        var tenantId = Guid.NewGuid();
        var principal = BuildPrincipal(("app_metadata.tenant_id", tenantId.ToString()));

        var result = _sut.ExtractTenantId(principal);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(tenantId);
    }

    [Fact]
    public void ExtractTenantId_Fails_WhenClaimMissing()
    {
        var result = _sut.ExtractTenantId(EmptyPrincipal());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(TenantContextErrorCode.MissingTenantId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("  ")]
    public void ExtractTenantId_Fails_WhenClaimIsInvalidGuid(string badValue)
    {
        var principal = BuildPrincipal(("tenant_id", badValue));

        var result = _sut.ExtractTenantId(principal);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(TenantContextErrorCode.InvalidTenantIdFormat);
    }

    [Fact]
    public void ExtractTenantId_PrioritizesDirectClaim_OverAppMetadata()
    {
        var direct   = Guid.NewGuid();
        var metadata = Guid.NewGuid();
        var principal = BuildPrincipal(
            ("tenant_id",              direct.ToString()),
            ("app_metadata.tenant_id", metadata.ToString()));

        var result = _sut.ExtractTenantId(principal);

        result.Value.Should().Be(direct, "el claim directo tiene prioridad sobre app_metadata");
    }

    // ════════════════════════════════════════════════════════════════
    // ExtractUserRole
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("therapist")]
    [InlineData("receptionist")]
    [InlineData("service")]
    public void ExtractUserRole_Succeeds_WithValidRole(string role)
    {
        var principal = BuildPrincipal(("user_role", role));

        var result = _sut.ExtractUserRole(principal);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(role);
    }

    [Fact]
    public void ExtractUserRole_ReturnsNull_WhenClaimAbsent()
    {
        var result = _sut.ExtractUserRole(EmptyPrincipal());

        result.Success.Should().BeTrue("ausencia de rol es válida");
        result.Value.Should().BeNull();
    }

    [Theory]
    [InlineData("superadmin")]
    [InlineData("ADMIN")]
    [InlineData("root")]
    public void ExtractUserRole_Fails_WithInvalidRole(string badRole)
    {
        var principal = BuildPrincipal(("user_role", badRole));

        var result = _sut.ExtractUserRole(principal);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(TenantContextErrorCode.InvalidRole);
    }

    // ════════════════════════════════════════════════════════════════
    // ExtractUserId
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractUserId_Succeeds_WithSubClaim()
    {
        var userId    = Guid.NewGuid();
        var principal = BuildPrincipal(("sub", userId.ToString()));

        var result = _sut.ExtractUserId(principal);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(userId);
    }

    [Fact]
    public void ExtractUserId_ReturnsNull_WhenSubAbsent()
    {
        var result = _sut.ExtractUserId(EmptyPrincipal());

        result.Success.Should().BeTrue("sub ausente es válido (service account)");
        result.Value.Should().BeNull();
    }

    [Fact]
    public void ExtractUserId_Fails_WithInvalidGuid()
    {
        var principal = BuildPrincipal(("sub", "not-a-guid"));

        var result = _sut.ExtractUserId(principal);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(TenantContextErrorCode.InvalidUserIdFormat);
    }
}
