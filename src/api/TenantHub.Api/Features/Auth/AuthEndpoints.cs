using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Accounts;
using TenantHub.Application.Demo;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Api.Features.Auth;

public static class AuthEndpoints
{
    private static readonly AppUser TimingDummy = new() { DisplayName = "-" };

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        // Also how the SPA gets its XSRF cookie on startup, so it is public.
        auth.MapGet("/session", async (HttpContext context, XsrfCookie xsrf, GetSessionQuery session, DemoMode demo, CancellationToken ct) =>
            {
                xsrf.Issue(context);
                return TypedResults.Ok(await ReadSessionAsync(context.User, session, ct) with { DemoMode = demo.Enabled });
            })
            .Public();

        auth.MapPost("/sign-up", SignUpAsync)
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        auth.MapPost("/sign-in", SignInAsync)
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        // Always 202 with no body: the answer must not tell whether the email has an account (SPEC-V2 4.1).
        auth.MapPost("/password-reset/request", async (PasswordResetRequest request, RequestPasswordResetHandler handler, CancellationToken ct) =>
            {
                await handler.HandleAsync(request.Email, ct);
                return TypedResults.Accepted((string?)null);
            })
            .Public()
            .RequireRateLimiting(RateLimiting.AccountEmailPolicy);

        // The token in the body is the credential, as for invitations (D028). It ends every session of the account.
        auth.MapPost("/password-reset/confirm", async (PasswordResetConfirmRequest request, ResetPasswordHandler handler, CancellationToken ct) =>
            {
                await handler.HandleAsync(request.Token, request.Password, ct);
                return TypedResults.NoContent();
            })
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        // Public: the link may be opened in a browser where nobody, or somebody else, is signed in. Limited like the
        // other links that carry a token (invitations), not like sign-in: no password is checked and nothing is sent.
        auth.MapPost("/verify-email", async (VerifyEmailRequest request, EmailVerification verification, CancellationToken ct) =>
            {
                await verification.VerifyAsync(request.Token, ct);
                return TypedResults.NoContent();
            })
            .Public()
            .RequireRateLimiting(RateLimiting.InvitationPolicy);

        auth.MapPost("/sign-out", async (HttpContext context, SignInManager<AppUser> signIn, XsrfCookie xsrf) =>
        {
            await signIn.SignOutAsync();
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            xsrf.Issue(context);
            return TypedResults.NoContent();
        })
        .UserScoped();

        return app;
    }

    internal static async Task<SessionResponse> ReadSessionAsync(ClaimsPrincipal principal, GetSessionQuery session, CancellationToken ct) =>
        principal.Identity?.IsAuthenticated == true && await session.HandleAsync(principal.GetUserId(), ct) is { } user
            ? new SessionResponse(UserResponse.From(user))
            : new SessionResponse(null);

    private static async Task<Created<SessionResponse>> SignUpAsync(
        SignUpRequest request,
        HttpContext context,
        SignUpHandler handler,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        XsrfCookie xsrf,
        GetSessionQuery session,
        CancellationToken ct)
    {
        var userId = await handler.HandleAsync(
            new SignUpCommand(request.DisplayName, request.Email, request.Password, request.OrganizationName, request.TimeZone),
            ct);

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("New user not found.");
        await StartSessionAsync(context, user, signIn, xsrf);
        return TypedResults.Created("/api/auth/session", await ReadSessionAsync(context.User, session, ct));
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SignInAsync(
        SignInRequest request,
        HttpContext context,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        XsrfCookie xsrf,
        GetSessionQuery session,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Spend the same hashing time as a real check, so response times do not reveal which emails exist.
            users.PasswordHasher.HashPassword(TimingDummy, request.Password);
            return InvalidCredentials();
        }

        // Locked-out accounts get the same answer as wrong passwords: no account enumeration.
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return InvalidCredentials();
        }

        await StartSessionAsync(context, user, signIn, xsrf);
        return TypedResults.Ok(await ReadSessionAsync(context.User, session, ct));
    }

    internal static async Task StartSessionAsync(HttpContext context, AppUser user, SignInManager<AppUser> signIn, XsrfCookie xsrf)
    {
        await signIn.SignInAsync(user, isPersistent: true);
        // The cookie only applies from the next request; this one continues as the new user for the XSRF token.
        context.User = await signIn.CreateUserPrincipalAsync(user);
        xsrf.Issue(context);
    }

    private static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.");
}
