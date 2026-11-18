using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Tests.Jobs;

/// <summary>
/// SPEC-V2 5.4: sending a quote, the client's one decision, the new In progress precondition, and the approval reset.
/// Jobs never sent are covered by <see cref="JobTests"/>'s transition table, which runs unchanged.
/// </summary>
public sealed class JobQuoteTests
{
    private static readonly Guid Org = Guid.Parse("0199f0a0-0000-7000-8000-0000000000a1");
    private static readonly Guid ClientId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000c1");
    private static readonly Guid OtherClientId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000c2");
    private static readonly Guid ManagerId = Guid.Parse("0199f0a0-0000-7000-8000-000000000001");
    private static readonly Guid ContactId = Guid.Parse("0199f0a0-0000-7000-8000-0000000000d1");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly JobActor Manager = JobActor.From(ManagerId, Role.Admin);
    private static readonly IReadOnlyDictionary<Guid, string> Names = new Dictionary<Guid, string> { [ClientId] = "Acme", [OtherClientId] = "Birch", [ManagerId] = "Adam" };

    private static JobDetails Details(decimal? amount = 180m, Guid? client = null, string title = "Deep clean") =>
        JobDetails.Validate(title, null, client ?? ClientId, ManagerId, JobPriority.Normal, new DateOnly(2026, 11, 20), null, amount);

    private static Job Scheduled(decimal? amount = 180m)
    {
        var job = Job.Create(Org, 42, Details(amount), Manager, Names, Now);
        job.TransitionTo(JobStatus.Scheduled, Manager, null, Now);
        job.ClearDomainEvents();
        return job;
    }

    private static Job Pending()
    {
        var job = Scheduled();
        job.SendForApproval(ManagerId, activeContacts: 1, Now);
        job.ClearDomainEvents();
        return job;
    }

    [Fact]
    public void Sending_needs_an_open_job_an_amount_and_a_contact()
    {
        var done = Scheduled();
        done.TransitionTo(JobStatus.InProgress, Manager, null, Now);

        Assert.Equal("quote-not-sendable", Assert.Throws<DomainException>(() => done.SendForApproval(ManagerId, 1, Now)).Code);
        Assert.Equal("quote-not-sendable", Assert.Throws<DomainException>(() => Scheduled(amount: null).SendForApproval(ManagerId, 1, Now)).Code);
        Assert.Equal("client-has-no-contacts", Assert.Throws<DomainException>(() => Scheduled().SendForApproval(ManagerId, 0, Now)).Code);
        Assert.Equal("quote-not-sendable", Assert.Throws<DomainException>(() => Pending().SendForApproval(ManagerId, 1, Now)).Code);
    }

    [Fact]
    public void Sending_makes_the_quote_pending_and_records_it()
    {
        var job = Scheduled();

        job.SendForApproval(ManagerId, activeContacts: 2, Now);

        Assert.Equal((JobApprovalStatus.Pending, Now, ManagerId), (job.ApprovalStatus, job.ApprovalSentAt!.Value, job.ApprovalSentByUserId!.Value));
        var sent = (JobEvent)Assert.Single(job.DomainEvents);
        Assert.Equal(("job.quote_sent", "Sent the quote for JOB-0042 (180.00) for approval"), (sent.Action, sent.Summary));
    }

    [Theory]
    [InlineData(JobApprovalStatus.Pending, "The client has not approved the quote yet.")]
    [InlineData(JobApprovalStatus.Declined, "The client declined the quote. Send a new one first.")]
    public void Work_does_not_start_on_a_quote_that_is_not_approved(JobApprovalStatus approval, string why)
    {
        var job = Pending();
        if (approval == JobApprovalStatus.Declined)
        {
            job.Decide(approve: false, ContactId, null, Now);
        }

        var refused = Assert.Throws<DomainException>(() => job.TransitionTo(JobStatus.InProgress, Manager, null, Now));

        Assert.Equal(("quote-not-approved", why), (refused.Code, refused.Message));
        Assert.Equal(why, job.OptionsFor(Manager).Single(o => o.To == JobStatus.InProgress).BlockedReason);
        Assert.Equal(JobStatus.Scheduled, job.Status);
        // Only starting is held back: the job can still be cancelled.
        job.TransitionTo(JobStatus.Cancelled, Manager, "Client changed their mind", Now);
    }

    [Fact]
    public void An_approved_quote_lets_work_start_and_records_the_decision()
    {
        var job = Pending();

        job.Decide(approve: true, ContactId, "  Fine, go ahead  ", Now.AddHours(1));
        job.TransitionTo(JobStatus.InProgress, Manager, null, Now.AddHours(2));

        Assert.Equal(
            (JobApprovalStatus.Approved, (decimal?)180m, ContactId, "Fine, go ahead", JobStatus.InProgress),
            (job.ApprovalStatus, job.ApprovedAmount, job.ApprovalDecidedByContactId!.Value, job.ApprovalComment, job.Status));
        Assert.Equal("Approved the quote for JOB-0042 (180.00)", ((JobEvent)job.DomainEvents[0]).Summary);
    }

    [Fact]
    public void The_client_decides_once_and_a_comment_has_a_limit()
    {
        var job = Pending();

        var tooLong = Assert.Throws<DomainValidationException>(() => job.Decide(true, ContactId, new string('c', 1001), Now));
        job.Decide(approve: false, ContactId, null, Now);
        var again = Assert.Throws<DomainException>(() => job.Decide(true, ContactId, null, Now));

        Assert.Equal(["comment"], tooLong.Errors.Keys);
        Assert.Equal("quote-already-decided", again.Code);
        Assert.Equal(JobApprovalStatus.Declined, job.ApprovalStatus);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("client")]
    public void Changing_the_amount_or_client_of_an_approved_quote_asks_again(string what)
    {
        var job = Pending();
        job.Decide(approve: true, ContactId, null, Now);
        job.ClearDomainEvents();

        job.Update(what == "amount" ? Details(amount: 200m) : Details(client: OtherClientId), Manager, Names, Now);

        Assert.Equal((JobApprovalStatus.NotRequested, (decimal?)null), (job.ApprovalStatus, job.ApprovedAmount));
        Assert.Equal(["job.quote_reset", "job.updated"], job.DomainEvents.Cast<JobEvent>().Select(e => e.Action));
        Assert.Equal($"The quote for JOB-0042 needs a new decision: its {what} changed", ((JobEvent)job.DomainEvents[0]).Summary);
    }

    [Fact]
    public void Other_edits_keep_the_approval()
    {
        var job = Pending();
        job.Decide(approve: true, ContactId, null, Now);

        job.Update(Details(title: "Deep clean, both floors"), Manager, Names, Now);

        Assert.Equal(JobApprovalStatus.Approved, job.ApprovalStatus);
    }
}
