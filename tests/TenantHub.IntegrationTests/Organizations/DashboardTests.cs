using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Clients;
using TenantHub.Application.Dashboard;
using TenantHub.Application.Jobs;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Organizations;

/// <summary>US-60: every dashboard number against hand-written SQL on the seeded data, and "this month" by zone.</summary>
public sealed class DashboardTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (ApiFactory Factory, string ConnectionString)? shared;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Roles() => new() { "olivia@brightline.example", "maya@brightline.example", "victor@brightline.example" };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Every_number_matches_the_SQL_oracle(string email)
    {
        var (factory, connectionString) = await SeededAsync();
        using var browser = new BrowserClient(factory);
        (await browser.SignInAsync(email, Password)).EnsureSuccessStatusCode();
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var org = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var me = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
        var today = Organization.TodayIn(org.TimeZone, DateTimeOffset.UtcNow);
        var monthStart = Organization.StartOfDayIn(org.TimeZone, new DateOnly(today.Year, today.Month, 1));

        var dashboard = await browser.Http.GetFromJsonAsync<DashboardView>($"/api/orgs/{org.Id}/dashboard", BrowserClient.Json, Ct);

        async Task<int> CountAsync(FormattableString sql) => await db.Database.SqlQuery<int>(sql).SingleAsync(Ct);
        Assert.Equal(
            new DashboardTiles(
                await CountAsync($"SELECT count(*)::int AS \"Value\" FROM jobs WHERE organization_id = {org.Id} AND status NOT IN ('Completed', 'Cancelled')"),
                await CountAsync($"SELECT count(*)::int AS \"Value\" FROM jobs WHERE organization_id = {org.Id} AND status NOT IN ('Completed', 'Cancelled') AND due_on < {today}"),
                await CountAsync($"SELECT count(*)::int AS \"Value\" FROM jobs WHERE organization_id = {org.Id} AND status = 'Completed' AND completed_at >= {monthStart}"),
                await CountAsync($"SELECT count(*)::int AS \"Value\" FROM clients WHERE organization_id = {org.Id} AND status = 'Active'")),
            dashboard!.Tiles);
        foreach (var status in dashboard.JobsByStatus)
        {
            var name = status.Status.ToString();
            Assert.Equal(await CountAsync($"SELECT count(*)::int AS \"Value\" FROM jobs WHERE organization_id = {org.Id} AND status = {name}"), status.Count);
        }

        var week = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM jobs
            WHERE organization_id = {org.Id} AND assignee_user_id = {me} AND scheduled_on >= {today} AND scheduled_on < {today.AddDays(7)}
            AND status NOT IN ('Completed', 'Cancelled')
            ORDER BY scheduled_on, number LIMIT 10
            """).ToListAsync(Ct);
        Assert.Equal(week, dashboard.MyNextSevenDays.Select(j => j.Id));
        Assert.Equal((today, new DateOnly(today.Year, today.Month, 1), "Europe/Amsterdam", Plan.Team), (dashboard.Today, dashboard.MonthStart, dashboard.TimeZone, dashboard.Plan));
        Assert.Equal((6, dashboard.Tiles.ActiveClients, dashboard.Tiles.OpenJobs), (dashboard.Usage.Seats, dashboard.Usage.ActiveClients, dashboard.Usage.OpenJobs));

        if (email.StartsWith("olivia", StringComparison.Ordinal))
        {
            var latest = await db.Database.SqlQuery<long>($"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org.Id} ORDER BY occurred_at DESC, id DESC LIMIT 8").ToListAsync(Ct);
            Assert.Equal(latest, dashboard.RecentActivity!.Select(a => a.Id));
        }
        else
        {
            Assert.Null(dashboard.RecentActivity);
        }
    }

    [Fact]
    public async Task This_month_is_the_organizations_month()
    {
        // Auckland is UTC+13 in late October: 10:30 UTC on 31 October is 23:30 there, 11:30 UTC is already 1 November.
        var clock = new MovingClock(new DateTimeOffset(2026, 10, 31, 10, 0, 0, TimeSpan.Zero));
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<TimeProvider>(clock));
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Auckland Cleaners", timeZone: "Pacific/Auckland")).EnsureSuccessStatusCode();
        var session = await owner.SessionAsync();
        var org = $"/api/orgs/{session.User!.Memberships.Single().OrganizationId}";
        var client = await (await owner.PostJsonAsync($"{org}/clients", new { name = "Harbour View" })).Content.ReadFromJsonAsync<ClientView>(BrowserClient.Json, Ct);

        async Task CompleteJobAtAsync(DateTimeOffset at)
        {
            clock.Now = at;
            var job = await (await owner.PostJsonAsync($"{org}/jobs", new { title = "Month edge", clientId = client!.Id, assigneeUserId = session.User.Id })).Content.ReadFromJsonAsync<JobView>(BrowserClient.Json, Ct);
            (await owner.PostJsonAsync($"{org}/jobs/{job!.Id}/transitions", new { to = "inProgress" })).EnsureSuccessStatusCode();
            (await owner.PostJsonAsync($"{org}/jobs/{job.Id}/transitions", new { to = "completed" })).EnsureSuccessStatusCode();
        }

        async Task<DashboardView> AtAsync(DateTimeOffset at)
        {
            clock.Now = at;
            return (await owner.Http.GetFromJsonAsync<DashboardView>($"{org}/dashboard", BrowserClient.Json, Ct))!;
        }

        await CompleteJobAtAsync(new DateTimeOffset(2026, 10, 31, 10, 30, 0, TimeSpan.Zero)); // 23:30, 31 October there
        var lateOctober = await AtAsync(new DateTimeOffset(2026, 10, 31, 10, 45, 0, TimeSpan.Zero));
        await CompleteJobAtAsync(new DateTimeOffset(2026, 10, 31, 11, 30, 0, TimeSpan.Zero)); // 00:30, 1 November there
        var earlyNovember = await AtAsync(new DateTimeOffset(2026, 10, 31, 11, 45, 0, TimeSpan.Zero));

        Assert.Equal((new DateOnly(2026, 10, 1), 1), (lateOctober.MonthStart, lateOctober.Tiles.CompletedThisMonth));
        Assert.Equal((new DateOnly(2026, 11, 1), 1), (earlyNovember.MonthStart, earlyNovember.Tiles.CompletedThisMonth));
        Assert.Equal(2, earlyNovember.JobsByStatus.Single(s => s.Status == JobStatus.Completed).Count);
    }

    private async Task<(ApiFactory Factory, string ConnectionString)> SeededAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (shared is null)
            {
                var connectionString = await postgres.CreateDatabaseAsync();
                var factory = new ApiFactory(connectionString, new Dictionary<string, string?>
                {
                    ["Seed:Enabled"] = "true",
                    [DemoSeeder.PasswordSetting] = Password,
                });
                using var warmUp = factory.CreateClient();
                shared = (factory, connectionString);
            }

            return shared.Value;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class MovingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
