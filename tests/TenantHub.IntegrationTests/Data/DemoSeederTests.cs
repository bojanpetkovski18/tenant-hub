using System.Net;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
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
        Assert.Equal(45, await db.AuditEntries.AcrossTenants().CountAsync(a => a.EntityType == "client", Ct));

        // Jobs: 150 in the larger organization, numbered 1..150, in every status, across members, some overdue.
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking().ToListAsync(Ct);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        Assert.Equal(150, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(brightline.Id, j.OrganizationId));
        Assert.Equal(Enumerable.Range(1, 150), jobs.Select(j => j.Number).Order());
        Assert.Equal(151, brightline.NextJobNumber);
        Assert.All(Enum.GetValues<JobStatus>(), status => Assert.Contains(jobs, j => j.Status == status));
        Assert.True(jobs.Select(j => j.AssigneeUserId).Distinct().Count() >= 4, "jobs are spread over the members");
        Assert.Contains(jobs, j => j.AssigneeUserId is null);
        var today = Organization.TodayIn(brightline.TimeZone, DateTimeOffset.UtcNow);
        Assert.Contains(jobs, j => j.IsOverdue(today));
        Assert.Contains(jobs, j => j.DueOn > today && j.Status.IsOpen());
        Assert.Equal(150, await db.AuditEntries.AcrossTenants().CountAsync(a => a.Action == "job.created", Ct));
        Assert.Equal(
            jobs.Count(j => j.Status != JobStatus.New),
            await db.AuditEntries.AcrossTenants().Where(a => a.Action == "job.status_changed").Select(a => a.EntityId).Distinct().CountAsync(Ct));
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
