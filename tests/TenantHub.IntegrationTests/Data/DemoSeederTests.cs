using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

/// <summary>SPEC 8.3: the demo data, its history, idempotency and determinism.</summary>
public sealed class DemoSeederTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly DateTimeOffset Today = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Settings => new() { ["Seed:Enabled"] = "true", [DemoSeeder.PasswordSetting] = Password };

    private async Task<string> SeedAtAsync(DateTimeOffset now, string? connectionString = null)
    {
        connectionString ??= await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, Settings, services => services.AddSingleton<TimeProvider>(new FixedClock(now)));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        return connectionString;
    }

    [Fact]
    public async Task Seed_creates_both_organizations_as_specified_and_is_idempotent()
    {
        var connectionString = await SeedAtAsync(Today);
        await SeedAtAsync(Today.AddHours(5), connectionString);

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var harbor = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.HarborPineSlug, Ct);
        Assert.Equal(2, await db.Organizations.CountAsync(Ct));
        Assert.True(await db.Organizations.AllAsync(o => o.IsDemoSeed, Ct));
        Assert.Equal(8, await db.Users.CountAsync(Ct));
        Assert.All(DemoSeeder.Emails, email => Assert.EndsWith(".example", email, StringComparison.Ordinal));

        // Brightline: Team, six members, forty clients (five archived), 150 jobs in every status.
        Assert.Equal((Plan.Team, 6), (brightline.Plan, await Members(db, brightline.Id)));
        Assert.Equal((40, 5), (await Clients(db, brightline.Id, null), await Clients(db, brightline.Id, ClientStatus.Archived)));
        var brightJobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == brightline.Id).ToListAsync(Ct);
        Assert.Equal(Enumerable.Range(1, 150), brightJobs.Select(j => j.Number).Order());
        Assert.All(Enum.GetValues<JobStatus>(), status => Assert.Contains(brightJobs, j => j.Status == status));
        var today = Organization.TodayIn(brightline.TimeZone, Today);
        Assert.Contains(brightJobs, j => j.IsOverdue(today));
        Assert.Contains(brightJobs, j => j.DueOn > today.AddDays(14) && j.Status.IsOpen());
        Assert.Contains(brightJobs, j => j.ScheduledOn < today.AddDays(-100));

        // Harbor & Pine: Free and close to every limit (SPEC 8.3, "to show limit handling").
        var harborJobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == harbor.Id).ToListAsync(Ct);
        var open = harborJobs.Count(j => j.Status.IsOpen());
        Assert.Equal((Plan.Free, 3), (harbor.Plan, await Members(db, harbor.Id)));
        Assert.Equal(22, await Clients(db, harbor.Id, ClientStatus.Active));
        Assert.InRange(open, 40, 50);
        Assert.Equal(Enumerable.Range(1, harborJobs.Count), harborJobs.Select(j => j.Number).Order());

        // The one person in both organizations, and no audit entries beyond the seed's own history.
        var sam = await db.Users.SingleAsync(u => u.Email == "sam@tenanthub.example", Ct);
        Assert.Equal(2, await db.Memberships.AcrossTenants().CountAsync(m => m.UserId == sam.Id, Ct));
    }

    [Fact]
    public async Task The_history_agrees_with_the_data_and_was_made_by_the_people_who_would_do_it()
    {
        var connectionString = await SeedAtAsync(Today);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == brightline.Id).ToListAsync(Ct);
        var audit = await db.AuditEntries.AcrossTenants().AsNoTracking().Where(a => a.OrganizationId == brightline.Id).ToListAsync(Ct);

        // Every entry is in the past, spread over months, and each job's creation entry is at its created_at.
        Assert.All(audit, a => Assert.True(a.OccurredAt <= Today));
        Assert.True(audit.Min(a => a.OccurredAt) < Today.AddDays(-100));
        var created = audit.Where(a => a.Action == "job.created").ToDictionary(a => a.EntityId, a => a.OccurredAt);
        Assert.All(jobs, j => Assert.Equal(j.CreatedAt, created[j.Id]));

        // Completed jobs: completed_at is the moment of the move to Completed, made by the assignee.
        var names = await db.Users.ToDictionaryAsync(u => u.Id, u => u.DisplayName, Ct);
        foreach (var job in jobs.Where(j => j.Status == JobStatus.Completed))
        {
            var completion = audit.Single(a => a.EntityId == job.Id && a.Action == "job.status_changed" && a.Summary.EndsWith("to Completed", StringComparison.Ordinal));
            Assert.Equal(job.CompletedAt, completion.OccurredAt);
            Assert.Equal(names[job.AssigneeUserId!.Value], completion.ActorName);
        }

        // Numbers follow creation time; several people did the work.
        Assert.Equal(jobs.OrderBy(j => j.CreatedAt).ThenBy(j => j.Number).Select(j => j.Number), jobs.OrderBy(j => j.Number).Select(j => j.Number));
        Assert.True(audit.Select(a => a.ActorName).Distinct().Count() >= 4, string.Join(", ", audit.Select(a => a.ActorName).Distinct()));
        Assert.Contains(audit, a => a.Action == "client.updated");
        Assert.Contains(audit, a => a.Action == "client.archived");
        Assert.Contains(audit, a => a.Action == "job.status_changed" && a.Summary.StartsWith("Cancelled JOB-", StringComparison.Ordinal) && a.Summary.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_seeds_on_the_same_day_produce_the_same_organizations()
    {
        var first = await SeedAtAsync(Today);
        var second = await SeedAtAsync(Today);

        Assert.Equal(await FingerprintAsync(first), await FingerprintAsync(second));
    }

    [Fact]
    public async Task Seeded_users_can_sign_in_and_the_shared_user_sees_both_organizations()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), Settings);
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

    private static Task<int> Members(AppDbContext db, Guid org) =>
        db.Memberships.AcrossTenants().CountAsync(m => m.OrganizationId == org && m.RemovedAt == null, Ct);

    private static Task<int> Clients(AppDbContext db, Guid org, ClientStatus? status) =>
        db.Clients.AcrossTenants().CountAsync(c => c.OrganizationId == org && (status == null || c.Status == status), Ct);

    /// <summary>Everything a visitor would see, without ids: equal fingerprints mean equal demos.</summary>
    private static async Task<string> FingerprintAsync(string connectionString)
    {
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var orgs = await db.Organizations.AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Slug, Ct);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email, Ct);
        var clients = await db.Clients.AcrossTenants().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, Ct);
        var jobs = (await db.Jobs.AcrossTenants().AsNoTracking().ToListAsync(Ct))
            .Select(j => $"{orgs[j.OrganizationId]}|{j.Number}|{j.Title}|{clients[j.ClientId]}|{(j.AssigneeUserId is { } a ? users[a] : "-")}|{j.Status}|{j.Priority}|{j.ScheduledOn}|{j.DueOn}|{j.QuotedAmount}|{j.CreatedAt:O}|{j.CompletedAt:O}")
            .Order(StringComparer.Ordinal);
        var audit = (await db.AuditEntries.AcrossTenants().AsNoTracking().ToListAsync(Ct))
            .Select(a => $"{orgs[a.OrganizationId]}|{a.OccurredAt:O}|{a.ActorName}|{a.Action}|{a.Summary}")
            .Order(StringComparer.Ordinal);
        return string.Join("\n", jobs.Concat(audit));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
