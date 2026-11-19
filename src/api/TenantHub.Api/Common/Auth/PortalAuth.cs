using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TenantHub.Application.Portal;

namespace TenantHub.Api.Common.Auth;

/// <summary>
/// The portal's own sign-in (SPEC-V2 5.3, D040): a cookie scheme separate from the staff one, with its own name, scoped
/// to <c>/api/portal</c>, valid for 7 days. A staff cookie never authenticates a portal request and a portal cookie
/// never reaches a staff endpoint: <see cref="PortalPrincipalMiddleware"/> gives portal paths the portal identity only.
/// </summary>
public static class PortalAuth
{
    public const string Scheme = "Portal";
    public const string CookieName = "th_portal";
    public const string ApiPath = "/api/portal";

    /// <summary>Where the portal's XSRF-TOKEN cookie lives: the portal pages read it, the staff pages never see it.</summary>
    public const string PagesPath = "/portal";

    public const string Policy = "portal";

    private const string OrganizationClaim = "th:portal:organization";
    private const string ClientClaim = "th:portal:client";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static void AddPortalAuth(this IServiceCollection services, CookieSecurePolicy securePolicy)
    {
        services.AddAuthentication().AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.Path = ApiPath;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = Lifetime;
            options.SlidingExpiration = false;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden),
            };
        });

        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(OrganizationClaim)
            .RequireClaim(ClientClaim)
            .RequireClaim(ClaimTypes.NameIdentifier));
    }

    public static ClaimsPrincipal PrincipalFor(PortalIdentity identity) => new(new ClaimsIdentity(
        [
            // The contact id as the name identifier, so antiforgery tokens are bound to the contact.
            new Claim(ClaimTypes.NameIdentifier, identity.ContactId.ToString()),
            new Claim(OrganizationClaim, identity.OrganizationId.ToString()),
            new Claim(ClientClaim, identity.ClientId.ToString()),
        ],
        Scheme));

    /// <summary>The session's contact, client and organization; null when the principal is not a portal session.</summary>
    public static PortalIdentity? GetPortalIdentity(this ClaimsPrincipal principal) =>
        principal.Identity?.AuthenticationType == Scheme
            && Guid.TryParse(principal.FindFirstValue(OrganizationClaim), out var organization)
            && Guid.TryParse(principal.FindFirstValue(ClientClaim), out var client)
            && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var contact)
            ? new PortalIdentity(organization, client, contact)
            : null;

    public static async Task SignInAsync(HttpContext context, PortalIdentity identity)
    {
        var principal = PrincipalFor(identity);
        await context.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = true });
        context.User = principal;
    }

    public static async Task SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(Scheme);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
    }

    private static Task SetStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Portal requests (<c>/api/portal</c>) are the portal session or nobody: the staff identity that the default scheme
/// found is replaced before authorization and antiforgery run. So a staff cookie cannot authorize a portal endpoint,
/// and the portal's antiforgery tokens are bound to the contact (D040).
/// </summary>
public sealed class PortalPrincipalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(PortalAuth.ApiPath))
        {
            var result = await context.AuthenticateAsync(PortalAuth.Scheme);
            context.User = result.Succeeded ? result.Principal : new ClaimsPrincipal(new ClaimsIdentity());
        }

        await next(context);
    }
}
