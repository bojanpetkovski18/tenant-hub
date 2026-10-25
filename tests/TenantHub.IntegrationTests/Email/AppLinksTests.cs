using TenantHub.Infrastructure.Email;

namespace TenantHub.IntegrationTests.Email;

public sealed class AppLinksTests
{
    [Theory]
    [InlineData("https://app.tenanthub.example", "https://app.tenanthub.example/invite#abc")]
    [InlineData("https://app.tenanthub.example/", "https://app.tenanthub.example/invite#abc")]
    [InlineData("https://tenanthub.example/app", "https://tenanthub.example/app/invite#abc")]
    public void Invitation_links_keep_the_configured_base_and_carry_the_token_in_the_fragment(string publicUrl, string expected) =>
        Assert.Equal(expected, new AppLinks(new AppOptions { PublicUrl = new Uri(publicUrl) }).Invitation("abc"));
}
