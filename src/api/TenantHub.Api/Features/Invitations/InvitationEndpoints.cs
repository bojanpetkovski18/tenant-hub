using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Accounts;
using TenantHub.Application.Invitations;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Api.Features.Invitations;

public sealed record InviteRequest([property: Required(AllowEmptyStrings = true), MaxLength(254)] string Email, Role? Role);

/// <summary>The token travels in the body, never in the URL, so it cannot reach request logs (D028).</summary>
public sealed record InvitationTokenRequest([property: Required, MaxLength(64)] string Token);

public sealed record InvitationSignUpRequest(
    [property: Required, MaxLength(64)] string Token,
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string DisplayName,
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string Password);

public sealed record AcceptInvitationResponse(Guid OrganizationId, bool AlreadyMember, SessionResponse Session);

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        // Tenant-scoped management (US-10).
        var invitations = app.MapTenantGroup().MapGroup("/invitations").WithTags("Invitations");

        invitations.MapGet("/", async (InvitationCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ListAsync(ct)))
            .RequirePermission(Permissions.MembersManage);

        invitations.MapPost("/", async (InviteRequest request, InvitationCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.InviteAsync(new InviteCommand(request.Email, request.Role), ct)))
            .RequirePermission(Permissions.MembersManage)
            .RequireRateLimiting(RateLimiting.InvitationPolicy);

        invitations.MapPost("/{id:guid}/resend", async (Guid id, InvitationCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ResendAsync(id, ct)))
            .RequirePermission(Permissions.MembersManage)
            .RequireRateLimiting(RateLimiting.InvitationPolicy);

        invitations.MapDelete("/{id:guid}", async (Guid id, InvitationCommands commands, CancellationToken ct) =>
            {
                await commands.RevokeAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.MembersManage);

        // By link (US-11). POST even for the preview: the token belongs in a body, not a URL.
        var byLink = app.MapGroup("/api/invitations").WithTags("Invitations").RequireRateLimiting(RateLimiting.InvitationPolicy);

        byLink.MapPost("/preview", async (InvitationTokenRequest request, HttpContext context, InvitationAcceptance acceptance, CancellationToken ct) =>
            {
                Guid? userId = context.User.Identity?.IsAuthenticated == true ? context.User.GetUserId() : null;
                return TypedResults.Ok(await acceptance.PreviewAsync(request.Token, userId, ct));
            })
            .Public();

        byLink.MapPost("/sign-up", async (
                InvitationSignUpRequest request,
                HttpContext context,
                InvitationAcceptance acceptance,
                UserManager<AppUser> users,
                SignInManager<AppUser> signIn,
                XsrfCookie xsrf,
                GetSessionQuery session,
                CancellationToken ct) =>
            {
                var result = await acceptance.SignUpAsync(new InvitationSignUpCommand(request.Token, request.DisplayName, request.Password), ct);
                var user = await users.FindByIdAsync(result.UserId.ToString()) ?? throw new InvalidOperationException("New user not found.");
                await AuthEndpoints.StartSessionAsync(context, user, signIn, xsrf);
                return TypedResults.Ok(new AcceptInvitationResponse(
                    result.OrganizationId, result.AlreadyMember, await AuthEndpoints.ReadSessionAsync(context.User, session, ct)));
            })
            .Public();

        byLink.MapPost("/accept", async (
                InvitationTokenRequest request,
                HttpContext context,
                InvitationAcceptance acceptance,
                GetSessionQuery session,
                CancellationToken ct) =>
            {
                var result = await acceptance.AcceptAsync(request.Token, context.User.GetUserId(), ct);
                return TypedResults.Ok(new AcceptInvitationResponse(
                    result.OrganizationId, result.AlreadyMember, await AuthEndpoints.ReadSessionAsync(context.User, session, ct)));
            })
            .UserScoped();

        return app;
    }
}
