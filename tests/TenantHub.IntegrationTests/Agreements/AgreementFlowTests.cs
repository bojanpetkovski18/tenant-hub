using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TenantHub.Application.Agreements;
using TenantHub.Application.Jobs;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Background;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Agreements;

/// <summary>
/// SPEC-V2 5.1 and criterion 14 through the real API and database: generation on the organization's dates, never
/// twice, edits that reach only untouched future visits, skip, end, resume, and the plan limits.
/// </summary>
public sealed class AgreementFlowTests(PostgresFixture postgres)
{
    // A Monday at 09:00 UTC, which is the same Monday in Europe/Amsterdam.
    private static readonly DateTimeOffset Monday = new(2027, 1, 4, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly D(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_weekly_agreement_fills_60_days_of_visits_audited_as_the_system()
    {
        await using var org = await OrgAsync(Monday);

        var created = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-04"), assignee: true);

        var visits = await org.VisitsAsync(created.Id);
        Assert.Equal(9, visits.Count); // the Mondays from 4 January to 1 March, within today + 60 days (5 March)
        Assert.Equal((D("2027-01-04"), D("2027-03-01")), (visits[0].OccurrenceOn!.Value, visits[^1].OccurrenceOn!.Value));
        Assert.All(visits, v => Assert.Equal((JobStatus.Scheduled, v.OccurrenceOn, v.OccurrenceOn, 90), (v.Status, v.ScheduledOn, v.DueOn, v.EstimatedMinutes!.Value)));
        Assert.Equal([.. Enumerable.Range(1, 9)], visits.Select(v => v.Number).Order());
        Assert.Equal((AgreementStatus.Active, D("2027-03-05")), (created.Status, created.GeneratedThrough!.Value));
        Assert.Equal(8, created.UpcomingVisits.Count);

        await using var db = PostgresFixture.CreateDbContext(org.ConnectionString);
        var entries = await db.AuditEntries.AcrossTenants().Where(e => e.OrganizationId == org.Id && e.EntityType != "organization" && e.EntityType != "client").ToListAsync(Ct);
        var agreementEntry = Assert.Single(entries, e => e.Action == "agreement.created");
        Assert.Equal((ActorKind.User, "Olivia Bennett"), (agreementEntry.ActorKind, agreementEntry.ActorName));
        Assert.Equal(9, entries.Count(e => e is { Action: "job.created", ActorKind: ActorKind.System, ActorName: "System (agreement Office cleaning)", ActorUserId: null }));

        // The jobs list filters by agreement, and the daily task row exists for the organization.
        var list = await org.Owner.Http.GetFromJsonAsync<System.Text.Json.JsonElement>(org.Url($"/jobs?agreementId={created.Id}&pageSize=50"), BrowserClient.Json, Ct);
        Assert.Equal(9, list.GetProperty("total").GetInt32());
        Assert.Equal(1, await db.ScheduledTasks.CountAsync(t => t.Name == IRecurringWork.DailyGeneration && t.OrganizationId == org.Id, Ct));
    }

    [Fact]
    public async Task Running_generation_again_and_on_two_schedulers_at_once_creates_each_visit_once()
    {
        const int rounds = 5;
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Monday);
        await using var first = Factory(connectionString, clock);
        await using var second = Factory(connectionString, clock);
        await using var org = await OrgAsync(Monday, first, connectionString, clock, plan: Plan.Business);
        var agreement = await org.CreateAsync(Weekly("monday", "wednesday", "friday"), startsOn: D("2027-01-04"));
        var runs = new List<int>();

        for (var round = 0; round < rounds; round++)
        {
            // A day later each round: one more day enters the horizon, and both instances wake for it.
            clock.Advance(TimeSpan.FromDays(1));
            var made = await Task.WhenAll(Task.Run(() => RunDueAsync(first), Ct), Task.Run(() => RunDueAsync(second), Ct));
            runs.Add(made.Sum());
        }

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var dates = await db.Jobs.AcrossTenants().Where(j => j.AgreementId == agreement.Id).Select(j => j.OccurrenceOn!.Value).ToListAsync(Ct);
        Assert.Equal(dates.Count, dates.Distinct().Count());
        Assert.Equal(Enumerable.Repeat(1, rounds), runs); // one of the two instances ran the organization's task each day
        var today = D("2027-01-09");
        var expected = new Schedule(Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday]), D("2027-01-04"), null, null)
            .Occurrences(D("2027-01-04"), today.AddDays(VisitGeneration.HorizonDays));
        Assert.Equal(expected, dates.Order());

        // The unique index is the backstop: the same occurrence twice is refused by the database itself.
        await using var connection = new NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(connectionString));
        await connection.OpenAsync(Ct);
        await using var duplicate = new NpgsqlCommand(
            """
            INSERT INTO jobs (id, organization_id, number, title, client_id, status, priority, created_at, created_by_user_id, updated_at, agreement_id, occurrence_on, changed_by_hand)
            SELECT gen_random_uuid(), organization_id, 9999, title, client_id, status, priority, created_at, created_by_user_id, updated_at, agreement_id, occurrence_on, false
            FROM jobs WHERE agreement_id = @agreement LIMIT 1
            """,
            connection);
        duplicate.Parameters.AddWithValue("agreement", agreement.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync(Ct));
        Assert.Equal(("23505", "ux_jobs_agreement_occurrence"), (error.SqlState, error.ConstraintName));
    }

    [Theory]
    // Auckland: 23:59 on 3 April 2027 (NZDT, +13) and 00:01 on 4 April, the day daylight saving ends there.
    [InlineData("Pacific/Auckland", "2027-04-03T10:59:00Z", "2027-04-03")]
    [InlineData("Pacific/Auckland", "2027-04-03T11:01:00Z", "2027-04-04")]
    // After the change (NZST, +12): 23:30 on 4 April and 00:30 on 5 April.
    [InlineData("Pacific/Auckland", "2027-04-04T11:30:00Z", "2027-04-04")]
    [InlineData("Pacific/Auckland", "2027-04-04T12:30:00Z", "2027-04-05")]
    // Santiago skips midnight on 6 September 2026: 23:59 on the 5th (-4), then the 6th begins at 01:00 (-3).
    [InlineData("America/Santiago", "2026-09-06T03:59:00Z", "2026-09-05")]
    [InlineData("America/Santiago", "2026-09-06T04:00:00Z", "2026-09-06")]
    public async Task Generation_uses_the_organizations_today_around_midnight_and_daylight_saving(string zone, string instant, string localToday)
    {
        var now = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);
        await using var org = await OrgAsync(now, timeZone: zone, plan: Plan.Business);

        var created = await org.CreateAsync(Daily(), startsOn: D(localToday));
        var dates = (await org.VisitsAsync(created.Id)).Select(v => v.OccurrenceOn!.Value).ToList();

        Assert.Equal(D(localToday), dates.Min());
        Assert.Equal(D(localToday).AddDays(VisitGeneration.HorizonDays), dates.Max());
        Assert.Equal(VisitGeneration.HorizonDays + 1, dates.Count);
        await ProblemAssert.HasFieldErrorsAsync(await org.PostAsync("/agreements", Body(Daily(), D(localToday).AddDays(-1), clientId: org.ClientId)), "startsOn");
    }

    [Fact]
    public async Task The_daily_run_is_due_just_after_local_midnight_and_catches_up_once_after_downtime()
    {
        var now = DateTimeOffset.Parse("2027-04-03T10:00:00Z", CultureInfo.InvariantCulture); // 23:00 in Auckland
        await using var org = await OrgAsync(now, timeZone: "Pacific/Auckland", plan: Plan.Business);
        var created = await org.CreateAsync(Daily(), startsOn: D("2027-04-03"));

        await using (var db = PostgresFixture.CreateDbContext(org.ConnectionString))
        {
            // 00:05 on 4 April in Auckland, still NZDT (+13).
            var task = await db.ScheduledTasks.AsNoTracking().SingleAsync(t => t.OrganizationId == org.Id, Ct);
            Assert.Equal(DateTimeOffset.Parse("2027-04-03T11:05:00Z", CultureInfo.InvariantCulture), task.DueAt);
        }

        // Ten days of downtime: one run on waking covers today's horizon, then the next is due after the next midnight.
        org.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(1, await RunDueAsync(org.Factory));
        Assert.Equal(0, await RunDueAsync(org.Factory));

        var dates = (await org.VisitsAsync(created.Id)).Select(v => v.OccurrenceOn!.Value).ToList();
        Assert.Equal(D("2027-04-13").AddDays(VisitGeneration.HorizonDays), dates.Max());
        Assert.Equal(dates.Count, dates.Distinct().Count());
        await using var check = PostgresFixture.CreateDbContext(org.ConnectionString);
        Assert.Equal(
            DateTimeOffset.Parse("2027-04-13T12:05:00Z", CultureInfo.InvariantCulture), // 00:05 on 14 April, NZST (+12)
            (await check.ScheduledTasks.AsNoTracking().SingleAsync(t => t.OrganizationId == org.Id, Ct)).DueAt);
    }

    [Fact]
    public async Task An_edit_changes_untouched_future_visits_and_keeps_past_started_and_hand_changed_ones()
    {
        await using var org = await OrgAsync(Monday);
        var agreement = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-04"));
        var visits = await org.VisitsAsync(agreement.Id);
        var past = visits[0]; // 4 January, which is past once the clock moves
        var byHand = visits[2]; // 18 January
        var started = visits[3]; // 25 January
        var job = await org.GetJobAsync(byHand.Id);
        (await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url($"/jobs/{byHand.Id}"), new { title = "Office cleaning, and the windows", clientId = org.ClientId, scheduledOn = job.ScheduledOn, dueOn = job.DueOn, version = job.Version })).EnsureSuccessStatusCode();
        var startedJob = await org.GetJobAsync(started.Id);
        (await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url($"/jobs/{started.Id}"), new { title = startedJob.Title, clientId = org.ClientId, assigneeUserId = org.OwnerId, scheduledOn = startedJob.ScheduledOn, dueOn = startedJob.DueOn, version = startedJob.Version })).EnsureSuccessStatusCode();
        (await org.Owner.PostJsonAsync(org.Url($"/jobs/{started.Id}/transitions"), new { to = "inProgress" })).EnsureSuccessStatusCode();
        await org.AdvanceAsync(TimeSpan.FromDays(8)); // Tuesday 12 January

        var edited = await org.UpdateAsync(agreement, Weekly("monday", interval: 2), title: "Deep office cleaning", startsOn: D("2027-01-04"));

        var after = (await org.VisitsAsync(agreement.Id)).ToDictionary(v => v.OccurrenceOn!.Value);
        Assert.Equal("Office cleaning", after[D("2027-01-04")].Title); // past
        Assert.Equal("Office cleaning, and the windows", after[D("2027-01-18")].Title); // by hand, although its date stays
        Assert.Equal((JobStatus.InProgress, "Office cleaning"), (after[D("2027-01-25")].Status, after[D("2027-01-25")].Title)); // started, off-schedule now
        Assert.Equal("Deep office cleaning", after[D("2027-02-01")].Title); // untouched and still on the schedule
        Assert.Equal("Office cleaning", after[D("2027-01-11")].Title); // past too
        Assert.DoesNotContain(D("2027-02-08"), after.Keys); // untouched, dropped by the fortnightly schedule
        Assert.DoesNotContain(D("2027-02-22"), after.Keys);
        Assert.Equal("Deep office cleaning", after[D("2027-03-01")].Title);
        // Both were edited by hand (the started one to give it an assignee), so the page lists both, in date order.
        Assert.Equal([byHand.Id, started.Id], edited.ChangedByHand.Select(v => v.Id));

        await using var db = PostgresFixture.CreateDbContext(org.ConnectionString);
        Assert.Contains(await db.AuditEntries.AcrossTenants().Where(e => e.Action == "job.deleted" && e.OrganizationId == org.Id).Select(e => e.ActorName).ToListAsync(Ct), name => name == "System (agreement Deep office cleaning)");
    }

    [Fact]
    public async Task A_skipped_visit_is_cancelled_and_never_generated_again()
    {
        await using var org = await OrgAsync(Monday);
        var agreement = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-04"));
        var visit = (await org.VisitsAsync(agreement.Id))[1];

        var skipped = await ReadAsync<JobView>(await org.Owner.PostJsonAsync(org.Url($"/jobs/{visit.Id}/skip"), new { }));
        org.Clock.Advance(TimeSpan.FromDays(1));
        await RunDueAsync(org.Factory);

        Assert.Equal(JobStatus.Cancelled, skipped.Status);
        var onThatDay = (await org.VisitsAsync(agreement.Id)).Where(v => v.OccurrenceOn == visit.OccurrenceOn).ToList();
        Assert.Equal(JobStatus.Cancelled, Assert.Single(onThatDay).Status);
        var hand = await ReadAsync<JobView>(await org.Owner.PostJsonAsync(org.Url("/jobs"), new { title = "One-off", clientId = org.ClientId }));
        await ProblemAssert.HasCodeAsync(await org.Owner.PostJsonAsync(org.Url($"/jobs/{hand.Id}/skip"), new { }), HttpStatusCode.Conflict, "not-a-visit");
    }

    [Fact]
    public async Task Ending_cancels_new_and_scheduled_future_visits_and_keeps_the_rest()
    {
        await using var org = await OrgAsync(Monday);
        var agreement = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-04"), assignee: true);
        var visits = await org.VisitsAsync(agreement.Id);
        (await org.Owner.PostJsonAsync(org.Url($"/jobs/{visits[1].Id}/transitions"), new { to = "inProgress" })).EnsureSuccessStatusCode();
        org.Clock.Advance(TimeSpan.FromDays(1));

        var ended = await ReadAsync<AgreementView>(await org.Owner.PostJsonAsync(org.Url($"/agreements/{agreement.Id}/end"), new { }));

        var after = await org.VisitsAsync(agreement.Id);
        Assert.Equal(AgreementStatus.Ended, ended.Status);
        Assert.Equal(JobStatus.Scheduled, after.Single(v => v.Id == visits[0].Id).Status); // 4 January, now past
        Assert.Equal(JobStatus.InProgress, after.Single(v => v.Id == visits[1].Id).Status);
        Assert.All(after.Skip(2), v => Assert.Equal(JobStatus.Cancelled, v.Status));
        await ProblemAssert.HasCodeAsync(await org.PostAsync($"/agreements/{agreement.Id}/resume", new { }), HttpStatusCode.Conflict, "agreement-status");
    }

    [Fact]
    public async Task Resuming_generates_from_today_and_never_back_fills()
    {
        await using var org = await OrgAsync(Monday);
        var agreement = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-04"), endsOn: null);
        (await org.Owner.PostJsonAsync(org.Url($"/agreements/{agreement.Id}/pause"), new { })).EnsureSuccessStatusCode();
        await DeleteFutureVisitsAsync(org, agreement.Id); // as if the agreement had been paused before any were made
        await org.AdvanceAsync(TimeSpan.FromDays(21)); // Monday 25 January

        await RunDueAsync(org.Factory);
        Assert.DoesNotContain(await org.VisitsAsync(agreement.Id), v => v.OccurrenceOn > D("2027-01-04"));
        (await org.Owner.PostJsonAsync(org.Url($"/agreements/{agreement.Id}/resume"), new { })).EnsureSuccessStatusCode();

        var dates = (await org.VisitsAsync(agreement.Id)).Select(v => v.OccurrenceOn!.Value).Where(d => d > D("2027-01-04")).ToList();
        Assert.Equal(D("2027-01-25"), dates.Min());
        Assert.DoesNotContain(D("2027-01-11"), dates);
        Assert.DoesNotContain(D("2027-01-18"), dates);
    }

    [Fact]
    public async Task Generation_stops_at_the_open_jobs_limit_and_says_so()
    {
        await using var org = await OrgAsync(Monday, plan: Plan.Free);

        // Every day for 61 days, on a plan of 50 open jobs.
        var created = await org.CreateAsync(Daily(), startsOn: D("2027-01-04"));

        Assert.Equal(50, (await org.VisitsAsync(created.Id)).Count);
        Assert.Equal((AgreementStatus.Active, GenerationHold.PlanLimit), (created.Status, created.PausedReason!.Value));
        await using var db = PostgresFixture.CreateDbContext(org.ConnectionString);
        var held = await db.AuditEntries.AcrossTenants().SingleAsync(e => e.OrganizationId == org.Id && e.Action == "agreement.generation_held", Ct);
        Assert.Equal(ActorKind.System, held.ActorKind);

        // Room again (15 visits closed): the next day's run creates the 12 dates still missing and lifts the hold.
        foreach (var visit in (await org.VisitsAsync(created.Id)).Take(15))
        {
            (await org.Owner.PostJsonAsync(org.Url($"/jobs/{visit.Id}/transitions"), new { to = "cancelled", reason = "Closed for the test" })).EnsureSuccessStatusCode();
        }

        org.Clock.Advance(TimeSpan.FromDays(1));
        await RunDueAsync(org.Factory);
        var after = await org.GetAgreementAsync(created.Id);
        Assert.Null(after.PausedReason);
        var all = await org.VisitsAsync(created.Id);
        Assert.Equal((62, D("2027-03-06")), (all.Count, all.Max(v => v.OccurrenceOn!.Value)));
    }

    [Fact]
    public async Task The_active_agreements_limit_holds_under_concurrency()
    {
        // Repeated: two creations racing for the last of three active agreements on Free.
        for (var round = 0; round < 3; round++)
        {
            await using var org = await OrgAsync(Monday, plan: Plan.Free);
            await org.CreateAsync(Weekly("monday"), startsOn: D("2027-02-01"));
            await org.CreateAsync(Weekly("tuesday"), startsOn: D("2027-02-01"));

            var responses = await Task.WhenAll(
                org.PostAsync("/agreements", Body(Weekly("wednesday"), D("2027-02-01"), clientId: org.ClientId)),
                org.PostAsync("/agreements", Body(Weekly("thursday"), D("2027-02-01"), clientId: org.ClientId)));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            await ProblemAssert.HasCodeAsync(responses.Single(r => r.StatusCode != HttpStatusCode.Created), HttpStatusCode.Forbidden, "plan-limit-reached");
            await using var db = PostgresFixture.CreateDbContext(org.ConnectionString);
            Assert.Equal(3, await db.Agreements.AcrossTenants().CountAsync(a => a.OrganizationId == org.Id, Ct));
        }
    }

    [Fact]
    public async Task Archiving_the_client_pauses_its_agreements_and_resuming_needs_it_active()
    {
        await using var org = await OrgAsync(Monday);
        var agreement = await org.CreateAsync(Weekly("monday"), startsOn: D("2027-01-11"));

        (await org.Owner.PostJsonAsync(org.Url($"/clients/{org.ClientId}/archive"), new { })).EnsureSuccessStatusCode();

        var paused = await org.GetAgreementAsync(agreement.Id);
        Assert.Equal((AgreementStatus.Paused, GenerationHold.ClientArchived), (paused.Status, paused.PausedReason!.Value));
        await ProblemAssert.HasCodeAsync(await org.PostAsync($"/agreements/{agreement.Id}/resume", new { }), HttpStatusCode.Conflict, "client-archived");
    }

    [Fact]
    public async Task Removing_the_assignee_clears_the_template_and_unassigns_future_visits()
    {
        var tenants = await SharedApiTenants.GetAsync<AgreementFlowTests>(postgres);
        var owner = await tenants.ClientForAsync(Domain.Memberships.Role.Owner);
        var member = await tenants.CreateMemberAsync(tenants.OrgA);
        var created = await ReadAsync<AgreementView>(await owner.PostJsonAsync(
            $"/api/orgs/{tenants.OrgA}/agreements",
            Body(Weekly("friday"), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2), tenants.ClientA, assignee: member)));

        (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/members/{member}", null)).EnsureSuccessStatusCode();

        var after = (await owner.Http.GetFromJsonAsync<AgreementView>($"/api/orgs/{tenants.OrgA}/agreements/{created.Id}", BrowserClient.Json, Ct))!;
        Assert.Null(after.AssigneeUserId);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var visits = await db.Jobs.AcrossTenants().Where(j => j.AgreementId == created.Id).ToListAsync(Ct);
        Assert.NotEmpty(visits);
        Assert.All(visits, v => Assert.Null(v.AssigneeUserId));
        Assert.Contains(await db.AuditEntries.AcrossTenants().Where(e => e.EntityId == created.Id).Select(e => e.Action).ToListAsync(Ct), a => a == "agreement.unassigned");
    }

    [Fact]
    public async Task The_database_refuses_an_agreement_or_a_visit_pointing_into_another_organization()
    {
        var tenants = await SharedApiTenants.GetAsync<AgreementFlowTests>(postgres);
        await using var connection = new NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(tenants.ConnectionString));
        await connection.OpenAsync(Ct);

        // A's agreement for B's client.
        await using var agreement = new NpgsqlCommand(
            """
            INSERT INTO agreements (id, organization_id, client_id, title, priority, recurrence, starts_on, status, created_at, created_by_user_id, updated_at)
            SELECT gen_random_uuid(), @a, @clientB, 'Cross', 'Normal', '{"kind":"weekly","interval":1,"weekdays":["monday"]}', current_date, 'Active', now(), created_by_user_id, now()
            FROM agreements WHERE id = @agreementA
            """,
            connection);
        agreement.Parameters.AddWithValue("a", tenants.OrgA);
        agreement.Parameters.AddWithValue("clientB", tenants.ClientB);
        agreement.Parameters.AddWithValue("agreementA", tenants.AgreementA);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => agreement.ExecuteNonQueryAsync(Ct))).SqlState);

        // A's job as a visit of B's agreement.
        await using var visit = new NpgsqlCommand("UPDATE jobs SET agreement_id = @agreementB, occurrence_on = current_date WHERE id = @jobA", connection);
        visit.Parameters.AddWithValue("agreementB", tenants.AgreementB);
        visit.Parameters.AddWithValue("jobA", tenants.JobA);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(() => visit.ExecuteNonQueryAsync(Ct))).SqlState);
    }

    // ---- helpers ----

    private static object Weekly(params string[] days) => new { kind = "weekly", interval = 1, weekdays = days };

    private static object Weekly(string day, int interval) => new { kind = "weekly", interval, weekdays = new[] { day } };

    private static object Daily() => Weekly("monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday");

    private static object Body(object recurrence, DateOnly startsOn, Guid? clientId = null, string title = "Office cleaning", Guid? assignee = null, DateOnly? endsOn = null, uint? version = null) =>
        new { clientId, title, assigneeUserId = assignee, estimatedMinutes = 90, quotedAmount = 120m, recurrence, startsOn, endsOn, version };

    private static ApiFactory Factory(string connectionString, MovingClock clock) =>
        new(connectionString, services: s => s.AddSingleton<TimeProvider>(clock));

    private static Task<int> RunDueAsync(ApiFactory factory) => factory.Services.GetRequiredService<Scheduler>().RunDueAsync(Ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return System.Text.Json.JsonSerializer.Deserialize<T>(body, BrowserClient.Json)!;
    }

    private static async Task DeleteFutureVisitsAsync(Org org, Guid agreementId)
    {
        await using var connection = new NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(org.ConnectionString));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("DELETE FROM jobs WHERE agreement_id = @id AND occurrence_on > '2027-01-04'", connection);
        command.Parameters.AddWithValue("id", agreementId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Org> OrgAsync(DateTimeOffset now, ApiFactory? factory = null, string? connectionString = null, MovingClock? clock = null, string timeZone = "Europe/Amsterdam", Plan plan = Plan.Team)
    {
        connectionString ??= await postgres.CreateDatabaseAsync();
        clock ??= new MovingClock(now);
        var ownsFactory = factory is null;
        factory ??= Factory(connectionString, clock);
        var owner = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email, timeZone: timeZone)).EnsureSuccessStatusCode();
        var session = (await owner.SessionAsync()).User!;
        var id = session.Memberships.Single().OrganizationId;
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == id, Ct);
            db.Entry(organization).Property(o => o.Plan).CurrentValue = plan;
            await db.SaveChangesAsync(Ct);
        }

        var client = await ReadAsync<Application.Clients.ClientView>(await owner.PostJsonAsync($"/api/orgs/{id}/clients", new { name = "Acme Offices" }));
        return new Org(factory, ownsFactory, owner, email, id, session.Id, client.Id, connectionString, clock);
    }

    private sealed record Org(ApiFactory Factory, bool OwnsFactory, BrowserClient Owner, string Email, Guid Id, Guid OwnerId, Guid ClientId, string ConnectionString, MovingClock Clock) : IAsyncDisposable
    {
        public string Url(string path = "") => $"/api/orgs/{Id}{path}";

        /// <summary>Moves the clock; past a week the session cookie has expired, so the owner signs in again.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            Clock.Advance(by);
            await Owner.SessionAsync(); // a fresh XSRF token for the signed-out browser, as the SPA gets on load
            (await Owner.SignInAsync(Email)).EnsureSuccessStatusCode();
        }

        public Task<HttpResponseMessage> PostAsync(string path, object body) => Owner.PostJsonAsync(Url(path), body);

        public async Task<AgreementView> CreateAsync(object recurrence, DateOnly startsOn, bool assignee = false, DateOnly? endsOn = null) =>
            await ReadAsync<AgreementView>(await PostAsync("/agreements", Body(recurrence, startsOn, ClientId, assignee: assignee ? OwnerId : null, endsOn: endsOn)));

        public async Task<AgreementView> UpdateAsync(AgreementView agreement, object recurrence, string title, DateOnly startsOn) =>
            await ReadAsync<AgreementView>(await Owner.SendJsonAsync(HttpMethod.Put, Url($"/agreements/{agreement.Id}"), Body(recurrence, startsOn, title: title, version: (await GetAgreementAsync(agreement.Id)).Version)));

        public async Task<AgreementView> GetAgreementAsync(Guid id) =>
            (await Owner.Http.GetFromJsonAsync<AgreementView>(Url($"/agreements/{id}"), BrowserClient.Json, Ct))!;

        public async Task<JobView> GetJobAsync(Guid id) =>
            (await Owner.Http.GetFromJsonAsync<JobView>(Url($"/jobs/{id}"), BrowserClient.Json, Ct))!;

        /// <summary>The agreement's visits in date order, read as the owner of the database.</summary>
        public async Task<List<Job>> VisitsAsync(Guid agreementId)
        {
            await using var db = PostgresFixture.CreateDbContext(ConnectionString);
            return await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.AgreementId == agreementId).OrderBy(j => j.OccurrenceOn).ToListAsync(Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            if (OwnsFactory)
            {
                await Factory.DisposeAsync();
            }
        }
    }
}
