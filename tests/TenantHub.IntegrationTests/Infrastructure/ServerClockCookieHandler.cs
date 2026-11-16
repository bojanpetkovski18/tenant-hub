using System.Net;
using Microsoft.Net.Http.Headers;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// A cookie jar like a browser whose clock agrees with the server's. The server writes a cookie's <c>expires</c> from
/// its own clock, which tests often pin to a fixed date; <see cref="CookieContainer"/> judges expiry by the machine's
/// real clock, so a cookie from a pinned date older than its lifetime would be dropped as soon as it arrived. Each
/// expiry is moved by the difference between the two clocks: the cookie keeps the lifetime the server gave it, and a
/// deletion (an expiry before the server's now) still deletes. The server keeps checking the ticket's own expiry.
/// </summary>
public sealed class ServerClockCookieHandler(CookieContainer cookies, TimeProvider serverClock) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var cookieHeader = cookies.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.Add(HeaderNames.Cookie, cookieHeader);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues(HeaderNames.SetCookie, out var setCookies))
        {
            var shift = DateTimeOffset.UtcNow - serverClock.GetUtcNow();
            foreach (var cookie in SetCookieHeaderValue.ParseList(setCookies.ToList()))
            {
                if (cookie.Expires is { } expires)
                {
                    cookie.Expires = expires + shift;
                }

                cookies.SetCookies(uri, cookie.ToString());
            }
        }

        return response;
    }
}
