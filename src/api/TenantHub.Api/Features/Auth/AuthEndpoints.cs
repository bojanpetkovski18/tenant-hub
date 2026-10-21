using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Accounts;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Api.Features.Auth;

public static class AuthEndpoints
{
    private static readonly AppUser TimingDummy = new() { DisplayName = "-" };

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        // Also how the SPA gets its XSRF cookie on startup, so it is public.
        auth.MapGet("/session", async (HttpContext context, XsrfCookie xsrf, GetSessionQuery session, CancellationToken ct) =>
            {
                xsrf.Issue(context);
                return TypedResults.Ok(await ReadSessionAsync(context.User, session, ct));
            })
            .Public();

        auth.MapPost("/sign-up", SignUpAsync)
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        auth.MapPost("/sign-in", SignInAsync)
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

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

    private static async Task StartSessionAsync(HttpContext context, AppUser user, SignInManager<AppUser> signIn, XsrfCookie xsrf)
    {
        await signIn.SignInAsync(user, isPersistent: true);
        // The cookie only applies from the next request; this one continues as the new user for the XSRF token.
        context.User = await signIn.CreateUserPrincipalAsync(user);
        xsrf.Issue(context);
    }

    private static ProblemHttpResult InvalidCredentials() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.");
}
