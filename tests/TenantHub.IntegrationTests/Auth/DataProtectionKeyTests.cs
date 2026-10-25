using Microsoft.EntityFrameworkCore;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Auth;

/// <summary>
/// K1: the keys that sign session and antiforgery cookies live in the database, so a restarted container (or a
/// second instance) still accepts the cookies the first one issued.
/// </summary>
public sealed class DataProtectionKeyTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_survives_a_restart_because_the_keys_are_in_the_database()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var email = BrowserClient.NewEmail();
        System.Net.CookieCollection cookies;
        await using (var first = new ApiFactory(connectionString))
        {
            using var browser = new BrowserClient(first);
            (await browser.SignUpAsync(email)).EnsureSuccessStatusCode();
            cookies = browser.Cookies.GetAllCookies();
        }

        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            var keys = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM data_protection_keys").SingleAsync(Ct);
            Assert.True(keys > 0, "No data-protection key in the database.");
        }

        await using var second = new ApiFactory(connectionString);
        using var again = new BrowserClient(second);
        again.Cookies.Add(cookies);

        Assert.Equal(email, (await again.SessionAsync()).User?.Email);
    }
}
