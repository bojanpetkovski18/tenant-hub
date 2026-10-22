using System.Net;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

public sealed class DemoSeederTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seed_runs_on_startup_and_is_idempotent()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?> { ["Seed:Enabled"] = "true", [DemoSeeder.PasswordSetting] = Password };

        // Two app starts against the same database: the second must not add anything.
        for (var start = 0; start < 2; start++)
        {
            await using var factory = new ApiFactory(connectionString, settings);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        }

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(2, await db.Organizations.CountAsync(Ct));
        Assert.Equal(6, await db.Users.CountAsync(Ct));
        Assert.Equal(7, await db.Memberships.AcrossTenants().CountAsync(Ct));
        Assert.True(await db.Organizations.AllAsync(o => o.IsDemoSeed, Ct));
        Assert.Equal(40, await db.Clients.AcrossTenants().CountAsync(Ct));
        Assert.Equal(5, await db.Clients.AcrossTenants().CountAsync(c => c.Status == ClientStatus.Archived, Ct));
        Assert.All(await db.Clients.AcrossTenants().Select(c => c.Email!).ToListAsync(Ct), email => Assert.EndsWith(".example", email, StringComparison.Ordinal));
        // The seed goes through the domain, so its history is complete: 40 created and 5 archived.
        Assert.Equal(45, await db.AuditEntries.AcrossTenants().CountAsync(Ct));
        Assert.All(DemoSeeder.Emails, email => Assert.EndsWith(".example", email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seeded_users_can_sign_in_and_the_shared_user_sees_both_organizations()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Seed:Enabled"] = "true",
            [DemoSeeder.PasswordSetting] = Password,
        });
        using var browser = new BrowserClient(factory);

        (await browser.SignInAsync("sam@tenanthub.example", Password)).EnsureSuccessStatusCode();

        var memberships = (await browser.SessionAsync()).User!.Memberships;
        Assert.Equal(
            [("brightline-cleaning-co", Role.Member), ("harbor-pine-landscaping", Role.Admin)],
            memberships.Select(m => (m.Slug, m.Role)));
    }

    [Fact]
    public async Task Seed_is_skipped_without_a_demo_password()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?> { ["Seed:Enabled"] = "true" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await db.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task A_weak_demo_password_stops_startup_with_a_clear_message()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Seed:Enabled"] = "true",
            [DemoSeeder.PasswordSetting] = "weak",
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("could not be created", error.Message, StringComparison.Ordinal);
    }
}
