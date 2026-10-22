using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Jobs;

/// <summary>SPEC 5.4 as tables: every cell of the transition table, who may act, required fields and boundaries.</summary>
public sealed class JobTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid ClientId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000c1");
    private static readonly Guid OtherClientId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000c2");
    private static readonly Guid ManagerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    private static readonly Guid WorkerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000002");
    private static readonly Guid OtherWorkerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static readonly JobActor Manager = JobActor.From(ManagerId, Role.Admin);
    private static readonly JobActor Worker = JobActor.From(WorkerId, Role.Member);
    private static readonly JobActor OtherWorker = JobActor.From(OtherWorkerId, Role.Member);
    private static readonly JobActor Viewer = JobActor.From(Guid.NewGuid(), Role.Viewer);

    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string>
    {
        [ClientId] = "Acme Offices",
        [OtherClientId] = "Birch Dental",
        [WorkerId] = "Maya Torres",
        [OtherWorkerId] = "Victor Lindqvist",
        [ManagerId] = "Adam Clarke",
    };

    private static JobDetails Details(
        Guid? assignee = null,
        DateOnly? scheduledOn = null,
        DateOnly? dueOn = null,
        decimal? amount = null,
        string title = "Deep clean, ground floor") =>
        JobDetails.Validate(title, null, ClientId, assignee, JobPriority.Normal, scheduledOn, dueOn, amount);

    private static Job NewJob(bool assigned = true, DateOnly? scheduledOn = null) =>
        Job.Create(Org, 42, Details(assigned ? WorkerId : null, scheduledOn), Manager, Names, Now);

    /// <summary>A job in the given status, reached through the real workflow, assigned and scheduled.</summary>
    private static Job JobIn(JobStatus status, bool assigned = true)
    {
        var job = NewJob(assigned, scheduledOn: Today.AddDays(1));
        switch (status)
        {
            case JobStatus.Scheduled:
                job.TransitionTo(JobStatus.Scheduled, Manager, null, Now);
                break;
            case JobStatus.InProgress:
                job.TransitionTo(JobStatus.InProgress, Manager, null, Now);
                break;
            case JobStatus.Completed:
                job.TransitionTo(JobStatus.InProgress, Manager, null, Now);
                job.TransitionTo(JobStatus.Completed, Manager, null, Now);
                break;
            case JobStatus.Cancelled:
                job.TransitionTo(JobStatus.Cancelled, Manager, "Client moved away", Now);
                break;
        }

        return job;
    }

    private static string? Outcome(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (DomainException error)
        {
            return error.Code;
        }
    }

    [Fact]
    public void Numbers_are_formatted_with_four_digits_and_grow_from_the_organization_counter()
    {
        var organization = Organization.Create("Acme", "acme", "UTC", ManagerId, Now);

        Assert.Equal([1, 2, 3], [organization.TakeJobNumber(), organization.TakeJobNumber(), organization.TakeJobNumber()]);
        Assert.Equal(4, organization.NextJobNumber);
        Assert.Equal(("JOB-0001", "JOB-0042", "JOB-12345"), (Job.FormatNumber(1), Job.FormatNumber(42), Job.FormatNumber(12345)));
    }

    public static TheoryData<JobStatus, JobStatus, bool> Cells()
    {
        var data = new TheoryData<JobStatus, JobStatus, bool>();
        foreach (var from in Enum.GetValues<JobStatus>())
        {
            foreach (var to in Enum.GetValues<JobStatus>())
            {
                data.Add(from, to, JobWorkflow.Find(from, to) is not null);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_of_the_table_is_either_allowed_or_refused_with_the_allowed_list(JobStatus from, JobStatus to, bool allowed)
    {
        var job = JobIn(from);

        if (allowed)
        {
            job.TransitionTo(to, Manager, "A reason", Now.AddHours(1));
            Assert.Equal(to, job.Status);
        }
        else
        {
            var error = Assert.Throws<InvalidJobTransitionException>(() => job.TransitionTo(to, Manager, "A reason", Now));
            Assert.Equal("invalid-transition", error.Code);
            Assert.Equal(DomainErrorKind.Conflict, error.Kind);
            Assert.Equal(JobWorkflow.From(from).Select(t => t.To), error.Allowed);
            Assert.Equal(from, job.Status);
        }
    }

    [Fact]
    public void The_table_is_exactly_the_spec()
    {
        var table = JobWorkflow.Transitions.Select(t => (t.From, t.To)).ToList();

        Assert.Equal(
            [
                (JobStatus.New, JobStatus.Scheduled),
                (JobStatus.New, JobStatus.InProgress),
                (JobStatus.Scheduled, JobStatus.InProgress),
                (JobStatus.InProgress, JobStatus.Completed),
                (JobStatus.New, JobStatus.Cancelled),
                (JobStatus.Scheduled, JobStatus.Cancelled),
                (JobStatus.InProgress, JobStatus.Cancelled),
                (JobStatus.Completed, JobStatus.InProgress),
            ],
            table);
        Assert.Equal(
            [JobStatus.New, JobStatus.Scheduled, JobStatus.InProgress],
            JobWorkflow.Transitions.Where(t => t.To == JobStatus.Cancelled).Select(t => t.From));
    }

    public static TheoryData<JobStatus, JobStatus, string, string?> Who() => new()
    {
        // from, to, actor (manager | assigned | other | viewer), refusal code (null = allowed)
        { JobStatus.New, JobStatus.Scheduled, "manager", null },
        { JobStatus.New, JobStatus.Scheduled, "assigned", "manager-only" },
        { JobStatus.New, JobStatus.InProgress, "manager", null },
        { JobStatus.New, JobStatus.InProgress, "assigned", null },
        { JobStatus.New, JobStatus.InProgress, "other", "job-not-yours" },
        { JobStatus.New, JobStatus.InProgress, "viewer", "job-not-yours" },
        { JobStatus.Scheduled, JobStatus.InProgress, "assigned", null },
        { JobStatus.Scheduled, JobStatus.InProgress, "other", "job-not-yours" },
        { JobStatus.InProgress, JobStatus.Completed, "assigned", null },
        { JobStatus.InProgress, JobStatus.Completed, "other", "job-not-yours" },
        { JobStatus.InProgress, JobStatus.Completed, "manager", null },
        { JobStatus.New, JobStatus.Cancelled, "manager", null },
        { JobStatus.New, JobStatus.Cancelled, "assigned", "manager-only" },
        { JobStatus.InProgress, JobStatus.Cancelled, "assigned", "manager-only" },
        { JobStatus.Completed, JobStatus.InProgress, "manager", null },
        { JobStatus.Completed, JobStatus.InProgress, "assigned", "manager-only" },
    };

    [Theory]
    [MemberData(nameof(Who))]
    public void Who_may_make_which_move(JobStatus from, JobStatus to, string who, string? refusal)
    {
        var job = JobIn(from);
        var actor = who switch
        {
            "manager" => Manager,
            "assigned" => Worker,
            "other" => OtherWorker,
            _ => Viewer,
        };

        Assert.Equal(refusal, Outcome(() => job.TransitionTo(to, actor, "A reason", Now)));
    }

    [Fact]
    public void Refusals_about_who_are_forbidden_and_the_allowed_list_only_names_what_the_actor_may_do()
    {
        var job = JobIn(JobStatus.New);

        var forbidden = Assert.Throws<DomainException>(() => job.TransitionTo(JobStatus.InProgress, OtherWorker, null, Now));
        var invalid = Assert.Throws<InvalidJobTransitionException>(() => job.TransitionTo(JobStatus.Completed, Worker, null, Now));

        Assert.Equal(DomainErrorKind.Forbidden, forbidden.Kind);
        Assert.Equal([JobStatus.InProgress], invalid.Allowed);
        Assert.Equal([JobStatus.Scheduled, JobStatus.InProgress, JobStatus.Cancelled], job.AllowedFor(Manager));
        Assert.Empty(job.AllowedFor(OtherWorker));
        Assert.Empty(job.AllowedFor(Viewer));
    }

    [Fact]
    public void Scheduling_needs_an_assignee_and_a_scheduled_date()
    {
        var neither = Job.Create(Org, 1, Details(assignee: null), Manager, Names, Now);
        var noDate = Job.Create(Org, 2, Details(WorkerId), Manager, Names, Now);
        var noAssignee = Job.Create(Org, 3, Details(null, Today), Manager, Names, Now);

        Assert.Equal(["assigneeUserId", "scheduledOn"], Fields(() => neither.TransitionTo(JobStatus.Scheduled, Manager, null, Now)));
        Assert.Equal(["scheduledOn"], Fields(() => noDate.TransitionTo(JobStatus.Scheduled, Manager, null, Now)));
        Assert.Equal(["assigneeUserId"], Fields(() => noAssignee.TransitionTo(JobStatus.Scheduled, Manager, null, Now)));
        Assert.Equal(["assigneeUserId"], Fields(() => noAssignee.TransitionTo(JobStatus.InProgress, Manager, null, Now)));
        Assert.Equal(JobStatus.New, noAssignee.Status);
    }

    [Fact]
    public void The_options_say_what_is_missing_before_a_move_can_be_made()
    {
        var job = Job.Create(Org, 1, Details(assignee: null), Manager, Names, Now);

        var options = job.OptionsFor(Manager);

        Assert.Equal(
            [
                (JobStatus.Scheduled, false, "Assign the job to someone first."),
                (JobStatus.InProgress, false, "Assign the job to someone first."),
                (JobStatus.Cancelled, true, null),
            ],
            options.Select(o => (o.To, o.NeedsReason, o.BlockedReason)));
    }

    [Theory]
    [InlineData(null, "reason")]
    [InlineData("", "reason")]
    [InlineData("   ", "reason")]
    public void Cancelling_and_reopening_need_a_reason(string? reason, string field)
    {
        var open = JobIn(JobStatus.Scheduled);
        var completed = JobIn(JobStatus.Completed);

        Assert.Equal([field], Fields(() => open.TransitionTo(JobStatus.Cancelled, Manager, reason, Now)));
        Assert.Equal([field], Fields(() => completed.TransitionTo(JobStatus.InProgress, Manager, reason, Now)));
    }

    [Fact]
    public void A_reason_is_one_to_five_hundred_characters_and_recorded_trimmed()
    {
        var job = JobIn(JobStatus.New);

        Assert.Equal(["reason"], Fields(() => job.TransitionTo(JobStatus.Cancelled, Manager, new string('x', 501), Now)));
        job.TransitionTo(JobStatus.Cancelled, Manager, "  " + new string('x', 500) + "  ", Now);

        var change = (JobEvent)job.DomainEvents[^1];
        Assert.Equal(new FieldChange("reason", null, new string('x', 500)), change.Changes[1]);
    }

    [Fact]
    public void Completing_stamps_the_time_and_reopening_clears_it_and_both_are_recorded()
    {
        var job = JobIn(JobStatus.InProgress);
        job.ClearDomainEvents();

        job.TransitionTo(JobStatus.Completed, Worker, null, Now.AddHours(2));
        var completedAt = job.CompletedAt;
        job.TransitionTo(JobStatus.InProgress, Manager, "Client found a mark", Now.AddHours(3));

        Assert.Equal(Now.AddHours(2), completedAt);
        Assert.Null(job.CompletedAt);
        var events = job.DomainEvents.Cast<JobEvent>().ToList();
        Assert.Equal(["job.status_changed", "job.status_changed"], events.Select(e => e.Action));
        Assert.Equal("Moved JOB-0042 from In progress to Completed", events[0].Summary);
        Assert.Equal("Reopened JOB-0042: Client found a mark", events[1].Summary);
        Assert.Equal(
            [new FieldChange("status", "Completed", "In progress"), new FieldChange("reason", null, "Client found a mark")],
            events[1].Changes);
    }

    [Fact]
    public void Cancelling_records_the_reason_in_the_summary()
    {
        var job = JobIn(JobStatus.Scheduled);

        job.TransitionTo(JobStatus.Cancelled, Manager, "Client moved away", Now);

        Assert.Equal("Cancelled JOB-0042: Client moved away", ((JobEvent)job.DomainEvents[^1]).Summary);
    }

    public static TheoryData<string, bool> Amounts() => new()
    {
        // amount, valid?
        { "0", true },
        { "1000000.00", true },
        { "1000000.01", false },
        { "-0.01", false },
        { "12.345", false },
        { "12.34", true },
    };

    [Theory]
    [MemberData(nameof(Amounts))]
    public void The_quoted_amount_is_0_to_a_million_with_two_decimals(string amount, bool valid)
    {
        var parsed = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var fields = Fields(() => Details(amount: parsed));

        Assert.Equal(valid ? [] : ["quotedAmount"], fields);
    }

    [Fact]
    public void The_due_date_may_equal_but_not_precede_the_scheduled_date()
    {
        Assert.Empty(Fields(() => Details(scheduledOn: Today, dueOn: Today)));
        Assert.Empty(Fields(() => Details(scheduledOn: Today, dueOn: Today.AddDays(1))));
        Assert.Empty(Fields(() => Details(scheduledOn: null, dueOn: Today.AddDays(-30))));
        Assert.Equal(["dueOn"], Fields(() => Details(scheduledOn: Today, dueOn: Today.AddDays(-1))));
    }

    [Fact]
    public void Every_invalid_field_is_reported_at_once()
    {
        var fields = Fields(() => JobDetails.Validate(" a ", new string('d', 5001), null, null, null, Today, Today.AddDays(-1), 2_000_000m));

        Assert.Equal(["title", "description", "clientId", "dueOn", "quotedAmount"], fields);
    }

    [Fact]
    public void Titles_are_trimmed_and_collapsed_and_bounded()
    {
        Assert.Equal("Deep clean now", Details(title: "  Deep   clean\tnow ").Title);
        Assert.Empty(Fields(() => Details(title: "ab")));
        Assert.Empty(Fields(() => Details(title: new string('t', 120))));
        Assert.Equal(["title"], Fields(() => Details(title: "a")));
        Assert.Equal(["title"], Fields(() => Details(title: new string('t', 121))));
    }

    [Fact]
    public void Creating_records_every_field_that_was_set_and_defaults_the_priority()
    {
        var job = Job.Create(
            Org,
            7,
            JobDetails.Validate("Window round", "All floors", ClientId, WorkerId, null, Today, Today.AddDays(2), 1250.5m),
            Manager,
            Names,
            Now);

        var created = (JobEvent)Assert.Single(job.DomainEvents);
        Assert.Equal(("job.created", "Created JOB-0007 “Window round”"), (created.Action, created.Summary));
        Assert.Equal(JobPriority.Normal, job.Priority);
        Assert.Equal(JobStatus.New, job.Status);
        Assert.Equal(
            [
                ("title", "Window round"),
                ("description", "All floors"),
                ("client", "Acme Offices"),
                ("assignee", "Maya Torres"),
                ("scheduledOn", "2026-10-22"),
                ("dueOn", "2026-10-24"),
                ("quotedAmount", "1250.50"),
            ],
            created.Changes.Select(c => (c.Field, c.To!)));
        Assert.All(created.Changes, c => Assert.Null(c.From));
    }

    [Fact]
    public void A_member_without_manage_may_create_unassigned_or_for_themselves_but_not_assign_or_schedule()
    {
        Assert.Null(Job.Create(Org, 1, Details(assignee: null), Worker, Names, Now).AssigneeUserId);
        Assert.Equal(WorkerId, Job.Create(Org, 2, Details(WorkerId), Worker, Names, Now).AssigneeUserId);
        Assert.Equal("manager-only", Outcome(() => Job.Create(Org, 3, Details(OtherWorkerId), Worker, Names, Now)));
        Assert.Equal("manager-only", Outcome(() => Job.Create(Org, 4, Details(WorkerId, scheduledOn: Today), Worker, Names, Now)));
    }

    [Fact]
    public void Updating_records_changed_fields_with_names_and_does_nothing_when_nothing_changed()
    {
        var job = JobIn(JobStatus.New);
        job.ClearDomainEvents();

        var unchanged = job.Update(job.Details, Manager, Names, Now);
        var changed = job.Update(
            job.Details with { Title = "Deep clean, first floor", AssigneeUserId = OtherWorkerId, ClientId = OtherClientId, QuotedAmount = 99m },
            Manager,
            Names,
            Now.AddHours(1));

        Assert.False(unchanged);
        Assert.True(changed);
        var updated = (JobEvent)Assert.Single(job.DomainEvents);
        Assert.Equal("Updated JOB-0042: title, client, assignee, quoted amount", updated.Summary);
        Assert.Equal(
            [
                new FieldChange("title", "Deep clean, ground floor", "Deep clean, first floor"),
                new FieldChange("client", "Acme Offices", "Birch Dental"),
                new FieldChange("assignee", "Maya Torres", "Victor Lindqvist"),
                new FieldChange("quotedAmount", null, "99.00"),
            ],
            updated.Changes);
        Assert.Equal(Now.AddHours(1), job.UpdatedAt);
    }

    [Fact]
    public void An_assigned_member_edits_their_job_but_cannot_reassign_or_reschedule_it()
    {
        var job = JobIn(JobStatus.Scheduled);

        Assert.True(job.Update(job.Details with { Priority = JobPriority.High }, Worker, Names, Now));
        Assert.Equal("manager-only", Outcome(() => job.Update(job.Details with { AssigneeUserId = OtherWorkerId }, Worker, Names, Now)));
        Assert.Equal("manager-only", Outcome(() => job.Update(job.Details with { AssigneeUserId = null }, Worker, Names, Now)));
        Assert.Equal("manager-only", Outcome(() => job.Update(job.Details with { ScheduledOn = Today.AddDays(5) }, Worker, Names, Now)));
        Assert.Equal("job-not-yours", Outcome(() => job.Update(job.Details with { Priority = JobPriority.Low }, OtherWorker, Names, Now)));
        Assert.Equal("job-not-yours", Outcome(() => job.Update(job.Details with { Priority = JobPriority.Low }, Viewer, Names, Now)));
    }

    public static TheoryData<string, string?, bool> Overdue() => new()
    {
        // due date, status, overdue? (today is 2026-10-22 in the organization's zone)
        { "2026-10-21", "New", true },
        { "2026-10-22", "New", false },
        { "2026-10-23", "New", false },
        { "2026-10-21", "InProgress", true },
        { "2026-10-21", "Completed", false },
        { "2026-10-21", "Cancelled", false },
        { "none", "New", false },
    };

    [Theory]
    [MemberData(nameof(Overdue))]
    public void Overdue_means_due_before_today_and_not_closed(string due, string? status, bool overdue)
    {
        DateOnly? dueOn = due == "none" ? null : DateOnly.Parse(due, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(overdue, Job.IsOverdue(dueOn, Enum.Parse<JobStatus>(status!), Today));
    }

    [Theory]
    [InlineData("Pacific/Auckland", "2026-10-22T10:59:00Z", "2026-10-22")]
    [InlineData("Pacific/Auckland", "2026-10-22T11:01:00Z", "2026-10-23")]
    [InlineData("America/Los_Angeles", "2026-10-22T06:59:00Z", "2026-10-21")]
    [InlineData("America/Los_Angeles", "2026-10-22T07:01:00Z", "2026-10-22")]
    [InlineData("UTC", "2026-10-22T23:59:00Z", "2026-10-22")]
    [InlineData("Europe/Amsterdam", "2026-10-22T22:30:00Z", "2026-10-23")]
    public void Today_is_judged_in_the_organizations_time_zone_around_midnight(string zone, string instant, string expected)
    {
        var now = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Organization.TodayIn(zone, now));
    }

    [Fact]
    public void A_job_due_yesterday_in_auckland_is_overdue_while_it_is_still_today_in_los_angeles()
    {
        var job = Job.Create(Org, 1, Details(WorkerId, dueOn: new DateOnly(2026, 10, 22)), Manager, Names, Now);
        var instant = DateTimeOffset.Parse("2026-10-22T11:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(job.IsOverdue(Organization.TodayIn("Pacific/Auckland", instant)));
        Assert.False(job.IsOverdue(Organization.TodayIn("America/Los_Angeles", instant)));
    }

    [Fact]
    public void Unassigning_a_departed_member_touches_only_open_assigned_jobs_and_is_recorded()
    {
        var open = JobIn(JobStatus.InProgress);
        var closed = JobIn(JobStatus.Completed);
        var unassigned = JobIn(JobStatus.New, assigned: false);
        open.ClearDomainEvents();

        Assert.True(open.Unassign("Maya Torres", "was removed", Now));
        Assert.False(closed.Unassign("Maya Torres", "was removed", Now));
        Assert.False(unassigned.Unassign("Maya Torres", "was removed", Now));

        Assert.Null(open.AssigneeUserId);
        Assert.Equal(JobStatus.InProgress, open.Status);
        Assert.Equal(WorkerId, closed.AssigneeUserId);
        var recorded = (JobEvent)Assert.Single(open.DomainEvents);
        Assert.Equal(("job.unassigned", "Unassigned JOB-0042 because Maya Torres was removed"), (recorded.Action, recorded.Summary));
        Assert.Equal(new FieldChange("assignee", "Maya Torres", null), recorded.Changes.Single());
    }

    [Fact]
    public void A_job_unassigned_after_removal_can_still_be_cancelled_and_completed_by_a_manager()
    {
        var job = JobIn(JobStatus.InProgress);
        job.Unassign("Maya Torres", "left the organization", Now);

        job.TransitionTo(JobStatus.Completed, Manager, null, Now);

        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Theory]
    [InlineData(JobStatus.New, true)]
    [InlineData(JobStatus.Cancelled, true)]
    [InlineData(JobStatus.Scheduled, false)]
    [InlineData(JobStatus.InProgress, false)]
    [InlineData(JobStatus.Completed, false)]
    public void Only_new_or_cancelled_jobs_can_be_deleted_and_only_by_a_manager(JobStatus status, bool deletable)
    {
        var job = JobIn(status);

        Assert.Equal(deletable ? null : "job-not-deletable", Outcome(() => job.MarkDeleted(Manager)));
        Assert.Equal("manager-only", Outcome(() => job.MarkDeleted(Worker)));
    }

    [Fact]
    public void Deleting_is_recorded()
    {
        var job = JobIn(JobStatus.New);
        job.ClearDomainEvents();

        job.MarkDeleted(Manager);

        Assert.Equal("Deleted JOB-0042 “Deep clean, ground floor”", ((JobEvent)Assert.Single(job.DomainEvents)).Summary);
    }

    [Fact]
    public void Audit_events_are_for_the_job_in_its_organization()
    {
        var job = NewJob();

        var created = (IAuditableEvent)Assert.Single(job.DomainEvents);

        Assert.Equal((Org, "job", job.Id), (created.OrganizationId, created.EntityType, created.EntityId));
    }

    private static List<string> Fields(Action action)
    {
        try
        {
            action();
            return [];
        }
        catch (DomainValidationException error)
        {
            return [.. error.Errors.Keys];
        }
    }
}
