using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Tests.Jobs;

/// <summary>
/// SPEC-V2 5.6: the checklist's rules, the Completed precondition alongside every v1 transition, the photo count, and the
/// client's sign-off on a completed visit.
/// </summary>
public sealed class JobChecklistTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid ClientId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000c1");
    private static readonly Guid ManagerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    private static readonly Guid WorkerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000002");
    private static readonly Guid ContactId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly JobActor Manager = JobActor.From(ManagerId, Role.Admin);
    private static readonly JobActor Worker = JobActor.From(WorkerId, Role.Member);
    private static readonly JobActor OtherWorker = JobActor.From(Guid.NewGuid(), Role.Member);
    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [ClientId] = "Acme", [WorkerId] = "Maya", [ManagerId] = "Adam" };

    private static Job NewJob() => Job.Create(
        Org,
        42,
        JobDetails.Validate("Deep clean", null, ClientId, WorkerId, JobPriority.Normal, new DateOnly(2026, 11, 20), null, null),
        Manager,
        Names,
        Now);

    private static Job InProgress()
    {
        var job = NewJob();
        job.TransitionTo(JobStatus.InProgress, Manager, null, Now);
        return job;
    }

    private static ChecklistEntry Entry(string text, bool required = false) => ChecklistEntry.Validate(text, required);

    /// <summary>A job reaching <paramref name="from"/> through the workflow, with one required item done on the way.</summary>
    private static (Job Job, ChecklistItem Item) JobIn(JobStatus from)
    {
        var job = NewJob();
        var item = job.AddChecklistItem(Entry("Mop the floors", required: true), Manager, Now);
        job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, true, null), Manager, Now);
        switch (from)
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

        return (job, item);
    }

    public static TheoryData<JobStatus, JobStatus> Transitions() =>
        new(JobWorkflow.Transitions.Select(t => (t.From, t.To)));

    [Theory]
    [MemberData(nameof(Transitions))]
    public void An_open_required_item_holds_back_only_completing(JobStatus from, JobStatus to)
    {
        var (job, item) = JobIn(from);
        if (from.IsOpen())
        {
            job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, false, null), Manager, Now);
        }

        var outcome = Record.Exception(() => job.TransitionTo(to, Manager, "A reason", Now.AddHours(1)));

        if (to == JobStatus.Completed)
        {
            var refused = Assert.IsType<ChecklistIncompleteException>(outcome);
            Assert.Equal(("checklist-incomplete", DomainErrorKind.Conflict), (refused.Code, refused.Kind));
            Assert.Equal("Tick the required checklist items first: Mop the floors.", refused.Message);
            Assert.Equal([item.Id], refused.Open.Select(i => i.Id));
            Assert.Equal(from, job.Status);
            Assert.Equal(refused.Message, job.OptionsFor(Manager).Single(o => o.To == JobStatus.Completed).BlockedReason);
        }
        else
        {
            Assert.Null(outcome);
            Assert.Equal(to, job.Status);
        }
    }

    [Fact]
    public void Optional_items_and_done_items_do_not_hold_anything_back()
    {
        var job = InProgress();
        job.AddChecklistItem(Entry("Water the plants"), Worker, Now);
        var done = job.AddChecklistItem(Entry("Empty the bins", required: true), Worker, Now);
        job.ChangeChecklistItem(done.Id, new ChecklistChange(null, null, true, null), Worker, Now);

        Assert.Null(job.OptionsFor(Worker).Single(o => o.To == JobStatus.Completed).BlockedReason);
        job.TransitionTo(JobStatus.Completed, Worker, null, Now);
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public void Items_have_a_text_of_1_to_200_characters_and_a_job_has_at_most_30()
    {
        var job = InProgress();

        Assert.Equal(["text"], Assert.Throws<DomainValidationException>(() => ChecklistEntry.Validate("   ", true)).Errors.Keys);
        Assert.Equal(["text"], Assert.Throws<DomainValidationException>(() => ChecklistEntry.Validate(new string('x', 201), true)).Errors.Keys);
        Assert.Equal("Wipe the counters", ChecklistEntry.Validate("  Wipe   the counters ", null).Text);
        for (var i = 0; i < Checklists.MaxItems; i++)
        {
            job.AddChecklistItem(Entry($"Item {i + 1}", required: i % 2 == 0), Worker, Now);
        }

        Assert.Equal("checklist-full", Assert.Throws<DomainException>(() => job.AddChecklistItem(Entry("One more"), Worker, Now)).Code);
        Assert.Equal(Enumerable.Range(0, 30), job.OrderedChecklist().Select(i => i.Position));
    }

    [Fact]
    public void Ticking_records_who_and_when_and_unticking_clears_both()
    {
        var job = InProgress();
        var item = job.AddChecklistItem(Entry("Mop the floors", required: true), Manager, Now);
        job.ClearDomainEvents();

        job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, true, null), Worker, Now.AddMinutes(5));
        var ticked = (item.DoneAt, item.DoneByUserId);
        job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, false, null), Worker, Now.AddMinutes(9));

        Assert.Equal((Now.AddMinutes(5), WorkerId), (ticked.DoneAt!.Value, ticked.DoneByUserId!.Value));
        Assert.Equal(((DateTimeOffset?)null, (Guid?)null), (item.DoneAt, item.DoneByUserId));
        Assert.Equal(
            ["Ticked “Mop the floors” on the checklist of JOB-0042", "Unticked “Mop the floors” on the checklist of JOB-0042"],
            job.DomainEvents.Cast<JobEvent>().Select(e => e.Summary));
        Assert.Equal(["job.checklist_item_ticked", "job.checklist_item_unticked"], job.DomainEvents.Cast<JobEvent>().Select(e => e.Action));
    }

    [Fact]
    public void Items_move_to_a_place_and_the_others_close_up_when_one_goes()
    {
        var job = InProgress();
        var first = job.AddChecklistItem(Entry("First"), Manager, Now);
        var second = job.AddChecklistItem(Entry("Second"), Manager, Now);
        var third = job.AddChecklistItem(Entry("Third"), Manager, Now);

        Assert.True(job.ChangeChecklistItem(third.Id, new ChecklistChange(null, null, null, 0), Manager, Now));
        var moved = job.OrderedChecklist().Select(i => i.Text).ToList();
        Assert.False(job.ChangeChecklistItem(first.Id, new ChecklistChange("First", false, false, 1), Manager, Now));
        job.ChangeChecklistItem(second.Id, new ChecklistChange(null, null, null, 99), Manager, Now); // beyond the end: the last place
        job.RemoveChecklistItem(third.Id, Manager, Now);

        Assert.Equal(["Third", "First", "Second"], moved);
        Assert.Equal([("First", 0), ("Second", 1)], job.OrderedChecklist().Select(i => (i.Text, i.Position)));
    }

    [Fact]
    public void Editing_records_the_text_and_required_changes()
    {
        var job = InProgress();
        var item = job.AddChecklistItem(Entry("Mop"), Manager, Now);
        job.ClearDomainEvents();

        job.ChangeChecklistItem(item.Id, new ChecklistChange("Mop the kitchen floor", true, null, null), Worker, Now);

        var changed = (JobEvent)Assert.Single(job.DomainEvents);
        Assert.Equal(("job.checklist_item_changed", "Changed “Mop” on the checklist of JOB-0042"), (changed.Action, changed.Summary));
        Assert.Equal([new FieldChange("item", "Mop", "Mop the kitchen floor"), new FieldChange("required", "no", "yes")], changed.Changes);
    }

    [Fact]
    public void Only_whoever_may_edit_the_job_changes_its_proof_and_only_while_it_is_open()
    {
        var job = InProgress();
        var item = job.AddChecklistItem(Entry("Mop", required: true), Worker, Now);
        var forbidden = Assert.Throws<DomainException>(() => job.AddChecklistItem(Entry("Not mine"), OtherWorker, Now));
        job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, true, null), Worker, Now);
        job.TransitionTo(JobStatus.Completed, Worker, null, Now);

        var locked = new[]
        {
            Record.Exception(() => job.AddChecklistItem(Entry("Late"), Manager, Now)),
            Record.Exception(() => job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, false, null), Manager, Now)),
            Record.Exception(() => job.RemoveChecklistItem(item.Id, Manager, Now)),
            Record.Exception(() => job.EnsureRoomForPhoto(0, Manager)),
        };

        Assert.Equal(("job-not-yours", DomainErrorKind.Forbidden), (forbidden.Code, forbidden.Kind));
        Assert.All(locked, error => Assert.Equal("job-closed", Assert.IsType<DomainException>(error).Code));
        Assert.Equal(
            "checklist-item-not-found",
            Assert.Throws<DomainException>(() => InProgress().ChangeChecklistItem(Guid.NewGuid(), new ChecklistChange(null, null, true, null), Manager, Now)).Code);

        // Reopened, the proof can change again.
        job.TransitionTo(JobStatus.InProgress, Manager, "Missed a room", Now);
        job.ChangeChecklistItem(item.Id, new ChecklistChange(null, null, false, null), Manager, Now);
    }

    [Fact]
    public void A_job_has_at_most_ten_photos_and_each_is_recorded()
    {
        var job = InProgress();
        job.ClearDomainEvents();
        var photo = new StoredPhoto("a/b.jpg", "a/c.jpg", "image/jpeg", 2048, 800, 600);

        var added = job.AddPhoto(photo, photosOnJob: 9, Worker, Now);
        var full = Assert.Throws<DomainException>(() => job.AddPhoto(photo, photosOnJob: 10, Worker, Now));
        job.RemovePhoto(added, photosOnJob: 10, Worker);

        Assert.Equal((Org, job.Id, 2048L, WorkerId), (added.OrganizationId, added.JobId, added.Bytes, added.CreatedByUserId));
        Assert.Equal("photo-limit-reached", full.Code);
        Assert.Equal(
            [("job.photo_added", "Added a photo to JOB-0042"), ("job.photo_removed", "Removed a photo from JOB-0042")],
            job.DomainEvents.Cast<JobEvent>().Select(e => (e.Action, e.Summary)));
    }

    [Fact]
    public void The_client_confirms_a_completed_visit_once()
    {
        var job = InProgress();
        var early = Assert.Throws<DomainException>(() => job.RecordFeedback(true, null, ContactId, Now));
        job.TransitionTo(JobStatus.Completed, Manager, null, Now);
        job.ClearDomainEvents();

        job.RecordFeedback(confirmed: true, "ignored for a confirmation", ContactId, Now.AddHours(1));
        var again = Assert.Throws<DomainException>(() => job.RecordFeedback(false, "Also a problem", ContactId, Now));

        Assert.Equal("visit-not-completed", early.Code);
        Assert.Equal("feedback-already-given", again.Code);
        Assert.Equal(
            ((ClientFeedback?)ClientFeedback.Confirmed, (string?)null, (DateTimeOffset?)Now.AddHours(1), (Guid?)ContactId),
            (job.ClientFeedback, job.ClientFeedbackText, job.ClientFeedbackAt, job.ClientFeedbackByContactId));
        var recorded = (JobEvent)Assert.Single(job.DomainEvents);
        Assert.Equal(("job.client_confirmed", "Confirmed JOB-0042: looks good"), (recorded.Action, recorded.Summary));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("x", true)]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public void A_problem_is_described_in_1_to_2000_characters(object text, bool valid)
    {
        var job = InProgress();
        job.TransitionTo(JobStatus.Completed, Manager, null, Now);
        var problem = text is int length ? new string('p', length) : (string)text;

        var error = Record.Exception(() => job.RecordFeedback(confirmed: false, problem, ContactId, Now));

        Assert.Equal(valid, error is null);
        Assert.Equal(valid ? ClientFeedback.ProblemReported : null, job.ClientFeedback);
        if (!valid)
        {
            Assert.Equal(["text"], Assert.IsType<DomainValidationException>(error).Errors.Keys);
        }
    }

    [Fact]
    public void Reopening_clears_the_feedback_and_says_so()
    {
        var job = InProgress();
        job.TransitionTo(JobStatus.Completed, Manager, null, Now);
        job.RecordFeedback(confirmed: false, "The hallway was missed", ContactId, Now);
        job.ClearDomainEvents();

        job.TransitionTo(JobStatus.InProgress, Manager, "Client reported a problem", Now);

        Assert.Equal(((ClientFeedback?)null, (string?)null), (job.ClientFeedback, job.ClientFeedbackText));
        Assert.Contains(new FieldChange("clientFeedback", "Problem reported", null), ((JobEvent)Assert.Single(job.DomainEvents)).Changes);
    }
}
