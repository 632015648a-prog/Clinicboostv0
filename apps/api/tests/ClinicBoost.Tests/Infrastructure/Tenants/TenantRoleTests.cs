using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;

namespace ClinicBoost.Tests.Infrastructure.Tenants;

public sealed class TenantRoleTests
{
    // ── IsValid ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("therapist")]
    [InlineData("receptionist")]
    [InlineData("service")]
    public void IsValid_ReturnsTrue_ForKnownRoles(string role)
    {
        TenantRole.IsValid(role).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("superadmin")]
    [InlineData("OWNER")]           // case-sensitive
    [InlineData("root")]
    [InlineData("manager")]
    public void IsValid_ReturnsFalse_ForUnknownOrNullRoles(string? role)
    {
        TenantRole.IsValid(role).Should().BeFalse();
    }

    // ── HasAtLeast ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("owner",        "owner",        true)]
    [InlineData("owner",        "admin",        true)]
    [InlineData("owner",        "therapist",    true)]
    [InlineData("owner",        "receptionist", true)]
    [InlineData("owner",        "service",      true)]
    [InlineData("admin",        "admin",        true)]
    [InlineData("admin",        "therapist",    true)]
    [InlineData("admin",        "owner",        false)]
    [InlineData("therapist",    "therapist",    true)]
    [InlineData("therapist",    "receptionist", false)]
    [InlineData("therapist",    "admin",        false)]
    [InlineData("receptionist", "receptionist", true)]
    [InlineData("receptionist", "service",      true)]
    [InlineData("receptionist", "therapist",    false)]
    [InlineData("service",      "service",      true)]
    [InlineData("service",      "therapist",    false)]
    [InlineData(null,           "service",      false)]
    [InlineData("unknown",      "service",      false)]
    public void HasAtLeast_MatchesHierarchy(string? user, string min, bool expected)
    {
        TenantRole.HasAtLeast(user, min).Should().Be(expected);
    }
}
