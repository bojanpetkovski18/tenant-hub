using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantHub.Application.Audit;
using TenantHub.Application.Invitations;
using TenantHub.Application.Jobs;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Audit;

/// <summary>US-40 through the API: filters against a SQL oracle, keyset paging, the time-zone date range, and every action's summary.</summary>
public sealed partial class AuditLogTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Seeded? shared;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Filters() => new()
    {
        "none",
        "actor",
        "action=job.created",
        "action=job.status_changed",
        "entityType=client",
        "entityType=job&action=job.status_changed",
        "entityId",
        "from=today",
        "to=yesterday",
        "action=unknown.action",
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Each_filter_returns_exactly_what_the_SQL_oracle_returns_newest_first_across_pages(string name)
    {
        var seed = await SeededAsync();
        var org = seed.Org;
        var today = Domain.Organizations.Organization.TodayIn(seed.TimeZone, DateTimeOffset.UtcNow);
        var start = Domain.Organizations.Organization.StartOfDayIn(seed.TimeZone, today);
        (string query, FormattableString sql) = name switch
        {
            "actor" => ($"actor={seed.OliviaId}", (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND actor_user_id = {seed.OliviaId} ORDER BY occurred_at DESC, id DESC"),
            "action=job.created" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND action = 'job.created' ORDER BY occurred_at DESC, id DESC"),
            "action=job.status_changed" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND action = 'job.status_changed' ORDER BY occurred_at DESC, id DESC"),
            "entityType=client" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND entity_type = 'client' ORDER BY occurred_at DESC, id DESC"),
            "entityType=job&action=job.status_changed" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND entity_type = 'job' AND action = 'job.status_changed' ORDER BY occurred_at DESC, id DESC"),
            "entityId" => ($"entityId={seed.BusiestJob}", (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND entity_id = {seed.BusiestJob} ORDER BY occurred_at DESC, id DESC"),
            "from=today" => ($"from={today:yyyy-MM-dd}", (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND occurred_at >= {start} ORDER BY occurred_at DESC, id DESC"),
            "to=yesterday" => ($"to={today.AddDays(-1):yyyy-MM-dd}", (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND occurred_at < {start} ORDER BY occurred_at DESC, id DESC"),
            "action=unknown.action" => (name, (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} AND action = 'unknown.action' ORDER BY occurred_at DESC, id DESC"),
            _ => ("", (FormattableString)$"SELECT id AS \"Value\" FROM audit_entries WHERE organization_id = {org} ORDER BY occurred_at DESC, id DESC"),
        };

        var (actual, pages) = await FetchAllAsync(seed, query, pageSize: 37);
        await using var db = PostgresFixture.CreateDbContext(seed.ConnectionString);
        var oracle = await db.Database.SqlQuery<long>(sql).ToListAsync(Ct);

        Assert.Equal(oracle, actual);
        Assert.Equal(Math.Max(1, (oracle.Count + 36) / 37), pages);
    }

    [Fact]
    public async Task A_malformed_cursor_or_page_size_falls_back_to_the_first_page()
    {
        var seed = await SeededAsync();

        var first = await seed.Olivia.Http.GetFromJsonAsync<AuditPage>($"/api/orgs/{seed.Org}/audit?pageSize=10", BrowserClient.Json, Ct);
        var odd = await seed.Olivia.Http.GetFromJsonAsync<AuditPage>($"/api/orgs/{seed.Org}/audit?pageSize=10&before=not-a-cursor", BrowserClient.Json, Ct);
        var huge = await seed.Olivia.Http.GetFromJsonAsync<AuditPage>($"/api/orgs/{seed.Org}/audit?pageSize=5000", BrowserClient.Json, Ct);

        Assert.Equal(first!.Items.Select(i => i.Id), odd!.Items.Select(i => i.Id));
        Assert.Equal(ListAuditEntriesQuery.MaxPageSize, huge!.Items.Count);
    }

    [Fact]
    public async Task The_date_range_is_in_the_organizations_time_zone()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync());
        using var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Auckland Cleaners", timeZone: "Pacific/Auckland")).EnsureSuccessStatusCode();
        var org = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;

        // Auckland is UTC+13 on 22 October 2026: 10:59 UTC is 23:59 on the 22nd there, 11:01 UTC is 00:01 on the 23rd.
        async Task<long> InsertAtAsync(string instant)
        {
            await using var connection = new NpgsqlConnection(factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:Database"]);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO audit_entries (organization_id, occurred_at, actor_name, action, entity_type, entity_id, summary, changes)
                VALUES (@org, @at, 'Test', 'client.updated', 'client', gen_random_uuid(), 'Boundary entry', '[]')
                RETURNING id
                """,
                connection);
            command.Parameters.AddWithValue("org", org);
            command.Parameters.AddWithValue("at", DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        var lateOn22nd = await InsertAtAsync("2026-10-22T10:59:00Z");
        var earlyOn23rd = await InsertAtAsync("2026-10-22T11:01:00Z");

        async Task<List<long>> OnAsync(string query) =>
            [.. (await owner.Http.GetFromJsonAsync<AuditPage>($"/api/orgs/{org}/audit?entityType=client&{query}", BrowserClient.Json, Ct))!.Items.Select(i => i.Id)];

        Assert.Equal([lateOn22nd], await OnAsync("from=2026-10-22&to=2026-10-22"));
        Assert.Equal([earlyOn23rd], await OnAsync("from=2026-10-23&to=2026-10-23"));
        Assert.Equal([earlyOn23rd, lateOn22nd], await OnAsync("from=2026-10-22&to=2026-10-23"));
        Assert.Empty(await OnAsync("to=2026-10-21"));
    }

    [Fact]
    public async Task Every_action_reads_as_a_sentence_about_names_not_ids_and_links_only_to_what_still_exists()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var org = tenants.OrgA;
        var memberId = await tenants.UserIdOfAsync(Role.Member);

        // Clients: create, update, archive, restore; a second one is deleted.
        var client = await ReadAsync<Application.Clients.ClientView>(await owner.PostJsonAsync($"/api/orgs/{org}/clients", new { name = "Audit Review Offices" }));
        await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{org}/clients/{client.Id}", new { name = client.Name, email = "desk@review.example", version = client.Version });
        await owner.PostJsonAsync($"/api/orgs/{org}/clients/{client.Id}/archive", new { });
        await owner.PostJsonAsync($"/api/orgs/{org}/clients/{client.Id}/restore", new { });
        var doomed = await ReadAsync<Application.Clients.ClientView>(await owner.PostJsonAsync($"/api/orgs/{org}/clients", new { name = "Short-Lived Ltd" }));
        await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{org}/clients/{doomed.Id}", null);

        // Jobs: create, update, move, unassign (by removal below), and delete.
        var job = await ReadAsync<JobView>(await owner.PostJsonAsync($"/api/orgs/{org}/jobs", new { title = "Review job", clientId = client.Id, assigneeUserId = memberId }));
        await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{org}/jobs/{job.Id}", new { title = "Review job, renamed", clientId = client.Id, assigneeUserId = memberId, priority = "high", version = job.Version });
        await owner.PostJsonAsync($"/api/orgs/{org}/jobs/{job.Id}/transitions", new { to = "inProgress" });
        var gone = await ReadAsync<JobView>(await owner.PostJsonAsync($"/api/orgs/{org}/jobs", new { title = "Deleted job", clientId = client.Id }));
        await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{org}/jobs/{gone.Id}", null);

        // Invitations: created, resent, revoked; another accepted by a new person.
        var invited = await ReadAsync<InvitationSent>(await owner.PostJsonAsync($"/api/orgs/{org}/invitations", new { email = BrowserClient.NewEmail(), role = "viewer" }));
        await owner.PostJsonAsync($"/api/orgs/{org}/invitations/{invited.Invitation.Id}/resend", new { });
        await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{org}/invitations/{invited.Invitation.Id}", null);
        var joining = await ReadAsync<InvitationSent>(await owner.PostJsonAsync($"/api/orgs/{org}/invitations", new { email = BrowserClient.NewEmail(), role = "member" }));
        using (var newcomer = new BrowserClient(tenants.Factory))
        {
            var link = joining.Link!;
            var token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..];
            (await newcomer.PostJsonAsync("/api/invitations/sign-up", new { token, displayName = "Nadia Review", password = BrowserClient.Password })).EnsureSuccessStatusCode();
        }

        // Members: a role change, a removal (which unassigns the job), and someone leaving.
        await owner.PatchJsonAsync($"/api/orgs/{org}/members/{await tenants.UserIdOfAsync(Role.Viewer)}", new { role = "member" });
        await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{org}/members/{memberId}", null);
        var admin = await tenants.ClientForAsync(Role.Admin);
        await admin.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{org}/members/me", null);

        var all = new List<AuditEntryView>();
        string? cursor = null;
        do
        {
            var page = await owner.Http.GetFromJsonAsync<AuditPage>($"/api/orgs/{org}/audit?pageSize=100{(cursor is null ? "" : $"&before={cursor}")}", BrowserClient.Json, Ct);
            all.AddRange(page!.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        string[] actions =
        [
            "client.created", "client.updated", "client.archived", "client.restored", "client.deleted",
            "job.created", "job.updated", "job.status_changed", "job.unassigned", "job.deleted",
            "invitation.created", "invitation.resent", "invitation.revoked", "invitation.accepted",
            "member.role_changed", "member.removed", "member.left",
        ];
        Assert.All(actions, action => Assert.Contains(all, e => e.Action == action));

        // Readable: a sentence that starts with a capitalized verb (or with the person it is about), no ids, and
        // every changed value a name, label, date or amount.
        Assert.All(all, e =>
        {
            if (e.Action is not ("member.left" or "invitation.accepted"))
            {
                Assert.Matches("^[A-Z][a-z]+ ", e.Summary);
            }

            Assert.DoesNotMatch(GuidPattern(), e.Summary);
            Assert.All(e.Changes, c => Assert.DoesNotMatch(GuidPattern(), $"{c.From}|{c.To}"));
            Assert.False(string.IsNullOrWhiteSpace(e.ActorName));
        });
        Assert.Contains(all, e => e.Action == "job.unassigned" && e.Summary.Contains("was removed", StringComparison.Ordinal));
        Assert.Contains(all, e => e.Action == "member.removed" && e.Summary.EndsWith("from the organization", StringComparison.Ordinal));

        // Links only where the thing still exists: the deleted client and job, the revoked invitation, the removed member.
        Assert.True(all.Single(e => e.Action == "client.created" && e.EntityId == client.Id).EntityExists);
        Assert.False(all.Single(e => e.Action == "client.deleted").EntityExists);
        Assert.True(all.First(e => e.EntityId == job.Id).EntityExists);
        Assert.False(all.Single(e => e.Action == "job.deleted").EntityExists);
        Assert.False(all.Single(e => e.Action == "invitation.revoked").EntityExists);
        Assert.False(all.Single(e => e.Action == "member.removed" && e.EntityId == memberId).EntityExists);
        Assert.True(all.Single(e => e.Action == "member.role_changed").EntityExists);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, BrowserClient.Json)!;
    }

    private static async Task<(List<long> Ids, int Pages)> FetchAllAsync(Seeded seed, string query, int pageSize)
    {
        var ids = new List<long>();
        var pages = 0;
        string? cursor = null;
        do
        {
            var page = await seed.Olivia.Http.GetFromJsonAsync<AuditPage>(
                $"/api/orgs/{seed.Org}/audit?{query}&pageSize={pageSize}{(cursor is null ? "" : $"&before={cursor}")}", BrowserClient.Json, Ct);
            ids.AddRange(page!.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        return (ids, pages);
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
            var busiestJob = await db.AuditEntries.AcrossTenants()
                .Where(a => a.OrganizationId == organization.Id && a.EntityType == "job")
                .GroupBy(a => a.EntityId)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstAsync(Ct);
            shared = new Seeded(factory, olivia, connectionString, organization.Id, organization.TimeZone, oliviaId, busiestJob);
            return shared;
        }
        finally
        {
            Gate.Release();
        }
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();

    private sealed record Seeded(ApiFactory Factory, BrowserClient Olivia, string ConnectionString, Guid Org, string TimeZone, Guid OliviaId, Guid BusiestJob);
}
