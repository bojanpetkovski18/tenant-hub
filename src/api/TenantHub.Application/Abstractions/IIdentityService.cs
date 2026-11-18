namespace TenantHub.Application.Abstractions;

/// <summary>
/// Account storage behind ASP.NET Core Identity, without exposing Identity types to the application layer.
/// Failures are returned as field errors keyed by the request's JSON property names.
/// </summary>
public interface IIdentityService
{
    /// <summary>
    /// Creates the account (email is also the user name) or returns field errors. <paramref name="emailVerified"/> is
    /// true only when the address has already been proven, as by an invitation link sent to it.
    /// </summary>
    Task<CreateUserResult> CreateUserAsync(string email, string displayName, string password, bool emailVerified, CancellationToken cancellationToken);

    Task<UserProfile?> FindProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Profiles by id, in one query; ids without an account are missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, UserProfile>> FindProfilesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>The account with this email (compared the way Identity normalizes it), if any.</summary>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Null on success, otherwise field errors.</summary>
    Task<FieldErrors?> UpdateDisplayNameAsync(Guid userId, string displayName);

    /// <summary>Null on success, otherwise field errors. Success rotates the security stamp (other sessions end).</summary>
    Task<FieldErrors?> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);

    /// <summary>
    /// A password reset token for the account, or null when one was made less than <paramref name="cooldown"/> ago
    /// (so a stranger cannot flood an inbox). Valid for 30 minutes and only until the password changes.
    /// </summary>
    Task<string?> CreatePasswordResetTokenAsync(Guid userId, TimeSpan cooldown, CancellationToken cancellationToken);

    /// <summary>Sets the new password if the token is valid; success rotates the security stamp (every session ends).</summary>
    Task<PasswordResetResult> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken);

    /// <summary>An email verification token for the account, valid for 7 days.</summary>
    Task<string> CreateEmailVerificationTokenAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Marks the account's email verified if the token is valid; false when it is not (or has expired).</summary>
    Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken);
}

/// <summary><c>EmailVerified</c>: the owner of the account has proven the address (SPEC-V2 4.1).</summary>
public sealed record UserProfile(Guid Id, string Email, string DisplayName, bool EmailVerified);

public enum PasswordResetOutcome
{
    Reset,

    /// <summary>Unknown, expired, already used, or made before the password last changed.</summary>
    InvalidToken,

    /// <summary>The new password breaks the password rules; see <see cref="PasswordResetResult.Errors"/>.</summary>
    Rejected,
}

public sealed record PasswordResetResult(PasswordResetOutcome Outcome, FieldErrors? Errors = null);

public sealed record CreateUserResult(Guid? UserId, FieldErrors Errors)
{
    public static CreateUserResult Success(Guid userId) => new(userId, FieldErrors.None);

    public static CreateUserResult Failure(FieldErrors errors) => new(null, errors);
}
