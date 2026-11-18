using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Board;
using TenantHub.Application.Jobs;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Board;

/// <summary>
/// The dispatch board (SPEC-V2 4.3, 5.2, criterion 15; US-75): one week in the organization's zone, the queue of
/// unscheduled work, each person's load, and moves that cannot overwrite someone else's.
/// </summary>
public sealed class BoardTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly D(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_week_is_the_organizations_monday_to_sunday_across_a_daylight_saving_change()
    {
        // 23:30 UTC on Sunday 25 October 2026 is already 00:30 on Monday 26 October in Amsterdam (winter time from that day).
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-10-25T23:30:00Z", CultureInfo.InvariantCulture), "Europe/Amsterdam");
        var monday = await org.JobAsync("Monday before", D("2026-10-19"));
        var sunday = await org.JobAsync("Sunday of the change", D("2026-10-25"));
        var next = await org.JobAsync("Next Monday", D("2026-10-26"));

        var current = await org.BoardAsync();
        var previous = await org.BoardAsync("?week=2026-10-25"); // any day stands for its week

        Assert.Equal((D("2026-10-26"), D("2026-10-26")), (current.WeekStart, current.Today));
        Assert.Equal([next], current.Cards.Select(c => c.Id));
        Assert.Equal(D("2026-10-19"), previous.WeekStart);
        Assert.Equal([monday, sunday], previous.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task A_week_across_the_year_end_runs_from_monday_to_sunday()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-12-30T10:00:00Z", CultureInfo.InvariantCulture), "Pacific/Auckland");
        var inside = await org.JobAsync("Sunday 3 January", D("2027-01-03"));
        await org.JobAsync("Monday 4 January", D("2027-01-04"));
        var first = await org.JobAsync("Monday 28 December", D("2026-12-28"));

        var board = await org.BoardAsync("?week=2026-12-31");

        Assert.Equal(D("2026-12-28"), board.WeekStart);
        Assert.Equal([first, inside], board.Cards.Select(c => c.Id));
    }

    [Fact]
    public async Task The_board_shows_the_queue_the_load_and_the_people_who_can_do_the_work()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        await org.JobAsync("Morning", D("2026-11-17"), minutes: 300, assign: true);
        await org.JobAsync("Afternoon", D("2026-11-17"), minutes: 240, assign: true);
        var cancelled = await org.JobAsync("Called off", D("2026-11-17"), minutes: 600, assign: true);
        await org.TransitionAsync(cancelled, "cancelled", "Client away");
        await org.JobAsync("Nobody yet", D("2026-11-18"), minutes: 60);
        var later = await org.JobAsync("Due later", null, due: D("2026-12-01"));
        var sooner = await org.JobAsync("Due sooner", null, due: D("2026-11-20"));
        var undated = await org.JobAsync("No due date", null);

        var board = await org.BoardAsync();

        // Planned minutes per person and day, not counting the cancelled job; the unassigned day counts too.
        Assert.Equal(480, board.DailyCapacityMinutes);
        Assert.Contains(board.Load, l => l.AssigneeUserId == org.OwnerId && l.Day == D("2026-11-17") && l.Minutes == 540 && l.Jobs == 2);
        Assert.Contains(board.Load, l => l.AssigneeUserId == null && l.Day == D("2026-11-18") && l.Minutes == 60);
        // The queue: open jobs without a day, the nearest due first and those without a due date last.
        Assert.Equal([sooner, later, undated], board.Unscheduled.Select(c => c.Id));
        Assert.Equal(3, board.UnscheduledTotal);
        Assert.Equal([(org.OwnerId, true)], board.People.Select(p => (p.UserId, p.Active)));
        Assert.True(board.CanPlan);
        Assert.Equal("JOB-0001", board.Cards[0].Label);
    }

    [Fact]
    public async Task A_move_schedules_a_new_job_and_records_it()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        var job = await org.JobAsync("Window cleaning", null);
        var card = (await org.BoardAsync()).Unscheduled.Single();

        var moved = await ReadAsync<JobView>(await org.MoveAsync(job, org.OwnerId, D("2026-11-18"), card.Version));

        Assert.Equal((JobStatus.Scheduled, org.OwnerId, D("2026-11-18")), (moved.Status, moved.AssigneeUserId!.Value, moved.ScheduledOn!.Value));
        var history = await org.Owner.Http.GetFromJsonAsync<JsonElement>(org.Url($"/jobs/{job}/history"), BrowserClient.Json, Ct);
        var summaries = history.EnumerateArray().Select(e => e.GetProperty("summary").GetString()).ToList();
        Assert.Contains(summaries, s => s!.StartsWith("Moved JOB-0001 on the board", StringComparison.Ordinal));
        Assert.Contains(summaries, s => s!.Contains("from New to Scheduled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_move_made_meanwhile_wins_and_the_late_one_is_a_conflict_that_changes_nothing()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        var job = await org.JobAsync("Gutter clearing", D("2026-11-17"), assign: true);
        var seen = (await org.BoardAsync()).Cards.Single().Version;
        (await org.MoveAsync(job, org.OwnerId, D("2026-11-19"), seen)).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await org.MoveAsync(job, null, D("2026-11-20"), seen), HttpStatusCode.Conflict, "concurrency-conflict");

        var card = (await org.BoardAsync()).Cards.Single();
        Assert.Equal((org.OwnerId, D("2026-11-19")), (card.AssigneeUserId!.Value, card.ScheduledOn!.Value));
    }

    // SPEC-V2 5.2: a card can also go back to "Not scheduled", by the job form's rules (it keeps its person; a New
    // job given a person but no day stays New, since Scheduled needs a day).
    [Fact]
    public async Task A_card_taken_off_the_plan_goes_back_to_the_queue()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        var planned = await org.JobAsync("Carpet clean", D("2026-11-18"), assign: true, due: D("2026-11-18"));
        var waiting = await org.JobAsync("Window cleaning", null);
        await org.TransitionAsync(planned, "scheduled");
        var board = await org.BoardAsync();

        var unplanned = await ReadAsync<JobView>(await org.MoveAsync(planned, org.OwnerId, null, board.Cards.Single().Version));
        var assigned = await ReadAsync<JobView>(await org.MoveAsync(waiting, org.OwnerId, null, board.Unscheduled.Single().Version));

        Assert.Equal((JobStatus.Scheduled, org.OwnerId, (DateOnly?)null, D("2026-11-18")), (unplanned.Status, unplanned.AssigneeUserId!.Value, unplanned.ScheduledOn, unplanned.DueOn!.Value));
        Assert.Equal((JobStatus.New, org.OwnerId), (assigned.Status, assigned.AssigneeUserId!.Value));
        var after = await org.BoardAsync();
        Assert.Empty(after.Cards);
        Assert.Equal(new[] { planned, waiting }.Order(), after.Unscheduled.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Refused_moves_say_why()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        var done = await org.JobAsync("Finished", D("2026-11-16"), assign: true);
        await org.TransitionAsync(done, "inProgress");
        await org.TransitionAsync(done, "completed");
        var due = await org.JobAsync("Due Wednesday", D("2026-11-17"), due: D("2026-11-18"), assign: true);
        var board = await org.BoardAsync();
        uint Version(Guid id) => board.Cards.Single(c => c.Id == id).Version;

        await ProblemAssert.HasCodeAsync(await org.MoveAsync(done, org.OwnerId, D("2026-11-18"), Version(done)), HttpStatusCode.Conflict, "job-closed");
        await ProblemAssert.HasCodeAsync(await org.MoveAsync(due, org.OwnerId, D("2026-11-20"), Version(due)), HttpStatusCode.Conflict, "after-due-date");
        await ProblemAssert.HasFieldErrorsAsync(await org.MoveAsync(due, Guid.NewGuid(), D("2026-11-17"), Version(due)), "assigneeUserId");

        var moved = await ReadAsync<JobView>(await org.MoveAsync(due, org.OwnerId, D("2026-11-20"), Version(due), moveDueDate: true));
        Assert.Equal((D("2026-11-20"), D("2026-11-20")), (moved.ScheduledOn!.Value, moved.DueOn!.Value));
    }

    [Fact]
    public async Task Members_and_viewers_read_the_board_but_cannot_move()
    {
        var tenants = await SharedApiTenants.GetAsync<BoardTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var viewer = await tenants.ClientForAsync(Role.Viewer);

        var memberBoard = await member.Http.GetFromJsonAsync<BoardView>($"/api/orgs/{tenants.OrgA}/board", BrowserClient.Json, Ct);
        var viewerBoard = await viewer.Http.GetFromJsonAsync<BoardView>($"/api/orgs/{tenants.OrgA}/board", BrowserClient.Json, Ct);

        Assert.False(memberBoard!.CanPlan);
        Assert.False(viewerBoard!.CanPlan);
        // The viewer is not a row: viewers do no work (permission map, D008).
        Assert.DoesNotContain(memberBoard.People, p => p.Name.Contains("viewer", StringComparison.OrdinalIgnoreCase) && p.Active);
        await ProblemAssert.HasCodeAsync(
            await member.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs/{tenants.JobA}/schedule", new { scheduledOn = "2026-11-18", version = 0 }),
            HttpStatusCode.Forbidden,
            "permission-denied");
    }

    [Fact]
    public async Task Moving_a_visit_by_hand_takes_it_out_of_its_agreements_reach()
    {
        await using var org = await OrgAsync(DateTimeOffset.Parse("2026-11-16T09:00:00Z", CultureInfo.InvariantCulture), "UTC");
        var agreement = await ReadAsync<JsonElement>(await org.Owner.PostJsonAsync(org.Url("/agreements"), new
        {
            clientId = org.ClientId,
            title = "Weekly office cleaning",
            recurrence = new { kind = "weekly", interval = 1, weekdays = new[] { "tuesday" } },
            startsOn = "2026-11-17",
        }));
        var card = (await org.BoardAsync()).Cards.First(c => c.IsVisit);

        // A visit is due on its own date, so a later day moves the due date along, as the board asks.
        var moved = await ReadAsync<JobView>(await org.MoveAsync(card.Id, org.OwnerId, D("2026-11-18"), card.Version, moveDueDate: true));

        Assert.True(moved.ChangedByHand);
        Assert.Equal(agreement.GetProperty("id").GetGuid(), moved.AgreementId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, BrowserClient.Json)!;
    }

    private async Task<Org> OrgAsync(DateTimeOffset now, string timeZone)
    {
        var clock = new MovingClock(now);
        var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<TimeProvider>(clock));
        var owner = new BrowserClient(factory);
        (await owner.SignUpAsync(BrowserClient.NewEmail(), timeZone: timeZone)).EnsureSuccessStatusCode();
        var session = (await owner.SessionAsync()).User!;
        var id = session.Memberships.Single().OrganizationId;
        var client = await ReadAsync<Application.Clients.ClientView>(await owner.PostJsonAsync($"/api/orgs/{id}/clients", new { name = "Acme Offices" }));
        return new Org(factory, owner, id, session.Id, client.Id);
    }

    private sealed record Org(ApiFactory Factory, BrowserClient Owner, Guid Id, Guid OwnerId, Guid ClientId) : IAsyncDisposable
    {
        public string Url(string path = "") => $"/api/orgs/{Id}{path}";

        public async Task<Guid> JobAsync(string title, DateOnly? scheduledOn, int? minutes = null, bool assign = false, DateOnly? due = null) =>
            (await ReadAsync<JobView>(await Owner.PostJsonAsync(Url("/jobs"), new
            {
                title,
                clientId = ClientId,
                assigneeUserId = assign ? OwnerId : (Guid?)null,
                scheduledOn,
                dueOn = due,
                estimatedMinutes = minutes,
            }))).Id;

        public async Task TransitionAsync(Guid job, string to, string? reason = null) =>
            (await Owner.PostJsonAsync(Url($"/jobs/{job}/transitions"), new { to, reason })).EnsureSuccessStatusCode();

        public async Task<BoardView> BoardAsync(string query = "") =>
            (await Owner.Http.GetFromJsonAsync<BoardView>(Url($"/board{query}"), BrowserClient.Json, Ct))!;

        public Task<HttpResponseMessage> MoveAsync(Guid job, Guid? assignee, DateOnly? day, uint version, bool moveDueDate = false) =>
            Owner.PostJsonAsync(Url($"/jobs/{job}/schedule"), new { assigneeUserId = assignee, scheduledOn = day, version, moveDueDate });

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
