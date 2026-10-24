using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Accounts;
using TenantHub.Application.Demo;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Identity;
using TenantHub.Infrastructure.Seed;

namespace TenantHub.Api.Features.Demo;

public sealed record DemoAccountResponse(Role Role, string DisplayName, string Email);

/// <summary>What the sign-in page and the demo banner show (US-61).</summary>
public sealed record DemoInfoResponse(int ResetIntervalHours, DateTimeOffset? NextResetAt, IReadOnlyList<DemoAccountResponse> Accounts);

public sealed record DemoSignInRequest(Role? Role);

/// <summary>
/// The public demo's one-click sign-in (SPEC 4.1). Always mapped, so the endpoint coverage tests see them, and 404
/// outside demo mode as if they did not exist. Signing in as a role needs no password: the accounts are public by
/// design and protected from changes that would spoil the demo for the next visitor (<see cref="DemoProtection"/>).
/// </summary>
public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/api/demo").WithTags("Demo");

        demo.MapGet("/accounts", async Task<Results<Ok<DemoInfoResponse>, ProblemHttpResult>> (DemoMode mode, DemoReset reset, CancellationToken ct) =>
            mode.Enabled
                ? TypedResults.Ok(new DemoInfoResponse(
                    mode.ResetIntervalHours,
                    await reset.NextResetAtAsync(ct),
                    [.. DemoSeeder.DemoAccounts.Select(a => new DemoAccountResponse(a.Role, a.DisplayName, a.Email))]))
                : NotFound())
            .Public();

        demo.MapPost("/sign-in", SignInAsync)
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        return app;
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SignInAsync(
        DemoSignInRequest request,
        HttpContext context,
        DemoMode mode,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        XsrfCookie xsrf,
        GetSessionQuery session,
        CancellationToken ct)
    {
        if (!mode.Enabled)
        {
            return NotFound();
        }

        if (DemoSeeder.DemoAccounts.FirstOrDefault(a => a.Role == request.Role) is not { } account)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Choose a demo role: owner, admin, member or viewer.");
        }

        if (await users.FindByEmailAsync(account.Email) is not { } user)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The demo is being prepared. Try again in a minute.");
        }

        await AuthEndpoints.StartSessionAsync(context, user, signIn, xsrf);
        return TypedResults.Ok(await AuthEndpoints.ReadSessionAsync(context.User, session, ct));
    }

    private static ProblemHttpResult NotFound() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
}
