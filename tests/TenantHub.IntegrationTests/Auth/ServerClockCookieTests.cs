using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

/// <summary>
/// Tests that pin the server's clock must still keep their session. The auth cookie's expiry is written from the
/// server's clock; the test client has to judge it by that clock too, or every pinned date older than the cookie's
/// lifetime signs the test out right after it signs in (found 2026-11-16, when the October dates had passed).
/// </summary>
public sealed class ServerClockCookieTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset LongAgo = new(2000, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_session_lasts_by_the_servers_clock_not_the_test_machines()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<TimeProvider>(new FixedClock(LongAgo)));
        using var browser = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();

        var response = await browser.SignUpAsync(email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await browser.SessionAsync();
        Assert.Equal(email, session.User?.Email);
    }

    [Fact]
    public async Task Signing_out_still_ends_the_session_under_a_pinned_clock()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<TimeProvider>(new FixedClock(LongAgo)));
        using var browser = new BrowserClient(factory);
        await browser.SignUpAsync(BrowserClient.NewEmail());

        var response = await browser.SignOutAsync();

        Assert.True(response.IsSuccessStatusCode, $"sign-out returned {(int)response.StatusCode}");
        Assert.Null((await browser.SessionAsync()).User);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
