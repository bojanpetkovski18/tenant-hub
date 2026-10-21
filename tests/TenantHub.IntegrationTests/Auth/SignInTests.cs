using System.Net;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

public sealed class SignInTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_in_session_sign_out_cycle()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
        (await browser.SignOutAsync()).EnsureSuccessStatusCode();
        Assert.Null((await browser.SessionAsync()).User);

        var signIn = await browser.SignInAsync(email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(email, (await browser.SessionAsync()).User?.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.SignOutAsync()).StatusCode);
        Assert.Null((await browser.SessionAsync()).User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.CreateOrganizationAsync("Acme")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_generic_answer()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
        await browser.SignOutAsync();

        var wrongPassword = await ProblemAssert.IsProblemAsync(await browser.SignInAsync(email, "Wrong-Horse-9"), HttpStatusCode.Unauthorized);
        var unknownEmail = await ProblemAssert.IsProblemAsync(await browser.SignInAsync(BrowserClient.NewEmail()), HttpStatusCode.Unauthorized);

        Assert.Equal("Invalid email or password.", wrongPassword.GetProperty("title").GetString());
        Assert.Equal(wrongPassword.GetProperty("title").GetString(), unknownEmail.GetProperty("title").GetString());
        Assert.Null((await browser.SessionAsync()).User);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_with_the_same_generic_answer()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
        await browser.SignOutAsync();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SignInAsync(email, "Wrong-Horse-9")).StatusCode);
        }

        var correctButLocked = await browser.SignInAsync(email);

        var problem = await ProblemAssert.IsProblemAsync(correctButLocked, HttpStatusCode.Unauthorized);
        Assert.Equal("Invalid email or password.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Sign_in_is_rate_limited_per_client_with_retry_after()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["RateLimiting:AuthPermitLimit"] = "2",
        });
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();

        await browser.SignInAsync(email);
        await browser.SignInAsync(email);
        var third = await browser.SignInAsync(email);

        await ProblemAssert.IsProblemAsync(third, HttpStatusCode.TooManyRequests);
        Assert.True(third.Headers.RetryAfter is not null, "Retry-After header missing.");
    }

    [Fact]
    public async Task Unsafe_requests_without_the_antiforgery_header_are_rejected()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
        browser.SendXsrfHeader = false;

        var createOrganization = await browser.CreateOrganizationAsync("Cross Site Ltd");
        var signIn = await browser.SignInAsync(email);

        var problem = await ProblemAssert.IsProblemAsync(createOrganization, HttpStatusCode.BadRequest);
        Assert.Contains("anti-forgery", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        await ProblemAssert.IsProblemAsync(signIn, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Session_cookie_is_http_only_strict_and_secure()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);

        var response = await browser.SignUpAsync(BrowserClient.NewEmail());

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var session = Assert.Single(setCookies, c => c.StartsWith("th_session=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", session, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", session, StringComparison.Ordinal);
        Assert.Contains("secure", session, StringComparison.Ordinal);
        var xsrf = Assert.Single(setCookies, c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.DoesNotContain("httponly", xsrf, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", xsrf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoints_without_an_explicit_opt_out_require_a_signed_in_user()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);

        // These endpoints carry no authorization metadata of their own: the fallback policy protects them.
        await ProblemAssert.IsProblemAsync(await browser.Http.GetAsync(ApiFactory.UnannotatedPath, Ct), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.CreateOrganizationAsync("Acme")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PatchJsonAsync("/api/me", new { displayName = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostJsonAsync("/api/me/password", new { currentPassword = "x", newPassword = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SignOutAsync()).StatusCode);

        await browser.SignUpAsync(BrowserClient.NewEmail());
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync(ApiFactory.UnannotatedPath, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_even_when_signed_out()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var browser = new BrowserClient(factory);

        await ProblemAssert.IsProblemAsync(await browser.Http.GetAsync("/api/no-such-thing", Ct), HttpStatusCode.NotFound);
        await ProblemAssert.IsProblemAsync(await browser.PostJsonAsync("/api/no-such-thing", new { }), HttpStatusCode.NotFound);
    }
}
