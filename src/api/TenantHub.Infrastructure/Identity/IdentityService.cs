using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;

namespace TenantHub.Infrastructure.Identity;

internal sealed class IdentityService(UserManager<AppUser> users, IDatabaseErrors databaseErrors, TimeProvider time)
    : IIdentityService
{
    private const string UserNameIndex = "ux_users_normalized_user_name";
    private const string EmailInUse = "An account with this email already exists. Sign in instead.";

    public async Task<CreateUserResult> CreateUserAsync(string email, string displayName, string password, CancellationToken cancellationToken)
    {
        var errors = ValidateDisplayName(displayName).ToList();
        var trimmedEmail = email.Trim();
        var user = new AppUser
        {
            UserName = trimmedEmail,
            Email = trimmedEmail,
            DisplayName = displayName.Trim(),
            CreatedAt = time.GetUtcNow(),
        };

        if (errors.Count == 0)
        {
            try
            {
                var created = await users.CreateAsync(user, password);
                errors.AddRange(created.Errors.Select(ToFieldError));
            }
            catch (DbUpdateException exception) when (databaseErrors.IsUniqueViolation(exception, UserNameIndex))
            {
                // Two sign-ups with the same email at once: UserManager's check passed for both, the index refused one.
                errors.Add(("email", EmailInUse));
            }
        }
        else
        {
            // Still report password problems together with the name problem.
            foreach (var validator in users.PasswordValidators)
            {
                var result = await validator.ValidateAsync(users, user, password);
                errors.AddRange(result.Errors.Select(ToFieldError));
            }
        }

        return errors.Count == 0 ? CreateUserResult.Success(user.Id) : CreateUserResult.Failure(FieldErrors.From(errors));
    }

    public async Task<UserProfile?> FindProfileAsync(Guid userId, CancellationToken cancellationToken) =>
        await users.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserProfile(u.Id, u.Email!, u.DisplayName))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, UserProfile>> FindProfilesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await users.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new UserProfile(u.Id, u.Email!, u.DisplayName))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email.Trim());
        return await users.Users
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<FieldErrors?> UpdateDisplayNameAsync(Guid userId, string displayName)
    {
        var errors = ValidateDisplayName(displayName).ToList();
        if (errors.Count > 0)
        {
            return FieldErrors.From(errors);
        }

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Signed-in user not found.");
        user.DisplayName = displayName.Trim();
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? null : FieldErrors.From(result.Errors.Select(ToFieldError));
    }

    public async Task<FieldErrors?> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Signed-in user not found.");
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            return null;
        }

        return FieldErrors.From(result.Errors.Select(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)
            ? ("currentPassword", "The current password is incorrect.")
            : ToFieldError(e) is ("password", var message) ? ("newPassword", message) : ToFieldError(e)));
    }

    private static IEnumerable<(string Field, string Message)> ValidateDisplayName(string displayName)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length is 0 or > AppUser.DisplayNameMaxLength)
        {
            yield return ("displayName", $"Enter a name of 1–{AppUser.DisplayNameMaxLength} characters.");
        }
    }

    private static (string Field, string Message) ToFieldError(IdentityError error) => error.Code switch
    {
        nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName) => ("email", EmailInUse),
        nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName) => ("email", "Enter a valid email address."),
        _ when error.Code.StartsWith("Password", StringComparison.Ordinal) => ("password", error.Description),
        _ => ("", error.Description),
    };
}
