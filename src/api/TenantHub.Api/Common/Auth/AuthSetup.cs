using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using TenantHub.Infrastructure;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Api.Common.Auth;

/// <summary>
/// Cookie authentication with ASP.NET Core Identity (D009). The SPA and API share an origin, so the session is an
/// <c>HttpOnly</c> cookie and JavaScript never sees a token; antiforgery protects unsafe requests.
/// </summary>
public static class AuthSetup
{
    public const string SessionCookieName = "th_session";
    private const string AntiforgeryCookieName = "th_af";

    public static WebApplicationBuilder AddAppAuth(this WebApplicationBuilder builder)
    {
        // Cookies are HTTPS-only outside Development. "Auth:RequireHttps=false" exists only for running the
        // production image locally over plain http (M11); never set it on a public deployment.
        var requireHttps = builder.Configuration.GetValue("Auth:RequireHttps", !builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(new CookieHttpsPolicy(requireHttps));
        var securePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

        builder.Services.AddAppIdentityCore().AddSignInManager();

        builder.Services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;

            // An API answers 401/403 (rendered as problem details) instead of redirecting to a login page.
            // Replacing Events drops Identity's default security-stamp hook, so it is wired back explicitly.
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,
                OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized),
                OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden),
            };
        });

        // Check the security stamp on every request, so a password change ends other sessions at once (one indexed
        // user lookup per authenticated request; Identity's default is every 30 minutes).
        builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

        // Every endpoint requires a signed-in user unless it opts out with AllowAnonymous().
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
        });
        builder.Services.AddScoped<XsrfCookie>();

        return builder;
    }

    public static WebApplication UseAppAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<AntiforgeryValidationMiddleware>();
        return app;
    }

    /// <summary>The signed-in user's id; only ever taken from the authenticated principal, never from input.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The endpoint requires an authenticated user.");

    private static Task SetStatus(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}

/// <summary>Whether auth cookies are HTTPS-only (set once in <see cref="AuthSetup"/>).</summary>
public sealed record CookieHttpsPolicy(bool RequireHttps);
