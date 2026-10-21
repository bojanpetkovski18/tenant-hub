using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Organizations;

public sealed class OrganizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");

    [Fact]
    public void Create_sets_the_defaults_from_the_spec()
    {
        var organization = Organization.Create("Brightline Cleaning Co.", "brightline-cleaning-co", "Europe/Amsterdam", UserId, Now);

        Assert.NotEqual(Guid.Empty, organization.Id);
        Assert.Equal("Brightline Cleaning Co.", organization.Name);
        Assert.Equal("brightline-cleaning-co", organization.Slug);
        Assert.Equal(Plan.Free, organization.Plan);
        Assert.Equal("Europe/Amsterdam", organization.TimeZone);
        Assert.Equal(1, organization.NextJobNumber);
        Assert.Equal(Now, organization.CreatedAt);
        Assert.Equal(UserId, organization.CreatedByUserId);
        Assert.False(organization.IsDemoSeed);
    }

    [Theory]
    [InlineData("  Acme   Ltd ", "Acme Ltd")]
    [InlineData("AB", "AB")]
    public void Name_whitespace_is_normalized(string name, string expected) =>
        Assert.Equal(expected, Organization.Create(name, "acme", "UTC", UserId, Now).Name);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData(" A ")]
    public void Names_shorter_than_two_characters_are_rejected(string name)
    {
        var error = Assert.Throws<DomainException>(() => Organization.Create(name, "acme", "UTC", UserId, Now));

        Assert.Equal("invalid-organization-name", error.Code);
        Assert.Equal(DomainErrorKind.Validation, error.Kind);
    }

    [Fact]
    public void Names_longer_than_eighty_characters_are_rejected()
    {
        Assert.Equal(80, Organization.Create(new string('x', 80), "acme", "UTC", UserId, Now).Name.Length);
        Assert.Throws<DomainException>(() => Organization.Create(new string('x', 81), "acme", "UTC", UserId, Now));
    }

    [Theory]
    [InlineData("Europe/Amsterdam")]
    [InlineData("America/New_York")]
    [InlineData("Asia/Kathmandu")]
    [InlineData("UTC")]
    public void IANA_time_zones_are_accepted(string timeZone) =>
        Assert.Equal(timeZone, Organization.Create("Acme", "acme", timeZone, UserId, Now).TimeZone);

    [Theory]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Central European Standard Time")]
    [InlineData("europe/amsterdam ")]
    public void Unknown_and_Windows_time_zone_ids_are_rejected(string timeZone)
    {
        var error = Assert.Throws<DomainException>(() => Organization.Create("Acme", "acme", timeZone, UserId, Now));

        Assert.Equal("invalid-time-zone", error.Code);
    }

    [Fact]
    public void An_invalid_slug_is_a_programming_error() =>
        Assert.Throws<ArgumentException>(() => Organization.Create("Acme", "api", "UTC", UserId, Now));

    [Fact]
    public void AddOwner_creates_an_active_owner_membership_of_this_organization()
    {
        var organization = Organization.Create("Acme", "acme", "UTC", UserId, Now);

        var membership = organization.AddOwner(UserId, Now);

        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(UserId, membership.UserId);
        Assert.Equal(Role.Owner, membership.Role);
        Assert.Equal(Now, membership.JoinedAt);
        Assert.Null(membership.InvitedByUserId);
        Assert.True(membership.IsActive);
    }
}
