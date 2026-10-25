using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Organizations;

public sealed class OrganizationSlugTests
{
    [Theory]
    [InlineData("Brightline Cleaning Co.", "brightline-cleaning-co")]
    [InlineData("Harbor & Pine Landscaping", "harbor-pine-landscaping")]
    [InlineData("  Café   Ünïon  ", "cafe-union")]
    [InlineData("ACME 24/7", "acme-24-7")]
    [InlineData("--Dash--Land--", "dash-land")]
    [InlineData("Ab", "ab-org")]
    [InlineData("API", "api-org")]
    [InlineData("Settings", "settings-org")]
    [InlineData("O", "o-org")]
    [InlineData("株式会社", "organization")]
    public void Base_slug_is_derived_from_the_name(string name, string expected) =>
        Assert.Equal(expected, OrganizationSlug.Candidates(name).First());

    [Fact]
    public void Long_names_are_cut_to_the_maximum_without_a_trailing_hyphen()
    {
        var slug = OrganizationSlug.Candidates("Northern Lights Residential and Commercial Window Cleaning").First();

        Assert.True(slug.Length <= OrganizationSlug.MaxLength);
        Assert.Equal("northern-lights-residential-and-commerci", slug);
        Assert.True(OrganizationSlug.IsValid(slug));
    }

    [Fact]
    public void Collision_candidates_are_numbered_and_stay_within_the_maximum_length()
    {
        var candidates = OrganizationSlug.Candidates(new string('a', 60), count: 12).ToList();

        Assert.Equal(12, candidates.Count);
        Assert.Equal(new string('a', 40), candidates[0]);
        Assert.Equal(new string('a', 38) + "-2", candidates[1]);
        Assert.Equal(new string('a', 37) + "-12", candidates[11]);
        Assert.All(candidates, slug => Assert.True(OrganizationSlug.IsValid(slug), slug));
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    [Fact]
    public void Numbered_candidates_do_not_end_up_with_a_double_hyphen()
    {
        // 39 characters then a hyphen: cutting for "-2" must not leave "…--2".
        var name = new string('b', 37) + " cd";

        var second = OrganizationSlug.Candidates(name).Skip(1).First();

        Assert.True(OrganizationSlug.IsValid(second), second);
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("brightline-cleaning-co", true)]
    [InlineData("ab", false)]
    [InlineData("api", false)]
    [InlineData("Brightline", false)]
    [InlineData("-abc", false)]
    [InlineData("abc-", false)]
    [InlineData("a--b", false)]
    [InlineData("a_bc", false)]
    public void Validity_follows_the_slug_rules(string slug, bool expected) =>
        Assert.Equal(expected, OrganizationSlug.IsValid(slug));
}
