using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Jobs;
using TenantHub.Application.Paging;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Jobs;

/// <summary>
/// US-33 on the seeded data: every filter and sort the list offers, through the API, compared with hand-written SQL
/// over the jobs table (the oracle). The SQL shares nothing with the handler but the database. One seeded database
/// serves the whole class: the tests only read, and seeding is the slow part.
/// </summary>
public sealed class JobFilterTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Seeded? shared;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Cases() => new()
    {
        "status=new&status=scheduled",
        "status=inProgress",
        "priority=urgent&priority=high",
        "assignee=me",
        "assignee=none",
        "assignee=maya",
        "client",
        "overdue=true",
        "due-range",
        "q=JOB-0042",
        "q=42",
        "q=window",
        "status=inProgress&assignee=maya&overdue=true",
        "status=bogus&priority=nope",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_filter_returns_exactly_what_the_SQL_oracle_returns(string name)
    {
        var seed = await SeededAsync();
        var org = seed.Org;
        var maya = seed.MayaId;
        var today = seed.Today;
        (string query, FormattableString sql) = name switch
        {
            "status=new&status=scheduled" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND status IN ('New', 'Scheduled')"),
            "status=inProgress" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND status = 'InProgress'"),
            "priority=urgent&priority=high" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND priority IN ('Urgent', 'High')"),
            "assignee=me" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND assignee_user_id = {seed.OliviaId}"),
            "assignee=none" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND assignee_user_id IS NULL"),
            "assignee=maya" => ($"assignee={maya}", (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND assignee_user_id = {maya}"),
            "client" => ($"clientId={seed.ClientId}", (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND client_id = {seed.ClientId}"),
            "overdue=true" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND due_on < {today} AND status NOT IN ('Completed', 'Cancelled')"),
            "due-range" => (
                $"dueFrom={today.AddDays(-14):yyyy-MM-dd}&dueTo={today.AddDays(7):yyyy-MM-dd}",
                (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND due_on BETWEEN {today.AddDays(-14)} AND {today.AddDays(7)}"),
            "q=JOB-0042" or "q=42" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND (number = 42 OR lower(title) LIKE {"%" + name[2..].ToLowerInvariant() + "%"})"),
            "q=window" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND lower(title) LIKE '%window%'"),
            "status=inProgress&assignee=maya&overdue=true" => (
                $"status=inProgress&assignee={maya}&overdue=true",
                (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org} AND status = 'InProgress' AND assignee_user_id = {maya} AND due_on < {today}"),
            _ => (name, (FormattableString)$"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {org}"),
        };

        var actual = await FetchAllAsync(seed, query);
        var oracle = await OracleAsync(seed, sql);

        Assert.NotEmpty(oracle);
        Assert.Equal(oracle.Order(), actual.Order());
    }

    public static TheoryData<string, string> Sorts() => new()
    {
        { "-number", "number DESC" },
        { "number", "number" },
        { "due", "due_on IS NULL, due_on, number" },
        { "-due", "due_on IS NULL, due_on DESC, number" },
        { "-priority", "CASE priority WHEN 'Urgent' THEN 3 WHEN 'High' THEN 2 WHEN 'Normal' THEN 1 ELSE 0 END DESC, number" },
        { "title", "title, number" },
        { "unknown", "number DESC" },
    };

    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task Each_sort_orders_like_the_SQL_oracle_across_pages(string sort, string orderBy)
    {
        var seed = await SeededAsync();

        var actual = await FetchAllAsync(seed, $"sort={sort}", pageSize: 40);
        // The ORDER BY text comes from this test's own table, never from input; the organization is a parameter.
        var oracle = await OracleAsync(seed, FormattableStringFactory.Create($"SELECT id AS \"Value\" FROM jobs WHERE organization_id = {{0}} ORDER BY {orderBy}", seed.Org));

        Assert.Equal(150, actual.Count);
        Assert.Equal(oracle, actual);
    }

    [Fact]
    public async Task The_list_marks_overdue_jobs_and_names_clients_and_assignees()
    {
        var seed = await SeededAsync();

        var page = await seed.Olivia.Http.GetFromJsonAsync<PagedList<JobListItem>>($"/api/orgs/{seed.Org}/jobs?overdue=true&pageSize=100", BrowserClient.Json, Ct);

        Assert.NotEmpty(page!.Items);
        Assert.All(page.Items, item => Assert.True(item.IsOverdue));
        Assert.All(page.Items, item => Assert.False(string.IsNullOrEmpty(item.ClientName)));
        Assert.All(page.Items.Where(i => i.AssigneeUserId is not null), item => Assert.False(string.IsNullOrEmpty(item.AssigneeName)));
        Assert.All(page.Items, item => Assert.Equal($"JOB-{item.Number:D4}", item.Label));
    }

    private static async Task<List<Guid>> FetchAllAsync(Seeded seed, string query, int pageSize = 100)
    {
        var ids = new List<Guid>();
        for (var page = 1; ; page++)
        {
            var result = await seed.Olivia.Http.GetFromJsonAsync<PagedList<JobListItem>>(
                $"/api/orgs/{seed.Org}/jobs?{query}&page={page}&pageSize={pageSize}", BrowserClient.Json, Ct);
            ids.AddRange(result!.Items.Select(i => i.Id));
            if (page * pageSize >= result.Total)
            {
                Assert.Equal(result.Total, ids.Count);
                return ids;
            }
        }
    }

    private static async Task<List<Guid>> OracleAsync(Seeded seed, FormattableString sql)
    {
        await using var db = PostgresFixture.CreateDbContext(seed.ConnectionString);
        return await db.Database.SqlQuery<Guid>(sql).ToListAsync(Ct);
    }

    private async Task<Seeded> SeededAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (shared is not null)
            {
                return shared;
            }

            var connectionString = await postgres.CreateDatabaseAsync();
            var factory = new ApiFactory(connectionString, new Dictionary<string, string?>
            {
                ["Seed:Enabled"] = "true",
                [DemoSeeder.PasswordSetting] = Password,
            });
            var olivia = new BrowserClient(factory);
            (await olivia.SignInAsync("olivia@brightline.example", Password)).EnsureSuccessStatusCode();

            await using var db = PostgresFixture.CreateDbContext(connectionString);
            var organization = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
            var oliviaId = await db.Users.Where(u => u.Email == "olivia@brightline.example").Select(u => u.Id).SingleAsync(Ct);
            var mayaId = await db.Users.Where(u => u.Email == "maya@brightline.example").Select(u => u.Id).SingleAsync(Ct);
            var busiestClient = (await db.Database
                .SqlQuery<Guid>($"SELECT client_id AS \"Value\" FROM jobs WHERE organization_id = {organization.Id} GROUP BY client_id ORDER BY count(*) DESC, client_id LIMIT 1")
                .ToListAsync(Ct)).Single();
            shared = new Seeded(factory, olivia, connectionString, organization.Id, organization.TimeZone, oliviaId, mayaId, busiestClient);
            return shared;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record Seeded(
        ApiFactory Factory, BrowserClient Olivia, string ConnectionString, Guid Org, string TimeZone, Guid OliviaId, Guid MayaId, Guid ClientId)
    {
        public DateOnly Today => Organization.TodayIn(TimeZone, DateTimeOffset.UtcNow);
    }
}
