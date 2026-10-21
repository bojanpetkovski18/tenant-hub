using TenantHub.Application.Abstractions;

namespace TenantHub.Application.Accounts;

public sealed record UpdateProfileCommand(Guid UserId, string DisplayName);

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword);

/// <summary>Changes the display name (US-04).</summary>
public sealed class UpdateProfileHandler(IIdentityService identity)
{
    public async Task HandleAsync(UpdateProfileCommand command)
    {
        if (await identity.UpdateDisplayNameAsync(command.UserId, command.DisplayName) is { } errors)
        {
            throw new RequestValidationException(errors);
        }
    }
}

/// <summary>Changes the password; the new security stamp signs out every other session (US-04).</summary>
public sealed class ChangePasswordHandler(IIdentityService identity)
{
    public async Task HandleAsync(ChangePasswordCommand command)
    {
        if (await identity.ChangePasswordAsync(command.UserId, command.CurrentPassword, command.NewPassword) is { } errors)
        {
            throw new RequestValidationException(errors);
        }
    }
}
