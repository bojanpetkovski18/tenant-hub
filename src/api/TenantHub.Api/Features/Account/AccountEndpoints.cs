using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Accounts;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Api.Features.Account;

public sealed record UpdateProfileRequest([property: Required(AllowEmptyStrings = true), MaxLength(200)] string DisplayName);

public sealed record ChangePasswordRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string CurrentPassword,
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string NewPassword);

/// <summary>The signed-in user's own account (US-04). The user is always the authenticated one, never an id from input.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").WithTags("Account");

        me.MapPatch("/", async (UpdateProfileRequest request, HttpContext context, UpdateProfileHandler handler, GetSessionQuery session, CancellationToken ct) =>
        {
            await handler.HandleAsync(new UpdateProfileCommand(context.User.GetUserId(), request.DisplayName));
            return TypedResults.Ok(await AuthEndpoints.ReadSessionAsync(context.User, session, ct));
        });

        me.MapPost("/password", async (
            ChangePasswordRequest request,
            HttpContext context,
            ChangePasswordHandler handler,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn) =>
        {
            var userId = context.User.GetUserId();
            await handler.HandleAsync(new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword));

            // The security stamp changed: re-issue this session's cookie so only the other sessions end.
            var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Signed-in user not found.");
            await signIn.RefreshSignInAsync(user);
            return TypedResults.NoContent();
        });

        return app;
    }
}
