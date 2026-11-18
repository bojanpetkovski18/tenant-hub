using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Common.Auth;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// Portal sign-in and sessions (US-81, SPEC-V2 5.3, criterion 13): the link works once for 15 minutes and never reaches
/// a log; asking for one answers the same for everyone and is rate-limited per email and per address; the session is
/// its own cookie, and removing the contact, archiving the client or losing the portal ends it on the next request.
/// </summary>
public sealed class PortalSessionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_signs_in_once_and_its_token_is_in_no_log()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var browser = new BrowserClient(tenants.Factory);
        var token = await tenants.RequestLinkAsync(browser, tenants.A1);

        var first = await Confirm(browser, tenants.A1, token);
        using var other = new BrowserClient(tenants.Factory);
        var second = await Confirm(other, tenants.A1, token);
        var visits = await browser.Http.GetAsync("/api/portal/visits", Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var session = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(tenants.A1.ContactEmail, session.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal(tenants.A1.Name, session.GetProperty("contact").GetProperty("clientName").GetString());
        await ProblemAssert.HasCodeAsync(second, HttpStatusCode.BadRequest, "portal-link-invalid");
        Assert.Equal(HttpStatusCode.OK, visits.StatusCode);
        Assert.DoesNotContain(tenants.Factory.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_session_cookie_is_the_portals_own()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var browser = new BrowserClient(tenants.Factory);
        var token = await tenants.RequestLinkAsync(browser, tenants.A1);

        var confirmed = await Confirm(browser, tenants.A1, token);

        var setCookie = confirmed.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(PortalAuth.CookieName + "=", StringComparison.Ordinal));
        foreach (var attribute in new[] { "path=/api/portal", "secure", "samesite=strict", "httponly", "expires=" })
        {
            Assert.Contains(attribute, setCookie, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(confirmed.Headers.GetValues("Set-Cookie"), c => c.StartsWith("th_session=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_expires_after_fifteen_minutes()
    {
        var clock = new MovingClock(DateTimeOffset.UtcNow);
        await using var tenants = await PortalTenants.CreateAsync(postgres, services: s => s.AddSingleton<TimeProvider>(clock));
        using var browser = new BrowserClient(tenants.Factory);
        var late = await tenants.RequestLinkAsync(browser, tenants.A1);
        var onTime = await tenants.RequestLinkAsync(browser, tenants.A2);

        clock.Advance(TimeSpan.FromMinutes(14));
        var inTime = await Confirm(new BrowserClient(tenants.Factory), tenants.A2, onTime);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var expired = await Confirm(browser, tenants.A1, late);

        Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
        await ProblemAssert.HasCodeAsync(expired, HttpStatusCode.BadRequest, "portal-link-invalid");
    }

    [Fact]
    public async Task Asking_for_a_link_answers_the_same_whoever_asks_and_emails_only_a_contact()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var browser = new BrowserClient(tenants.Factory);
        var sentBefore = tenants.Emails.Sent.Count;

        var answers = new[]
        {
            await Request(browser, tenants.A1.Slug, tenants.A1.ContactEmail.ToUpperInvariant()),
            await Request(browser, tenants.A1.Slug, "nobody@client.example"),
            await Request(browser, tenants.A1.Slug, tenants.B1.ContactEmail), // a contact, but of another organization
            await Request(browser, "no-such-portal", tenants.A1.ContactEmail),
            await Request(browser, tenants.A1.Slug, "not an email"),
        };
        await tenants.Factory.DispatchOutboxAsync();

        foreach (var answer in answers)
        {
            Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
            Assert.Equal("", await answer.Content.ReadAsStringAsync(Ct));
        }

        var sent = tenants.Emails.Sent.Skip(sentBefore).ToList();
        Assert.Equal([tenants.A1.ContactEmail], sent.Select(m => m.To));
    }

    [Fact]
    public async Task One_link_a_minute_per_contact_and_few_requests_per_address()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres, new Dictionary<string, string?> { ["RateLimiting:PortalSignInPermitLimit"] = "4" });
        using var browser = new BrowserClient(tenants.Factory);
        await browser.PortalSessionAsync(tenants.A1.Slug);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await Request(browser, tenants.A1.Slug, tenants.A1.ContactEmail)).StatusCode);
        }

        await tenants.Factory.DispatchOutboxAsync();

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
        Assert.Single(tenants.Emails.Sent, m => m.To == tenants.A1.ContactEmail && m.Subject.StartsWith("Your sign-in link", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("contact removed")]
    [InlineData("client archived")]
    [InlineData("plan without the portal")]
    public async Task The_session_ends_on_the_next_request(string change)
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        var org = $"/api/orgs/{tenants.A1.OrganizationId}";
        Assert.Equal(HttpStatusCode.OK, (await contact.Http.GetAsync("/api/portal/visits", Ct)).StatusCode);

        var changed = change switch
        {
            "contact removed" => await owner.SendJsonAsync(HttpMethod.Delete, $"{org}/contacts/{tenants.A1.ContactId}", null),
            "client archived" => await owner.PostJsonAsync($"{org}/clients/{tenants.A1.ClientId}/archive", new { }),
            _ => await owner.SendJsonAsync(HttpMethod.Put, $"{org}/plan", new { plan = "free", confirmFeatureLoss = true }),
        };
        changed.EnsureSuccessStatusCode();
        var refused = await contact.Http.GetAsync("/api/portal/visits", Ct);
        var session = await contact.PortalSessionAsync(tenants.A1.Slug);

        await ProblemAssert.HasCodeAsync(refused, HttpStatusCode.Unauthorized, "portal-session-ended");
        Assert.Contains(refused.Headers.GetValues("Set-Cookie"), c => c.StartsWith(PortalAuth.CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, session.GetProperty("contact").ValueKind);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_another_portal_shows_its_own_sign_in()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);

        var elsewhere = await contact.PortalSessionAsync(tenants.B1.Slug);
        var home = await contact.PortalSessionAsync(tenants.A1.Slug);
        var signedOut = await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/sign-out", null, tenants.A1.Slug);
        var after = await contact.Http.GetAsync("/api/portal/visits", Ct);

        Assert.Equal((tenants.B1.OrganizationName, JsonValueKind.Null), (elsewhere.GetProperty("organization").GetProperty("name").GetString(), elsewhere.GetProperty("contact").ValueKind));
        Assert.Equal(tenants.A1.ContactEmail, home.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_link_asked_for_before_the_contact_was_removed_no_longer_works()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var browser = new BrowserClient(tenants.Factory);
        var token = await tenants.RequestLinkAsync(browser, tenants.A1);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.A1.OrganizationId}/contacts/{tenants.A1.ContactId}", null)).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await Confirm(browser, tenants.A1, token), HttpStatusCode.BadRequest, "portal-link-invalid");
    }

    private static Task<HttpResponseMessage> Confirm(BrowserClient browser, PortalClient who, string token) =>
        browser.PortalSendAsync(HttpMethod.Post, "/api/portal/sign-in/confirm", new { orgSlug = who.Slug, token }, who.Slug);

    private static Task<HttpResponseMessage> Request(BrowserClient browser, string slug, string email) =>
        browser.PortalSendAsync(HttpMethod.Post, "/api/portal/sign-in/request", new { orgSlug = slug, email }, slug);
}
