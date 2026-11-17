using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace TenantHub.Infrastructure.Identity;

/// <summary>
/// Password reset and email verification tokens (D039). Identity's own flow does the work: <c>UserManager</c> makes
/// and checks the tokens, and the security stamp inside them makes a reset token single use (a reset changes the
/// stamp) and voids older ones. Identity's <c>DataProtectorTokenProvider</c> would do the same, but it reads the
/// system clock, so expiry could not be tested; this provider reads the injected <see cref="TimeProvider"/>. It also
/// carries the user id, so a link holds one opaque token and nothing else. No table: the token is its own record.
/// </summary>
internal sealed class AccountTokenProvider(IDataProtectionProvider protection, TimeProvider time) : IUserTwoFactorTokenProvider<AppUser>
{
    public const string ProviderName = "TenantHubAccount";

    public static readonly TimeSpan ResetLifespan = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan VerificationLifespan = TimeSpan.FromDays(7);

    private const byte Version = 1;

    private readonly IDataProtector _protector = protection.CreateProtector("TenantHub.AccountTokens", "v1");

    public async Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(time.GetUtcNow().UtcTicks);
            writer.Write(user.Id.ToByteArray());
            writer.Write(purpose);
            writer.Write(await manager.GetSecurityStampAsync(user));
        }

        return Base64Url.EncodeToString(_protector.Protect(buffer.ToArray()));
    }

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user)
    {
        if (Read(token) is not { } contents || contents.UserId != user.Id || contents.Purpose != purpose)
        {
            return false;
        }

        if (time.GetUtcNow().UtcTicks >= contents.CreatedTicks + LifespanOf(purpose).Ticks)
        {
            return false;
        }

        var stamp = Encoding.UTF8.GetBytes(await manager.GetSecurityStampAsync(user));
        return CryptographicOperations.FixedTimeEquals(stamp, Encoding.UTF8.GetBytes(contents.SecurityStamp));
    }

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);

    /// <summary>Whose token this is, if it is one of ours at all (the expiry and stamp are checked by <see cref="ValidateAsync"/>).</summary>
    public Guid? ReadUserId(string token) => Read(token)?.UserId;

    private static TimeSpan LifespanOf(string purpose) => purpose switch
    {
        UserManager<AppUser>.ResetPasswordTokenPurpose => ResetLifespan,
        UserManager<AppUser>.ConfirmEmailTokenPurpose => VerificationLifespan,
        _ => TimeSpan.Zero, // no other purpose is issued by this provider
    };

    private Contents? Read(string token)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(_protector.Unprotect(Base64Url.DecodeFromChars(token))), Encoding.UTF8);
            return reader.ReadByte() != Version
                ? null
                : new Contents(reader.ReadInt64(), new Guid(reader.ReadBytes(16)), reader.ReadString(), reader.ReadString());
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or EndOfStreamException or ArgumentException)
        {
            return null; // not base64url, not ours, altered, or cut short
        }
    }

    private sealed record Contents(long CreatedTicks, Guid UserId, string Purpose, string SecurityStamp);
}
