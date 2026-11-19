using TenantHub.Domain.Agreements;
using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Jobs;

/// <summary>
/// A unit of work for a client (SPEC 5.4). The status only moves along <see cref="JobWorkflow"/>; who may do what is
/// decided here from the <see cref="JobActor"/> and the assignee, so every entry point (API, seed, tests) obeys the
/// same rules. Requirements such as "assignee" are checked at the transition, not as standing invariants: removing a
/// member unassigns their open jobs without moving them.
/// </summary>
public sealed class Job : AggregateRoot, IClientOwned
{
    private static readonly IReadOnlyDictionary<string, string> FieldLabels = new Dictionary<string, string>
    {
        ["title"] = "title",
        ["description"] = "description",
        ["client"] = "client",
        ["assignee"] = "assignee",
        ["priority"] = "priority",
        ["scheduledOn"] = "scheduled date",
        ["dueOn"] = "due date",
        ["quotedAmount"] = "quoted amount",
        ["estimatedMinutes"] = "estimated time",
    };

    private Job()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>Per organization, sequential, never reused (SPEC 5.4).</summary>
    public int Number { get; private set; }

    public string Title { get; private set; } = "";

    public string? Description { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid? AssigneeUserId { get; private set; }

    public JobStatus Status { get; private set; }

    public JobPriority Priority { get; private set; }

    public DateOnly? ScheduledOn { get; private set; }

    public DateOnly? DueOn { get; private set; }

    public decimal? QuotedAmount { get; private set; }

    public int? EstimatedMinutes { get; private set; }

    /// <summary>The agreement this visit was generated from (SPEC-V2 4.2); null for jobs made by hand.</summary>
    public Guid? AgreementId { get; private set; }

    /// <summary>The agreement's occurrence this visit is; with the agreement, unique, so it is never generated twice.</summary>
    public DateOnly? OccurrenceOn { get; private set; }

    /// <summary>A person edited this visit, so changes to its agreement leave it alone (SPEC-V2 5.1).</summary>
    public bool ChangedByHand { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The quote's approval by the client (SPEC-V2 5.4); jobs never sent stay NotRequested and work as in v1.</summary>
    public JobApprovalStatus ApprovalStatus { get; private set; }

    public DateTimeOffset? ApprovalSentAt { get; private set; }

    public Guid? ApprovalSentByUserId { get; private set; }

    /// <summary>The amount the client approved: the quoted amount at that moment.</summary>
    public decimal? ApprovedAmount { get; private set; }

    public DateTimeOffset? ApprovalDecidedAt { get; private set; }

    public Guid? ApprovalDecidedByContactId { get; private set; }

    public string? ApprovalComment { get; private set; }

    /// <summary>Optimistic-concurrency token; the database bumps it on every update.</summary>
    public uint Version { get; private set; }

    public string Label => FormatNumber(Number);

    public JobDetails Details => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount, EstimatedMinutes);

    public static string FormatNumber(int number) => $"JOB-{number:D4}";

    /// <summary>
    /// Creating needs <c>jobs.create</c> (checked by the caller). Without <c>jobs.manage</c> the job may stay
    /// unassigned or be taken by its creator, and cannot be scheduled (SPEC 2, US-30).
    /// </summary>
    public static Job Create(
        Guid organizationId,
        int number,
        JobDetails details,
        JobActor actor,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now)
    {
        if (!actor.CanManage)
        {
            EnsureAssigneeAllowed(details.AssigneeUserId, actor, current: null);
            if (details.ScheduledOn is not null)
            {
                throw ManagersOnly("scheduling a job");
            }
        }

        var job = new Job
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Number = number,
            Status = JobStatus.New,
            CreatedAt = now,
            CreatedByUserId = actor.UserId,
            UpdatedAt = now,
        };
        job.Apply(details);
        var empty = new JobDetails("", null, Guid.Empty, null, details.Priority, null, null, null);
        var changes = empty.ChangesTo(details, names).Select(c => c with { From = null }).ToList();
        job.Raise(new JobEvent(job, "job.created", $"Created {job.Label} “{job.Title}”", changes));
        return job;
    }

    /// <summary>
    /// Applies new details; returns false (and records nothing) when nothing changed. <c>jobs.manage</c> edits any
    /// job; <c>jobs.work</c> edits jobs assigned to the actor, but cannot reassign or reschedule them.
    /// </summary>
    public bool Update(JobDetails details, JobActor actor, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
    {
        if (!CanEdit(actor))
        {
            throw new DomainException("job-not-yours", "You can only change jobs assigned to you.", DomainErrorKind.Forbidden);
        }

        if (!actor.CanManage)
        {
            EnsureAssigneeAllowed(details.AssigneeUserId, actor, current: AssigneeUserId);
            if (details.ScheduledOn != ScheduledOn)
            {
                throw ManagersOnly("scheduling a job");
            }
        }

        var changes = Details.ChangesTo(details, names);
        if (changes.Count == 0)
        {
            return false;
        }

        Apply(details);
        UpdatedAt = now;
        ChangedByHand |= AgreementId is not null;
        var fields = string.Join(", ", changes.Select(c => FieldLabels.GetValueOrDefault(c.Field, c.Field)));
        Raise(new JobEvent(this, "job.updated", $"Updated {Label}: {fields}", changes));
        return true;
    }

    /// <summary>
    /// A move on the board (SPEC-V2 5.2): to a person (or nobody) and a day (or back to "not scheduled"), in one step. Managers only, as for
    /// assigning and scheduling anywhere else; closed jobs stay where they are. A New job that ends up with an
    /// assignee and a day becomes Scheduled through the ordinary workflow move; without a day it stays New, and a job
    /// taken off the plan keeps its status, as the job form allows. A day after the due date is refused
    /// unless <paramref name="moveDueDate"/> moves the due date along. Returns false when nothing changed.
    /// </summary>
    public bool Schedule(
        Guid? assigneeUserId,
        DateOnly? scheduledOn,
        bool moveDueDate,
        JobActor actor,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now)
    {
        if (!actor.CanManage)
        {
            throw ManagersOnly("planning jobs on the board");
        }

        if (!Status.IsOpen())
        {
            throw new DomainException("job-closed", $"{Label} is {JobLabels.Of(Status)}, so it stays where it is.");
        }

        var dueOn = DueOn;
        if (dueOn is { } due && scheduledOn is { } day && day > due)
        {
            if (!moveDueDate)
            {
                throw new DomainException(
                    "after-due-date",
                    $"{Label} is due on {JobDetails.Date(due)}. Move the due date too, or choose an earlier day.");
            }

            dueOn = day;
        }

        var details = Details with { AssigneeUserId = assigneeUserId, ScheduledOn = scheduledOn, DueOn = dueOn };
        var changes = Details.ChangesTo(details, names);
        var becomesScheduled = Status == JobStatus.New && assigneeUserId is not null && scheduledOn is not null;
        if (changes.Count == 0 && !becomesScheduled)
        {
            return false;
        }

        if (changes.Count > 0)
        {
            Apply(details);
            UpdatedAt = now;
            ChangedByHand |= AgreementId is not null;
            var fields = string.Join(", ", changes.Select(c => FieldLabels.GetValueOrDefault(c.Field, c.Field)));
            Raise(new JobEvent(this, "job.updated", $"Moved {Label} on the board: {fields}", changes));
        }

        if (becomesScheduled)
        {
            TransitionTo(JobStatus.Scheduled, actor, null, now);
        }

        return true;
    }

    /// <summary>
    /// A visit of <paramref name="agreement"/> on <paramref name="occurrence"/> (SPEC-V2 5.1): the template's fields,
    /// scheduled and due that day, Scheduled when the template has an assignee and otherwise New. The agreement's
    /// creator is its creator; the audit names the system.
    /// </summary>
    public static Job Generate(Agreement agreement, int number, DateOnly occurrence, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
    {
        var details = agreement.Template.ForVisit(agreement.ClientId, occurrence);
        var job = new Job
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = agreement.OrganizationId,
            Number = number,
            Status = details.AssigneeUserId is null ? JobStatus.New : JobStatus.Scheduled,
            AgreementId = agreement.Id,
            OccurrenceOn = occurrence,
            CreatedAt = now,
            CreatedByUserId = agreement.CreatedByUserId,
            UpdatedAt = now,
        };
        job.Apply(details);
        var empty = new JobDetails("", null, Guid.Empty, null, details.Priority, null, null, null);
        var changes = empty.ChangesTo(details, names).Select(c => c with { From = null }).ToList();
        changes.Add(new FieldChange("status", null, JobLabels.Of(job.Status)));
        job.Raise(new JobEvent(job, "job.created", $"Created {job.Label} “{job.Title}” for {JobDetails.Date(occurrence)}", changes, agreement.SystemActor));
        return job;
    }

    /// <summary>
    /// Sends the quote to the client for approval (SPEC-V2 5.4): a New or Scheduled job with a quoted amount, for a client
    /// with at least one active contact. A declined quote can be sent again (after changing it, typically).
    /// </summary>
    public void SendForApproval(Guid userId, int activeContacts, DateTimeOffset now)
    {
        if (JobQuotes.WhyNotSend(Status, QuotedAmount, ApprovalStatus) is { } why)
        {
            throw new DomainException("quote-not-sendable", why);
        }

        if (activeContacts == 0)
        {
            throw new DomainException("client-has-no-contacts", "Add a portal contact to the client first: they are the ones who approve.");
        }

        var from = ApprovalStatus;
        ApprovalStatus = JobApprovalStatus.Pending;
        ApprovalSentAt = now;
        ApprovalSentByUserId = userId;
        ClearDecision();
        UpdatedAt = now;
        Raise(new JobEvent(
            this,
            "job.quote_sent",
            $"Sent the quote for {Label} ({JobQuotes.Amount(QuotedAmount!.Value)}) for approval",
            [new FieldChange("approval", from.ToString(), ApprovalStatus.ToString())]));
    }

    /// <summary>
    /// The contact's decision (SPEC-V2 5.4): once, while the quote is pending, with an optional comment. The audit names
    /// the contact (their portal session is the scope). Two contacts deciding at once: the version check lets one win.
    /// </summary>
    public void Decide(bool approve, Guid contactId, string? comment, DateTimeOffset now)
    {
        if (ApprovalStatus != JobApprovalStatus.Pending)
        {
            throw new DomainException("quote-already-decided", "This quote is no longer waiting for a decision.");
        }

        var normalized = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (normalized is { Length: > JobQuotes.CommentMaxLength })
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["comment"] = [$"Use at most {JobQuotes.CommentMaxLength} characters."] });
        }

        ApprovalStatus = approve ? JobApprovalStatus.Approved : JobApprovalStatus.Declined;
        ApprovedAmount = approve ? QuotedAmount : null;
        ApprovalDecidedAt = now;
        ApprovalDecidedByContactId = contactId;
        ApprovalComment = normalized;
        UpdatedAt = now;
        var changes = new List<FieldChange> { new("approval", JobApprovalStatus.Pending.ToString(), ApprovalStatus.ToString()) };
        if (normalized is not null)
        {
            changes.Add(new FieldChange("comment", null, normalized));
        }

        Raise(new JobEvent(
            this,
            approve ? "job.quote_approved" : "job.quote_declined",
            approve ? $"Approved the quote for {Label} ({JobQuotes.Amount(QuotedAmount!.Value)})" : $"Declined the quote for {Label}",
            changes));
    }

    /// <summary>
    /// Whether a change to the agreement reaches this visit (SPEC-V2 5.1): it is the agreement's, today or later, New or
    /// Scheduled, and nobody has edited it.
    /// </summary>
    public bool FollowsAgreement(Guid agreementId, DateOnly today) =>
        AgreementId == agreementId && !ChangedByHand && OccurrenceOn >= today && Status is JobStatus.New or JobStatus.Scheduled;

    /// <summary>
    /// Brings a following visit in line with the agreement's template, recorded as the system. The status follows the
    /// assignee as at generation (Scheduled with one, New without); nobody has worked on such a visit yet.
    /// </summary>
    public bool FollowAgreement(Agreement agreement, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
    {
        var details = agreement.Template.ForVisit(ClientId, OccurrenceOn!.Value);
        var status = details.AssigneeUserId is null ? JobStatus.New : JobStatus.Scheduled;
        var changes = Details.ChangesTo(details, names).ToList();
        if (status != Status)
        {
            changes.Add(new FieldChange("status", JobLabels.Of(Status), JobLabels.Of(status)));
        }

        if (changes.Count == 0)
        {
            return false;
        }

        Apply(details);
        Status = status;
        UpdatedAt = now;
        var fields = string.Join(", ", changes.Select(c => FieldLabels.GetValueOrDefault(c.Field, c.Field)));
        Raise(new JobEvent(this, "job.updated", $"Updated {Label} to follow its agreement: {fields}", changes, agreement.SystemActor));
        return true;
    }

    /// <summary>
    /// A following visit whose date the agreement no longer has (its schedule changed): recorded as deleted by the
    /// system; the caller removes the row. Nobody had touched it, so no work is lost.
    /// </summary>
    public void WithdrawFromAgreement(Agreement agreement) =>
        Raise(new JobEvent(this, "job.deleted", $"Deleted {Label} “{Title}”: the agreement no longer has a visit on {JobDetails.Date(OccurrenceOn)}", [], agreement.SystemActor));

    /// <summary>
    /// Skips this one visit (SPEC-V2 5.1): cancelled with the reason "Skipped". It keeps its occurrence date, so it is
    /// not generated again. Only visits of an agreement can be skipped; the workflow decides from which statuses.
    /// </summary>
    public void Skip(JobActor actor, DateTimeOffset now)
    {
        if (AgreementId is null)
        {
            throw new DomainException("not-a-visit", $"{Label} is not a visit of an agreement; cancel it instead.");
        }

        TransitionTo(JobStatus.Cancelled, actor, "Skipped", now);
    }

    /// <summary>Moves along the workflow, or explains why not (409 with the allowed list, 403, or 400 with the missing field).</summary>
    public void TransitionTo(JobStatus target, JobActor actor, string? reason, DateTimeOffset now)
    {
        var transition = JobWorkflow.Find(Status, target)
            ?? throw new InvalidJobTransitionException(this, target, AllowedFor(actor));

        if (!MayPerform(transition, actor))
        {
            throw transition.AssigneeMayPerform
                ? new DomainException("job-not-yours", "You can only change the status of jobs assigned to you.", DomainErrorKind.Forbidden)
                : ManagersOnly("this status change");
        }

        // SPEC-V2 5.4 adds one condition to the v1 table: work does not start on a quote the client has not approved.
        if (target == JobStatus.InProgress && JobQuotes.WhyNotStart(ApprovalStatus) is { } why)
        {
            throw new DomainException("quote-not-approved", why);
        }

        var missing = MissingFor(transition, reason, checkReason: true);
        if (missing.Count > 0)
        {
            throw new DomainValidationException(missing);
        }

        var normalizedReason = NormalizeReason(reason);
        var from = Status;
        Status = target;
        UpdatedAt = now;
        CompletedAt = target == JobStatus.Completed ? now : null;

        var changes = new List<FieldChange> { new("status", JobLabels.Of(from), JobLabels.Of(target)) };
        if (normalizedReason is not null)
        {
            changes.Add(new FieldChange("reason", null, normalizedReason));
        }

        var verb = (from, target) switch
        {
            (_, JobStatus.Cancelled) => $"Cancelled {Label}",
            (JobStatus.Completed, _) => $"Reopened {Label}",
            _ => $"Moved {Label} from {JobLabels.Of(from)} to {JobLabels.Of(target)}",
        };
        Raise(new JobEvent(this, "job.status_changed", normalizedReason is null ? verb : $"{verb}: {normalizedReason}", changes));
    }

    /// <summary>The statuses the actor could move this job to right now by permission (requirements aside).</summary>
    public IReadOnlyList<JobStatus> AllowedFor(JobActor actor) =>
        [.. JobWorkflow.From(Status).Where(t => MayPerform(t, actor)).Select(t => t.To)];

    /// <summary>What the detail page offers: each allowed move, and what is still missing before it can be made (null when ready).</summary>
    public IReadOnlyList<JobTransitionOption> OptionsFor(JobActor actor) =>
    [
        .. JobWorkflow.From(Status).Where(t => MayPerform(t, actor)).Select(t =>
            new JobTransitionOption(
                t.To,
                t.NeedsReason,
                (t.To == JobStatus.InProgress ? JobQuotes.WhyNotStart(ApprovalStatus) : null)
                    ?? MissingFor(t, reason: null, checkReason: false).Values.FirstOrDefault()?[0])),
    ];

    /// <summary>Whether the actor may edit this job (not whether the edit is valid).</summary>
    public bool CanEdit(JobActor actor) => actor.CanManage || (actor.CanWork && AssigneeUserId == actor.UserId);

    /// <summary>Overdue: due before today in the organization's time zone, and not closed (SPEC 5.4).</summary>
    public bool IsOverdue(DateOnly today) => IsOverdue(DueOn, Status, today);

    public static bool IsOverdue(DateOnly? dueOn, JobStatus status, DateOnly today) =>
        dueOn is { } due && due < today && status.IsOpen();

    /// <summary>A removed or departed member no longer works on this job (SPEC 5.2). Closed jobs keep their assignee.</summary>
    public bool Unassign(string memberName, string why, DateTimeOffset now)
    {
        if (AssigneeUserId is null || !Status.IsOpen())
        {
            return false;
        }

        AssigneeUserId = null;
        UpdatedAt = now;
        Raise(new JobEvent(this, "job.unassigned", $"Unassigned {Label} because {memberName} {why}", [new FieldChange("assignee", memberName, null)]));
        return true;
    }

    /// <summary>Records the deletion (New or Cancelled jobs only); the caller removes the row in the same save.</summary>
    public void MarkDeleted(JobActor actor)
    {
        if (!actor.CanManage)
        {
            throw ManagersOnly("deleting a job");
        }

        // A visit's row is what keeps its date from being generated again (SPEC-V2 5.1): it is skipped or cancelled,
        // never deleted. Edits to the agreement withdraw untouched visits through WithdrawFromAgreement instead.
        if (AgreementId is not null)
        {
            throw new DomainException(
                "visit-not-deletable",
                $"{Label} is a visit of an agreement and would be generated again. Skip it or cancel it instead.");
        }

        if (Status is not (JobStatus.New or JobStatus.Cancelled))
        {
            throw new DomainException("job-not-deletable", "Only New or Cancelled jobs can be deleted. Cancel the job first.");
        }

        Raise(new JobEvent(this, "job.deleted", $"Deleted {Label} “{Title}”", []));
    }

    private static void EnsureAssigneeAllowed(Guid? requested, JobActor actor, Guid? current)
    {
        // Unchanged is always fine; otherwise a non-manager may only take an unassigned job for themselves.
        if (requested != current && !(current is null && requested == actor.UserId))
        {
            throw ManagersOnly("assigning a job to someone else");
        }
    }

    private static DomainException ManagersOnly(string what) =>
        new("manager-only", $"Only Admins and Owners can do this ({what}).", DomainErrorKind.Forbidden);

    private static string? NormalizeReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    private bool MayPerform(JobTransition transition, JobActor actor) =>
        actor.CanManage || (transition.AssigneeMayPerform && actor.CanWork && AssigneeUserId == actor.UserId);

    private Dictionary<string, string[]> MissingFor(JobTransition transition, string? reason, bool checkReason)
    {
        var missing = new Dictionary<string, string[]>();
        if (transition.NeedsAssignee && AssigneeUserId is null)
        {
            missing["assigneeUserId"] = ["Assign the job to someone first."];
        }

        if (transition.NeedsScheduledOn && ScheduledOn is null)
        {
            missing["scheduledOn"] = ["Set a scheduled date first."];
        }

        if (checkReason && transition.NeedsReason)
        {
            var normalized = NormalizeReason(reason);
            if (normalized is null)
            {
                missing["reason"] = [$"Give a reason of 1–{JobWorkflow.ReasonMaxLength} characters."];
            }
            else if (normalized.Length > JobWorkflow.ReasonMaxLength)
            {
                missing["reason"] = [$"Use at most {JobWorkflow.ReasonMaxLength} characters."];
            }
        }

        return missing;
    }

    private void ClearDecision()
    {
        ApprovedAmount = null;
        ApprovalDecidedAt = null;
        ApprovalDecidedByContactId = null;
        ApprovalComment = null;
    }

    private void Apply(JobDetails details)
    {
        // A quote is approved for an amount and a client: changing either asks again (SPEC-V2 5.4). Applies to a pending
        // quote too, since the client would otherwise approve terms they never saw.
        if (ApprovalStatus is JobApprovalStatus.Pending or JobApprovalStatus.Approved
            && (details.QuotedAmount != QuotedAmount || details.ClientId != ClientId))
        {
            var from = ApprovalStatus;
            ApprovalStatus = JobApprovalStatus.NotRequested;
            ClearDecision();
            Raise(new JobEvent(
                this,
                "job.quote_reset",
                $"The quote for {Label} needs a new decision: its {(details.ClientId != ClientId ? "client" : "amount")} changed",
                [new FieldChange("approval", from.ToString(), ApprovalStatus.ToString())]));
        }

        Title = details.Title;
        Description = details.Description;
        ClientId = details.ClientId;
        AssigneeUserId = details.AssigneeUserId;
        Priority = details.Priority;
        ScheduledOn = details.ScheduledOn;
        DueOn = details.DueOn;
        QuotedAmount = details.QuotedAmount;
        EstimatedMinutes = details.EstimatedMinutes;
    }
}

/// <summary>A move the actor may make, and what is still missing before it can be made (null when ready).</summary>
public sealed record JobTransitionOption(JobStatus To, bool NeedsReason, string? BlockedReason);

/// <summary>The requested status is not reachable from the current one; carries the ones the actor can choose (409).</summary>
public sealed class InvalidJobTransitionException(Job job, JobStatus target, IReadOnlyList<JobStatus> allowed)
    : DomainException(
        "invalid-transition",
        $"{job.Label} is {JobLabels.Of(job.Status)} and cannot move to {JobLabels.Of(target)}.")
{
    public IReadOnlyList<JobStatus> Allowed { get; } = allowed;
}

public sealed record JobEvent(Job Job, string Action, string Summary, IReadOnlyList<FieldChange> Changes, AuditActor? Actor = null) : IAuditableEvent
{
    public Guid OrganizationId => Job.OrganizationId;

    public string EntityType => "job";

    public Guid EntityId => Job.Id;
}
