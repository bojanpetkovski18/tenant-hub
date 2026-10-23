using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Invitations;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Organizations;

/// <summary>
/// SPEC 5.5 and D013 through the API, on fresh Free organizations: every command that consumes a limit is refused at
/// the limit with the same details, and with one unit left two simultaneous requests produce exactly one success.
/// </summary>
public sealed class PlanLimitTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record FreeOrganization(ApiFactory Factory, BrowserClient Owner, Guid Id, Guid OwnerId, string ConnectionString) : IAsyncDisposable
    {
        public string Url(string path = "") => $"/api/orgs/{Id}{path}";

        /// <summary>A context working in this organization as its Owner, for arranging usage directly.</summary>
        public AppDbContext Db()
        {
            var tenant = new TenantContext();
            tenant.Enter(Id, OwnerId, Role.Owner);
            return PostgresFixture.CreateDbContext(ConnectionString, tenant);
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<FreeOrganization> FreeOrganizationAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var factory = new ApiFactory(connectionString);
        var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), "Small Cleaners")).EnsureSuccessStatusCode();
        var session = await owner.SessionAsync();
        return new FreeOrganization(factory, owner, session.User!.Memberships.Single().OrganizationId, session.User.Id, connectionString);
    }

    private static async Task<List<Guid>> AddClientsAsync(FreeOrganization org, int count, bool archived = false)
    {
        await using var db = org.Db();
        var clients = Enumerable.Range(0, count)
            .Select(i => Client.Create(org.Id, ClientDetails.Validate($"Client {Guid.NewGuid():N}", null, null, null, null, null), DateTimeOffset.UtcNow))
            .ToList();
        if (archived)
        {
            clients.ForEach(c => c.Archive(DateTimeOffset.UtcNow));
        }

        db.Clients.AddRange(clients);
        await db.SaveChangesAsync(Ct);
        return [.. clients.Select(c => c.Id)];
    }

    private static async Task<List<Guid>> AddOpenJobsAsync(FreeOrganization org, Guid clientId, int count)
    {
        await using var db = org.Db();
        var organization = await db.Organizations.SingleAsync(o => o.Id == org.Id, Ct);
        var owner = JobActor.From(org.OwnerId, Role.Owner);
        var jobs = Enumerable.Range(0, count)
            .Select(i => Job.Create(org.Id, organization.TakeJobNumber(), JobDetails.Validate($"Job {i}", null, clientId, null, null, null, null, null), owner, new Dictionary<Guid, string>(), DateTimeOffset.UtcNow))
            .ToList();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync(Ct);
        return [.. jobs.Select(j => j.Id)];
    }

    private static async Task AssertLimitReachedAsync(HttpResponseMessage response, string limit, int allowed, int usage)
    {
        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("plan-limit-reached", problem.GetProperty("code").GetString());
        Assert.Equal(
            ("free", limit, allowed, usage),
            (problem.GetProperty("plan").GetString(), problem.GetProperty("limit").GetString(), problem.GetProperty("allowed").GetInt32(), problem.GetProperty("usage").GetInt32()));
        Assert.StartsWith("Your Free plan allows", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_simultaneous_invitations_for_the_last_seat_give_exactly_one_success()
    {
        await using var org = await FreeOrganizationAsync();
        (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" })).EnsureSuccessStatusCode();

        var responses = await Task.WhenAll(
            org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" }),
            org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "viewer" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLimitReachedAsync(responses.Single(r => r.StatusCode != HttpStatusCode.OK), "seats", 3, 3);
        var overview = await org.Owner.Http.GetFromJsonAsync<OrganizationOverview>(org.Url(), BrowserClient.Json, Ct);
        Assert.Equal(3, overview!.Usage.Seats);
    }

    [Fact]
    public async Task Two_simultaneous_clients_for_the_last_slot_give_exactly_one_success()
    {
        await using var org = await FreeOrganizationAsync();
        await AddClientsAsync(org, 24);

        var responses = await Task.WhenAll(
            org.Owner.PostJsonAsync(org.Url("/clients"), new { name = $"Racer A {Guid.NewGuid():N}" }),
            org.Owner.PostJsonAsync(org.Url("/clients"), new { name = $"Racer B {Guid.NewGuid():N}" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        await AssertLimitReachedAsync(responses.Single(r => r.StatusCode != HttpStatusCode.Created), "activeClients", 25, 25);
    }

    [Fact]
    public async Task Two_simultaneous_jobs_for_the_last_slot_give_exactly_one_success()
    {
        await using var org = await FreeOrganizationAsync();
        var client = (await AddClientsAsync(org, 1))[0];
        await AddOpenJobsAsync(org, client, 49);

        var responses = await Task.WhenAll(
            org.Owner.PostJsonAsync(org.Url("/jobs"), new { title = "Racer A", clientId = client }),
            org.Owner.PostJsonAsync(org.Url("/jobs"), new { title = "Racer B", clientId = client }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        await AssertLimitReachedAsync(responses.Single(r => r.StatusCode != HttpStatusCode.Created), "openJobs", 50, 50);
    }

    [Fact]
    public async Task Restoring_a_client_and_reopening_a_job_are_limit_checked_too()
    {
        await using var org = await FreeOrganizationAsync();
        var archived = (await AddClientsAsync(org, 1, archived: true))[0];
        var active = await AddClientsAsync(org, 25);
        await AddOpenJobsAsync(org, active[0], 50);
        var done = (await AddOpenJobsAsync(org, active[0], 1))[0];
        await using (var db = org.Db())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == done, Ct);
            job.Update(job.Details with { AssigneeUserId = org.OwnerId }, JobActor.From(org.OwnerId, Role.Owner), new Dictionary<Guid, string>(), DateTimeOffset.UtcNow);
            job.TransitionTo(JobStatus.InProgress, JobActor.From(org.OwnerId, Role.Owner), null, DateTimeOffset.UtcNow);
            job.TransitionTo(JobStatus.Completed, JobActor.From(org.OwnerId, Role.Owner), null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        }

        await AssertLimitReachedAsync(await org.Owner.PostJsonAsync(org.Url($"/clients/{archived}/restore"), new { }), "activeClients", 25, 25);
        await AssertLimitReachedAsync(await org.Owner.PostJsonAsync(org.Url($"/jobs/{done}/transitions"), new { to = "inProgress", reason = "Again" }), "openJobs", 50, 50);

        // Archiving a client and completing a job free the room again.
        (await org.Owner.PostJsonAsync(org.Url($"/clients/{active[1]}/archive"), new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await org.Owner.PostJsonAsync(org.Url($"/clients/{archived}/restore"), new { })).StatusCode);
    }

    [Fact]
    public async Task Expired_invitations_free_their_seat_until_renewed_and_accepting_takes_no_extra_seat()
    {
        await using var org = await FreeOrganizationAsync();
        var first = await (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" })).Content.ReadFromJsonAsync<InvitationSent>(BrowserClient.Json, Ct);
        await using (var db = org.Db())
        {
            var invitation = await db.Invitations.SingleAsync(i => i.Id == first!.Invitation.Id, Ct);
            db.Entry(invitation).Property(i => i.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }

        // 1 member + 0 pending (expired): two more invitations fit.
        var second = await (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" })).Content.ReadFromJsonAsync<InvitationSent>(BrowserClient.Json, Ct);
        (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "viewer" })).EnsureSuccessStatusCode();

        // Full: renewing the expired one is refused; accepting a pending one only turns its seat into a membership.
        await AssertLimitReachedAsync(await org.Owner.PostJsonAsync(org.Url($"/invitations/{first!.Invitation.Id}/resend"), new { }), "seats", 3, 3);
        using var joiner = new BrowserClient(org.Factory);
        var link = second!.Link!;
        var token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..];
        Assert.Equal(HttpStatusCode.OK, (await joiner.PostJsonAsync("/api/invitations/sign-up", new { token, displayName = "Lena Joiner", password = BrowserClient.Password })).StatusCode);

        var overview = await org.Owner.Http.GetFromJsonAsync<OrganizationOverview>(org.Url(), BrowserClient.Json, Ct);
        Assert.Equal((3, 3), (overview!.Usage.Seats, overview.Limits.Seats));
    }

    [Fact]
    public async Task An_owner_upgrades_and_continues_and_a_downgrade_that_does_not_fit_lists_what_blocks_it()
    {
        await using var org = await FreeOrganizationAsync();
        for (var i = 0; i < 2; i++)
        {
            (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" })).EnsureSuccessStatusCode();
        }

        await AssertLimitReachedAsync(await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" }), "seats", 3, 3);
        var upgraded = await (await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url("/plan"), new { plan = "team" })).Content.ReadFromJsonAsync<OrganizationOverview>(BrowserClient.Json, Ct);
        (await org.Owner.PostJsonAsync(org.Url("/invitations"), new { email = BrowserClient.NewEmail(), role = "member" })).EnsureSuccessStatusCode();
        await AddClientsAsync(org, 26);

        var refused = await ProblemAssert.IsProblemAsync(await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url("/plan"), new { plan = "free" }), HttpStatusCode.Conflict);

        Assert.Equal((Plan.Team, 10), (upgraded!.Plan, upgraded.Limits.Seats));
        Assert.Equal("plan-downgrade-blocked", refused.GetProperty("code").GetString());
        var blocking = refused.GetProperty("blocking").EnumerateArray().Select(b => (b.GetProperty("limit").GetString(), b.GetProperty("allowed").GetInt32(), b.GetProperty("usage").GetInt32())).ToList();
        Assert.Equal([("seats", 3, 4), ("activeClients", 25, 26)], blocking);
        var overview = await org.Owner.Http.GetFromJsonAsync<OrganizationOverview>(org.Url(), BrowserClient.Json, Ct);
        Assert.Equal(Plan.Team, overview!.Plan);
        Assert.Equal([Plan.Free, Plan.Team, Plan.Business], overview.Plans.Select(p => p.Plan));
        Assert.Null(overview.Plans.Single(p => p.Plan == Plan.Business).Limits.OpenJobs);

        await using var check = PostgresFixture.CreateDbContext(org.ConnectionString);
        var changes = await check.AuditEntries.AcrossTenants().Where(a => a.OrganizationId == org.Id && a.Action == "organization.plan_changed").Select(a => a.Summary).ToListAsync(Ct);
        Assert.Equal(["Changed the plan from Free to Team"], changes);
    }

    [Fact]
    public async Task The_refusal_body_is_the_same_wherever_a_limit_is_hit()
    {
        await using var org = await FreeOrganizationAsync();
        await AddClientsAsync(org, 25);

        var body = await (await org.Owner.PostJsonAsync(org.Url("/clients"), new { name = "One Too Many" })).Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);

        Assert.Equal(
            ["allowed", "code", "limit", "plan", "status", "title", "traceId", "type", "usage"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
