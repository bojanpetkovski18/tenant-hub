namespace TenantHub.Application.Abstractions;

/// <summary>
/// Account storage behind ASP.NET Core Identity, without exposing Identity types to the application layer.
/// Failures are returned as field errors keyed by the request's JSON property names.
/// </summary>
public interface IIdentityService
{
    /// <summary>Creates the account (email is also the user name) or returns field errors.</summary>
    Task<CreateUserResult> CreateUserAsync(string email, string displayName, string password, CancellationToken cancellationToken);

    Task<UserProfile?> FindProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Profiles by id, in one query; ids without an account are missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, UserProfile>> FindProfilesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Null on success, otherwise field errors.</summary>
    Task<FieldErrors?> UpdateDisplayNameAsync(Guid userId, string displayName);

    /// <summary>Null on success, otherwise field errors. Success rotates the security stamp (other sessions end).</summary>
    Task<FieldErrors?> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);
}

public sealed record UserProfile(Guid Id, string Email, string DisplayName);

public sealed record CreateUserResult(Guid? UserId, FieldErrors Errors)
{
    public static CreateUserResult Success(Guid userId) => new(userId, FieldErrors.None);

    public static CreateUserResult Failure(FieldErrors errors) => new(null, errors);
}
