using Microsoft.AspNetCore.Antiforgery;

namespace TenantHub.Api.Common.Auth;

/// <summary>
/// Issues the JavaScript-readable <c>XSRF-TOKEN</c> cookie that Angular echoes back as <c>X-XSRF-TOKEN</c>.
/// Antiforgery tokens are bound to the current user, so this runs again whenever the user changes
/// (session load, sign-in, sign-up, sign-out) with <see cref="HttpContext.User"/> already updated.
/// </summary>
public sealed class XsrfCookie(IAntiforgery antiforgery, CookieHttpsPolicy https)
{
    public const string Name = "XSRF-TOKEN";

    public void Issue(HttpContext context)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(Name, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // Angular must read it; it is useless to an attacker without the HttpOnly antiforgery cookie
            SameSite = SameSiteMode.Strict,
            Secure = https.RequireHttps || context.Request.IsHttps,
            Path = "/",
        });
    }
}
