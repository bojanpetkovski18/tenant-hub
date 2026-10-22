using System.Buffers.Text;
using System.Security.Cryptography;

namespace TenantHub.Application.Invitations;

/// <summary>
/// Invitation link tokens (SPEC 9, D011): 32 random bytes, Base64Url in the link; only their SHA-256 is stored.
/// A database leak therefore exposes no working link, and the token is never logged or audited.
/// </summary>
public static class InvitationTokens
{
    private const int TokenBytes = 32;

    public static (string Token, byte[] Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return (Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }

    /// <summary>The hash to look the token up by; null when the text cannot be a token (so nothing matches).</summary>
    public static byte[]? HashOf(string token)
    {
        if (token.Length > 64 || !Base64Url.IsValid(token))
        {
            return null;
        }

        var bytes = Base64Url.DecodeFromChars(token);
        return bytes.Length == TokenBytes ? SHA256.HashData(bytes) : null;
    }

    /// <summary>Compares a looked-up hash in constant time, as a second line behind the indexed lookup.</summary>
    public static bool Matches(byte[] storedHash, byte[] hash) => CryptographicOperations.FixedTimeEquals(storedHash, hash);

    /// <summary>"m***@brightline.example": enough to recognise, not enough to harvest addresses from links.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
