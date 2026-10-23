using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Clients;
using TenantHub.Application.Jobs;
using TenantHub.Application.Paging;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Jobs;

/// <summary>US-30 to US-32 through the API: the workflow by two roles, resource rules, numbering and the database proofs.</summary>
public sealed class JobEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Jobs(Guid org) => $"/api/orgs/{org}/jobs";

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, BrowserClient.Json)!;
    }

    private static Task<HttpResponseMessage> TransitionAsync(BrowserClient client, Guid org, Guid job, string to, string? reason = null) =>
        client.PostJsonAsync($"{Jobs(org)}/{job}/transitions", new { to, reason });

    private static Dictionary<string, object?> Body(JobView job, object? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = job.Title,
            ["description"] = job.Description,
            ["clientId"] = job.ClientId,
            ["assigneeUserId"] = job.AssigneeUserId,
            ["priority"] = JsonNamingPolicy.CamelCase.ConvertName(job.Priority.ToString()),
            ["scheduledOn"] = job.ScheduledOn,
            ["dueOn"] = job.DueOn,
            ["quotedAmount"] = job.QuotedAmount,
            ["version"] = job.Version,
        };
        foreach (var property in changes?.GetType().GetProperties() ?? [])
        {
            body[property.Name] = property.GetValue(changes);
        }

        return body;
    }

    [Fact]
    public async Task An_owner_and_a_member_take_a_job_through_the_whole_workflow_and_the_history_shows_each_step()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var day = new DateOnly(2026, 11, 2);

        var created = await ReadAsync<JobView>(
            await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Spring clean", clientId = tenants.ClientA, quotedAmount = 240.5m }),
            HttpStatusCode.Created);
        Assert.Equal((JobStatus.New, (Guid?)null), (created.Status, created.AssigneeUserId));
        var schedule = Assert.Single(created.Transitions, t => t.To == JobStatus.Scheduled);
        Assert.Equal("Assign the job to someone first.", schedule.BlockedReason);

        var assigned = await ReadAsync<JobView>(await owner.SendJsonAsync(
            HttpMethod.Put,
            $"{Jobs(tenants.OrgA)}/{created.Id}",
            Body(created, new { assigneeUserId = memberId, scheduledOn = day, dueOn = day.AddDays(1) })));
        await ReadAsync<JobView>(await TransitionAsync(owner, tenants.OrgA, created.Id, "scheduled"));

        // The member sees only what they may do on their own job: start it, not cancel it.
        var asMember = await member.Http.GetFromJsonAsync<JobView>($"{Jobs(tenants.OrgA)}/{created.Id}", BrowserClient.Json, Ct);
        Assert.Equal([JobStatus.InProgress], asMember!.Transitions.Select(t => t.To));
        Assert.Equal((true, false, false), (asMember.CanEdit, asMember.CanAssign, asMember.CanDelete));
        await ReadAsync<JobView>(await TransitionAsync(member, tenants.OrgA, created.Id, "inProgress"));
        var completed = await ReadAsync<JobView>(await TransitionAsync(member, tenants.OrgA, created.Id, "completed"));
        Assert.NotNull(completed.CompletedAt);

        await ReadAsync<JobView>(await TransitionAsync(owner, tenants.OrgA, created.Id, "inProgress", "The client found streaks"));
        var cancelled = await ReadAsync<JobView>(await TransitionAsync(owner, tenants.OrgA, created.Id, "cancelled", "Contract ended"));
        Assert.Equal((JobStatus.Cancelled, (DateTimeOffset?)null), (cancelled.Status, cancelled.CompletedAt));
        Assert.Equal(created.Number, assigned.Number);

        var history = (await member.Http.GetFromJsonAsync<List<JobHistoryEntry>>($"{Jobs(tenants.OrgA)}/{created.Id}/history", BrowserClient.Json, Ct))!;
        Assert.Equal(
            [
                "job.created", "job.updated", "job.status_changed", "job.status_changed",
                "job.status_changed", "job.status_changed", "job.status_changed",
            ],
            history.Select(h => h.Action).Reverse());
        Assert.Equal($"Cancelled {created.Label}: Contract ended", history[0].Summary);
        Assert.Equal($"Reopened {created.Label}: The client found streaks", history[1].Summary);
        Assert.Contains(history, h => h.Changes.Contains(new Domain.FieldChange("assignee", null, tenants.EmailsA[Role.Member].Split('@')[0])));
        Assert.Equal(tenants.EmailsA[Role.Member].Split('@')[0], history[2].ActorName);
    }

    [Fact]
    public async Task A_member_cannot_act_on_someone_elses_job_and_the_refusals_say_why()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var adminsJob = await tenants.CreateJobAsync(tenants.OrgA, Role.Admin);
        var view = await member.Http.GetFromJsonAsync<JobView>($"{Jobs(tenants.OrgA)}/{adminsJob}", BrowserClient.Json, Ct);
        var before = await tenants.JobVersionAsync(adminsJob);

        await ProblemAssert.HasCodeAsync(await TransitionAsync(member, tenants.OrgA, adminsJob, "inProgress"), HttpStatusCode.Forbidden, "job-not-yours");
        await ProblemAssert.HasCodeAsync(
            await member.SendJsonAsync(HttpMethod.Put, $"{Jobs(tenants.OrgA)}/{adminsJob}", Body(view!, new { title = "Mine now" })),
            HttpStatusCode.Forbidden,
            "job-not-yours");
        await ProblemAssert.HasCodeAsync(
            await member.SendJsonAsync(HttpMethod.Delete, $"{Jobs(tenants.OrgA)}/{adminsJob}", null),
            HttpStatusCode.Forbidden,
            TenantResolutionFilter.PermissionDeniedCode);

        Assert.Empty(view!.Transitions);
        Assert.Equal((false, false, false), (view.CanEdit, view.CanAssign, view.CanDelete));
        Assert.Equal(before, await tenants.JobVersionAsync(adminsJob));
    }

    [Fact]
    public async Task A_member_edits_their_own_job_but_cannot_reassign_it_or_cancel_it()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var mine = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var view = await member.Http.GetFromJsonAsync<JobView>($"{Jobs(tenants.OrgA)}/{mine}", BrowserClient.Json, Ct);
        var adminId = await tenants.UserIdOfAsync(Role.Admin);

        var edited = await ReadAsync<JobView>(await member.SendJsonAsync(HttpMethod.Put, $"{Jobs(tenants.OrgA)}/{mine}", Body(view!, new { priority = "urgent" })));
        await ProblemAssert.HasCodeAsync(
            await member.SendJsonAsync(HttpMethod.Put, $"{Jobs(tenants.OrgA)}/{mine}", Body(edited, new { assigneeUserId = adminId })),
            HttpStatusCode.Forbidden,
            "manager-only");
        await ProblemAssert.HasCodeAsync(await TransitionAsync(member, tenants.OrgA, mine, "cancelled", "Not needed"), HttpStatusCode.Forbidden, "manager-only");

        Assert.Equal(JobPriority.Urgent, edited.Priority);
    }

    [Fact]
    public async Task A_member_creates_jobs_for_themselves_or_unassigned_but_cannot_assign_others()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var adminId = await tenants.UserIdOfAsync(Role.Admin);

        var unassigned = await member.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Found work", clientId = tenants.ClientA });
        var own = await member.PostJsonAsync(Jobs(tenants.OrgA), new { title = "My work", clientId = tenants.ClientA, assigneeUserId = memberId });
        var other = await member.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Their work", clientId = tenants.ClientA, assigneeUserId = adminId });

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (unassigned.StatusCode, own.StatusCode));
        await ProblemAssert.HasCodeAsync(other, HttpStatusCode.Forbidden, "manager-only");
    }

    [Fact]
    public async Task A_move_that_is_not_in_the_table_is_409_with_the_moves_that_are()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Owner);

        var refused = await ProblemAssert.IsProblemAsync(await TransitionAsync(owner, tenants.OrgA, job, "completed"), HttpStatusCode.Conflict);
        var noReason = await TransitionAsync(owner, tenants.OrgA, job, "cancelled", "   ");
        var notScheduled = await TransitionAsync(owner, tenants.OrgA, job, "scheduled");

        Assert.Equal("invalid-transition", refused.GetProperty("code").GetString());
        Assert.Equal(["scheduled", "inProgress", "cancelled"], refused.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
        await ProblemAssert.HasFieldErrorsAsync(noReason, "reason");
        await ProblemAssert.HasFieldErrorsAsync(notScheduled, "scheduledOn");
    }

    [Fact]
    public async Task References_must_be_this_organizations_active_client_and_active_members()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var archived = await tenants.CreateClientAsync(tenants.OrgA);
        await tenants.SetArchivedAsync(archived, archived: true);
        Guid bOwner;
        await using (var db = PostgresFixture.CreateDbContext(tenants.ConnectionString))
        {
            bOwner = await db.Users.Where(u => u.Email == tenants.OrgBOwnerEmail).Select(u => u.Id).SingleAsync(Ct);
        }

        await ProblemAssert.HasFieldErrorsAsync(await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Foreign client", clientId = tenants.ClientB }), "clientId");
        await ProblemAssert.HasFieldErrorsAsync(await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Archived client", clientId = archived }), "clientId");
        await ProblemAssert.HasFieldErrorsAsync(
            await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Foreign assignee", clientId = tenants.ClientA, assigneeUserId = bOwner }),
            "assigneeUserId");
        await ProblemAssert.HasFieldErrorsAsync(
            await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "x", clientId = tenants.ClientA, quotedAmount = 1.005m, dueOn = "2026-01-01", scheduledOn = "2026-01-02" }),
            "title",
            "quotedAmount",
            "dueOn");
    }

    [Fact]
    public async Task Saving_over_someone_elses_newer_change_is_a_conflict()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var job = await tenants.CreateJobAsync(tenants.OrgA);
        var seen = await owner.Http.GetFromJsonAsync<JobView>($"{Jobs(tenants.OrgA)}/{job}", BrowserClient.Json, Ct);

        var first = await admin.SendJsonAsync(HttpMethod.Put, $"{Jobs(tenants.OrgA)}/{job}", Body(seen!, new { title = "Admin's title" }));
        var second = await owner.SendJsonAsync(HttpMethod.Put, $"{Jobs(tenants.OrgA)}/{job}", Body(seen!, new { title = "Owner's title" }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ProblemAssert.HasCodeAsync(second, HttpStatusCode.Conflict, "concurrency-conflict");
        Assert.Equal("Admin's title", (await tenants.JobDetailsAsync(job)).Title);
    }

    [Fact]
    public async Task Only_new_or_cancelled_jobs_can_be_deleted()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var started = await tenants.CreateJobAsync(tenants.OrgA, Role.Owner);
        (await TransitionAsync(owner, tenants.OrgA, started, "inProgress")).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await owner.SendJsonAsync(HttpMethod.Delete, $"{Jobs(tenants.OrgA)}/{started}", null), HttpStatusCode.Conflict, "job-not-deletable");
        (await TransitionAsync(owner, tenants.OrgA, started, "cancelled", "Mistake")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"{Jobs(tenants.OrgA)}/{started}", null)).StatusCode);
        await ProblemAssert.HasCodeAsync(await owner.Http.GetAsync($"{Jobs(tenants.OrgA)}/{started}/history", Ct), HttpStatusCode.NotFound, "job-not-found");
    }

    [Fact]
    public async Task A_client_with_jobs_cannot_be_deleted_but_can_be_archived()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var client = await tenants.CreateClientAsync(tenants.OrgA);
        await ReadAsync<JobView>(await owner.PostJsonAsync(Jobs(tenants.OrgA), new { title = "Keeps the client", clientId = client }), HttpStatusCode.Created);

        await ProblemAssert.HasCodeAsync(
            await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/clients/{client}", null),
            HttpStatusCode.Conflict,
            "client-has-jobs");
        var archived = await ReadAsync<ClientView>(await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/clients/{client}/archive", new { }));
        Assert.Equal(Domain.Clients.ClientStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task Removing_a_member_unassigns_their_open_jobs_with_audit_entries_and_closed_jobs_keep_them()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var open = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var done = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        (await TransitionAsync(owner, tenants.OrgA, done, "inProgress")).EnsureSuccessStatusCode();
        (await TransitionAsync(owner, tenants.OrgA, done, "completed")).EnsureSuccessStatusCode();

        (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/members/{memberId}", null)).EnsureSuccessStatusCode();

        Assert.Null((await tenants.JobDetailsAsync(open)).AssigneeUserId);
        Assert.Null((await tenants.JobDetailsAsync(tenants.JobA)).AssigneeUserId);
        Assert.Equal(memberId, (await tenants.JobDetailsAsync(done)).AssigneeUserId);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var unassigned = await db.AuditEntries.AcrossTenants().Where(a => a.Action == "job.unassigned").Select(a => a.EntityId).ToListAsync(Ct);
        Assert.Equal(new[] { open, tenants.JobA }.Order(), unassigned.Order());
        Assert.False(await db.AuditEntries.AcrossTenants().AnyAsync(a => a.Action == "job.unassigned" && a.EntityId == done, Ct));
    }

    [Fact]
    public async Task Parallel_creates_get_consecutive_numbers_without_gaps_or_duplicates()
    {
        await using var tenants = await ApiTenants.CreateAsync(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        await owner.SessionAsync();
        await member.SessionAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
            (i % 2 == 0 ? owner : member).PostJsonAsync(Jobs(tenants.OrgA), new { title = $"Parallel {i}", clientId = tenants.ClientA })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var numbers = await db.Jobs.AcrossTenants().Where(j => j.OrganizationId == tenants.OrgA).Select(j => j.Number).OrderBy(n => n).ToListAsync(Ct);
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
        Assert.Equal(numbers.Count + 1, (await db.Organizations.SingleAsync(o => o.Id == tenants.OrgA, Ct)).NextJobNumber);
        // B's counter is its own.
        Assert.Equal(1, await db.Jobs.AcrossTenants().Where(j => j.OrganizationId == tenants.OrgB).MaxAsync(j => j.Number, Ct));
    }

    [Fact]
    public async Task The_database_refuses_a_job_that_points_at_another_organizations_client_or_member()
    {
        var tenants = await SharedApiTenants.GetAsync<JobEndpointTests>(postgres);
        await using var connection = new NpgsqlConnection(tenants.ConnectionString);
        await connection.OpenAsync(Ct);
        Guid ownerA;
        Guid ownerB;
        await using (var db = PostgresFixture.CreateDbContext(tenants.ConnectionString))
        {
            ownerA = await db.Users.Where(u => u.Email == tenants.EmailsA[Role.Owner]).Select(u => u.Id).SingleAsync(Ct);
            ownerB = await db.Users.Where(u => u.Email == tenants.OrgBOwnerEmail).Select(u => u.Id).SingleAsync(Ct);
        }

        // Raw SQL, past every application check: only the composite foreign keys stand in the way.
        async Task<PostgresException> InsertAsync(Guid clientId, Guid? assignee)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO jobs (id, organization_id, number, title, client_id, assignee_user_id, status, priority,
                                  created_at, created_by_user_id, updated_at)
                VALUES (gen_random_uuid(), @org, 99999, 'Smuggled', @client, @assignee, 'New', 'Normal', now(), @creator, now())
                """,
                connection);
            command.Parameters.AddWithValue("org", tenants.OrgA);
            command.Parameters.AddWithValue("client", clientId);
            command.Parameters.AddWithValue("assignee", (object?)assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("creator", ownerA);
            return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
        }

        var foreignClient = await InsertAsync(tenants.ClientB, null);
        var foreignAssignee = await InsertAsync(tenants.ClientA, ownerB);

        Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, "fk_jobs_clients_organization_id_client_id"), (foreignClient.SqlState, foreignClient.ConstraintName));
        Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, "fk_jobs_memberships_organization_id_assignee_user_id"), (foreignAssignee.SqlState, foreignAssignee.ConstraintName));
    }

    [Fact]
    public async Task Overdue_is_judged_in_each_organizations_own_time_zone()
    {
        // 11:30 UTC on 22 October: already the 23rd in Auckland, still the 22nd in Los Angeles.
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 22, 11, 30, 0, TimeSpan.Zero));
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<TimeProvider>(clock));
        var due = new DateOnly(2026, 10, 22);

        async Task<(JobView Job, PagedList<JobListItem> Overdue)> DueTodayAsync(string zone)
        {
            using var owner = new BrowserClient(factory);
            (await owner.SignUpAsync(BrowserClient.NewEmail(), $"Zone {zone.Replace('/', ' ')}", timeZone: zone)).EnsureSuccessStatusCode();
            var org = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;
            var client = await ReadAsync<ClientView>(await owner.PostJsonAsync($"/api/orgs/{org}/clients", new { name = "Zone client" }), HttpStatusCode.Created);
            var job = await ReadAsync<JobView>(await owner.PostJsonAsync(Jobs(org), new { title = "Due today", clientId = client.Id, dueOn = due }), HttpStatusCode.Created);
            var overdue = await owner.Http.GetFromJsonAsync<PagedList<JobListItem>>($"{Jobs(org)}?overdue=true", BrowserClient.Json, Ct);
            return (job, overdue!);
        }

        var auckland = await DueTodayAsync("Pacific/Auckland");
        var losAngeles = await DueTodayAsync("America/Los_Angeles");

        Assert.True(auckland.Job.IsOverdue);
        Assert.Equal([auckland.Job.Id], auckland.Overdue.Items.Select(i => i.Id));
        Assert.False(losAngeles.Job.IsOverdue);
        Assert.Empty(losAngeles.Overdue.Items);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
