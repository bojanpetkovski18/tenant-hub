using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Demo;
using TenantHub.Application.Portal;
using TenantHub.Infrastructure.Seed;

namespace TenantHub.Api.Features.Portal;

public sealed record PortalDemoSignInRequest([property: MaxLength(500)] string? Email);

public sealed record PortalWorkRequest(
    [property: MaxLength(500)] string? Title,
    [property: MaxLength(10000)] string? Description,
    DateOnly? PreferredOn);

public sealed record PortalDecisionRequest([property: MaxLength(5000)] string? Comment);

public sealed record PortalSignInRequest(
    [property: MaxLength(100)] string? OrgSlug,
    [property: MaxLength(500)] string? Email);

/// <summary>The link names its portal (<c>/portal/{slug}/sign-in#token</c>); the slug says which organization to look in.</summary>
public sealed record PortalConfirmRequest(
    [property: MaxLength(100)] string? OrgSlug,
    [property: MaxLength(200)] string? Token);

/// <summary>
/// The portal's public endpoints (SPEC-V2 7): asking for a link, spending it, the session and signing out. Everything a
/// signed-in contact does is under <see cref="EndpointAccessExtensions.MapPortalGroup"/>.
/// </summary>
public static class PortalEndpoints
{
    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder app)
    {
        // Always 202 with no body: the answer reveals nothing about the email or the organization.
        app.MapPost($"{PortalAuth.ApiPath}/sign-in/request", async (PortalSignInRequest request, PortalSignIn signIn, CancellationToken ct) =>
            {
                await signIn.RequestAsync(request.OrgSlug, request.Email, ct);
                return TypedResults.Accepted((string?)null);
            })
            .WithTags("Portal")
            .Public()
            .RequireRateLimiting(RateLimiting.PortalSignInPolicy);

        // The token in the body is the credential (D028); a link signs in once.
        app.MapPost($"{PortalAuth.ApiPath}/sign-in/confirm", async (
                PortalConfirmRequest request, HttpContext context, PortalSignIn signIn, XsrfCookie xsrf, CancellationToken ct) =>
            {
                var (identity, session) = await signIn.ConfirmAsync(request.OrgSlug, request.Token, ct);
                await PortalAuth.SignInAsync(context, identity);
                xsrf.Issue(context, PortalAuth.PagesPath);
                return TypedResults.Ok(session);
            })
            .WithTags("Portal")
            .Public()
            .RequireRateLimiting(RateLimiting.InvitationPolicy);

        // Public like the staff session: who is signed in to this portal, or which portal the sign-in page is for.
        app.MapGet($"{PortalAuth.ApiPath}/session", async (string? orgSlug, HttpContext context, PortalSessions sessions, XsrfCookie xsrf, CancellationToken ct) =>
            {
                var session = await CurrentSessionAsync(context, sessions, orgSlug, ct);
                xsrf.Issue(context, PortalAuth.PagesPath);
                return TypedResults.Ok(session);
            })
            .WithTags("Portal")
            .Public();

        // The public demo only (404 otherwise, as if it did not exist): sign in as one of the seeded contacts without a
        // link, since the demo sends no email (D040). Any other address is refused, so a contact a visitor added is safe.
        app.MapPost($"{PortalAuth.ApiPath}/demo/sign-in", async Task<IResult> (
                PortalDemoSignInRequest request, HttpContext context, DemoMode mode, PortalSignIn signIn, XsrfCookie xsrf, CancellationToken ct) =>
            {
                if (!mode.Enabled)
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
                }

                if (string.IsNullOrWhiteSpace(request.Email)
                    || await signIn.SignInDemoContactAsync(DemoSeeder.BrightlineSlug, request.Email, ct) is not { } signedIn)
                {
                    return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose one of the demo clients.");
                }

                await PortalAuth.SignInAsync(context, signedIn.Identity);
                xsrf.Issue(context, PortalAuth.PagesPath);
                return TypedResults.Ok(signedIn.Session);
            })
            .WithTags("Portal")
            .Public()
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        app.MapPost($"{PortalAuth.ApiPath}/sign-out", async (HttpContext context, XsrfCookie xsrf) =>
            {
                await PortalAuth.SignOutAsync(context);
                xsrf.Issue(context, PortalAuth.PagesPath);
                return TypedResults.NoContent();
            })
            .WithTags("Portal")
            .Public();

        // A signed-in contact (SPEC-V2 7, portal-scoped): the client comes from the session, never from the request.
        var portal = app.MapPortalGroup().WithTags("Portal");

        portal.MapGet("/visits", async (string? when, int? page, int? pageSize, PortalVisits visits, CancellationToken ct) =>
            TypedResults.Ok(await visits.ListAsync(when, page, pageSize, ct)));

        portal.MapGet("/visits/{id:guid}", async (Guid id, PortalVisits visits, CancellationToken ct) =>
            TypedResults.Ok(await visits.GetAsync(id, ct)));

        portal.MapGet("/requests", async (PortalRequests requests, CancellationToken ct) =>
            TypedResults.Ok(await requests.ListAsync(ct)));

        portal.MapPost("/requests", async (PortalWorkRequest request, PortalRequests requests, CancellationToken ct) =>
        {
            var created = await requests.SubmitAsync(request.Title, request.Description, request.PreferredOn, ct);
            return TypedResults.Created($"{PortalAuth.ApiPath}/requests/{created.Id}", created);
        });

        portal.MapGet("/quotes", async (PortalQuotes quotes, CancellationToken ct) =>
            TypedResults.Ok(await quotes.ListAsync(ct)));

        portal.MapPost("/quotes/{id:guid}/approve", async (Guid id, PortalDecisionRequest request, PortalQuotes quotes, CancellationToken ct) =>
            TypedResults.Ok(await quotes.DecideAsync(id, approve: true, request.Comment, ct)));

        portal.MapPost("/quotes/{id:guid}/decline", async (Guid id, PortalDecisionRequest request, PortalQuotes quotes, CancellationToken ct) =>
            TypedResults.Ok(await quotes.DecideAsync(id, approve: false, request.Comment, ct)));

        return app;
    }

    /// <summary>
    /// A session that has ended (contact removed, client archived, portal off) is cleared here too. A contact who opens
    /// another organization's portal is signed out of nothing, but that portal shows its sign-in page.
    /// </summary>
    private static async Task<PortalSessionView> CurrentSessionAsync(HttpContext context, PortalSessions sessions, string? orgSlug, CancellationToken ct)
    {
        if (context.User.GetPortalIdentity() is { } identity)
        {
            if (await sessions.TryEnterAsync(identity, ct))
            {
                var current = await sessions.CurrentAsync(ct);
                if (string.IsNullOrWhiteSpace(orgSlug) || string.Equals(current.Organization?.Slug, orgSlug.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }
            }
            else
            {
                await PortalAuth.SignOutAsync(context);
            }
        }

        return await sessions.SignedOutAsync(orgSlug, ct);
    }
}
