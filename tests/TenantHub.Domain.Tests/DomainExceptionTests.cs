using TenantHub.Domain;

namespace TenantHub.Domain.Tests;

public sealed class DomainExceptionTests
{
    [Theory]
    [InlineData("last-owner")]
    [InlineData("plan-limit-reached")]
    [InlineData("invalid-transition")]
    [InlineData("a")]
    [InlineData("error2")]
    public void Kebab_case_codes_are_accepted(string code)
    {
        var exception = new DomainException(code, "Message.");

        Assert.Equal(code, exception.Code);
        Assert.Equal("Message.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Last-Owner")]
    [InlineData("last_owner")]
    [InlineData("last owner")]
    [InlineData("-last-owner")]
    [InlineData("last-owner-")]
    [InlineData("last--owner")]
    [InlineData("2fa-required")]
    public void Codes_that_are_not_kebab_case_are_rejected(string code) =>
        Assert.Throws<ArgumentException>(() => new DomainException(code, "Message."));

    [Fact]
    public void Codes_longer_than_64_characters_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new DomainException(new string('a', 65), "Message."));
}
