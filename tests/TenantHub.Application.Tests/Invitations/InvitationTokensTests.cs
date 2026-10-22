using System.Buffers.Text;
using System.Security.Cryptography;
using TenantHub.Application.Invitations;

namespace TenantHub.Application.Tests.Invitations;

public sealed class InvitationTokensTests
{
    [Fact]
    public void A_token_is_32_random_bytes_in_base64url_and_only_its_sha256_is_kept()
    {
        var (token, hash) = InvitationTokens.Create();
        var (other, _) = InvitationTokens.Create();

        Assert.Equal(43, token.Length);
        Assert.Equal(32, Base64Url.DecodeFromChars(token).Length);
        Assert.Equal(SHA256.HashData(Base64Url.DecodeFromChars(token)), hash);
        Assert.Equal(hash, InvitationTokens.HashOf(token));
        Assert.NotEqual(token, other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("c2hvcnQ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Text_that_cannot_be_a_token_has_no_hash(string text) => Assert.Null(InvitationTokens.HashOf(text));

    [Theory]
    [InlineData("maya.torres@brightline.example", "m***@brightline.example")]
    [InlineData("x@a.example", "x***@a.example")]
    [InlineData("broken", "***")]
    public void Emails_are_masked_to_their_first_letter_and_domain(string email, string masked) =>
        Assert.Equal(masked, InvitationTokens.MaskEmail(email));
}
