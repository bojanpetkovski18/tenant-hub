using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Jobs;

/// <summary>
/// A unit of work for a client (SPEC 5.4). The status only moves along <see cref="JobWorkflow"/>; who may do what is
/// decided here from the <see cref="JobActor"/> and the assignee, so every entry point (API, seed, tests) obeys the
/// same rules. Requirements such as "assignee" are checked at the transition, not as standing invariants: removing a
/// member unassigns their open jobs without moving them.
/// </summary>
public sealed class Job : AggregateRoot, ITenantOwned
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

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Optimistic-concurrency token; the database bumps it on every update.</summary>
    public uint Version { get; private set; }

    public string Label => FormatNumber(Number);

    public JobDetails Details => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount);

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
        var fields = string.Join(", ", changes.Select(c => FieldLabels.GetValueOrDefault(c.Field, c.Field)));
        Raise(new JobEvent(this, "job.updated", $"Updated {Label}: {fields}", changes));
        return true;
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
            new JobTransitionOption(t.To, t.NeedsReason, MissingFor(t, reason: null, checkReason: false).Values.FirstOrDefault()?[0])),
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

    private void Apply(JobDetails details)
    {
        Title = details.Title;
        Description = details.Description;
        ClientId = details.ClientId;
        AssigneeUserId = details.AssigneeUserId;
        Priority = details.Priority;
        ScheduledOn = details.ScheduledOn;
        DueOn = details.DueOn;
        QuotedAmount = details.QuotedAmount;
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

public sealed record JobEvent(Job Job, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Job.OrganizationId;

    public string EntityType => "job";

    public Guid EntityId => Job.Id;
}
