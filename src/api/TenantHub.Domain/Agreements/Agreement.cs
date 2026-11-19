using TenantHub.Domain.Jobs;

namespace TenantHub.Domain.Agreements;

public enum AgreementStatus
{
    Active,
    Paused,
    Ended,
}

/// <summary>Why visits are not being generated although the agreement exists (SPEC-V2 5.1).</summary>
public enum GenerationHold
{
    /// <summary>The open-jobs limit stopped generation; the agreement stays active and the daily run tries again.</summary>
    PlanLimit,

    /// <summary>The client was archived, which pauses its agreements.</summary>
    ClientArchived,
}

/// <summary>
/// What every visit of an agreement starts as (SPEC-V2 4.2): the job fields a generated visit copies, and its checklist
/// (SPEC-V2 4.5). <see cref="Validate"/> uses the job rules, keyed by the API's property names.
/// </summary>
public sealed record AgreementTemplate(
    string Title,
    string? Description,
    Guid? AssigneeUserId,
    JobPriority Priority,
    decimal? QuotedAmount,
    int? EstimatedMinutes)
{
    /// <summary>The checklist each visit starts with; empty when the visits have none.</summary>
    public IReadOnlyList<ChecklistEntry> Checklist { get; init; } = [];

    public static AgreementTemplate Validate(
        string? title,
        string? description,
        Guid? assigneeUserId,
        JobPriority? priority,
        decimal? quotedAmount,
        int? estimatedMinutes,
        IReadOnlyList<(string? Text, bool? Required)>? checklist = null)
    {
        // The job's own rules; the client is the agreement's, so a placeholder satisfies that check.
        var details = JobDetails.Validate(title, description, Guid.NewGuid(), assigneeUserId, priority, null, null, quotedAmount, estimatedMinutes);
        return new AgreementTemplate(details.Title, details.Description, details.AssigneeUserId, details.Priority, details.QuotedAmount, details.EstimatedMinutes)
        {
            Checklist = ChecklistEntry.ValidateList(checklist),
        };
    }

    /// <summary>The job a visit on <paramref name="occurrence"/> is: scheduled and due on that date.</summary>
    public JobDetails ForVisit(Guid clientId, DateOnly occurrence) =>
        new(Title, Description, clientId, AssigneeUserId, Priority, occurrence, occurrence, QuotedAmount, EstimatedMinutes);
}

/// <summary>
/// Repeating work for one client (SPEC-V2 4.2, 5.1; D041): a visit template and a schedule. Its visits are ordinary
/// jobs, generated ahead by <see cref="VisitGeneration"/>; this aggregate holds the rules about the agreement itself.
/// The client never changes: an agreement for another client is another agreement.
/// </summary>
public sealed class Agreement : AggregateRoot, IClientOwned
{
    private Agreement()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public string Title { get; private set; } = "";

    public string? Description { get; private set; }

    public Guid? AssigneeUserId { get; private set; }

    public JobPriority Priority { get; private set; }

    public decimal? QuotedAmount { get; private set; }

    public int? EstimatedMinutes { get; private set; }

    /// <summary>The checklist copied into each generated visit (SPEC-V2 4.5); stored as one jsonb document.</summary>
    public IReadOnlyList<ChecklistEntry> ChecklistTemplate { get; private set; } = [];

    public Recurrence Recurrence { get; private set; } = null!;

    public DateOnly StartsOn { get; private set; }

    public DateOnly? EndsOn { get; private set; }

    public int? MaxVisits { get; private set; }

    public AgreementStatus Status { get; private set; }

    public GenerationHold? PausedReason { get; private set; }

    /// <summary>The last date generation has covered (today plus the horizon at the last run); null before the first.</summary>
    public DateOnly? GeneratedThrough { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic-concurrency token; the database bumps it on every update.</summary>
    public uint Version { get; private set; }

    public AgreementTemplate Template => new(Title, Description, AssigneeUserId, Priority, QuotedAmount, EstimatedMinutes) { Checklist = ChecklistTemplate };

    public Schedule Schedule => new(Recurrence, StartsOn, EndsOn, MaxVisits);

    /// <summary>How generated visits and their audit entries name the actor; fits the 80-character actor name.</summary>
    public AuditActor SystemActor => AuditActor.System($"System (agreement {(Title.Length > 60 ? Title[..59] + "…" : Title)})");

    /// <summary>The first visit cannot be before the organization's today (SPEC-V2 5.1).</summary>
    public static Agreement Create(
        Guid organizationId,
        Guid clientId,
        AgreementTemplate template,
        Schedule schedule,
        DateOnly today,
        Guid createdByUserId,
        IReadOnlyDictionary<Guid, string> names,
        DateTimeOffset now)
    {
        if (schedule.StartsOn < today)
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["startsOn"] = ["The first visit cannot be before today."] });
        }

        var agreement = new Agreement
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            ClientId = clientId,
            Status = AgreementStatus.Active,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
            UpdatedAt = now,
        };
        agreement.Apply(template, schedule);
        var changes = Fields(template, schedule, names)
            .Where(field => field.Value is not null)
            .Select(field => new FieldChange(field.Field, null, field.Value))
            .ToList();
        agreement.Raise(new AgreementEvent(agreement, "agreement.created", $"Created agreement “{agreement.Title}”", changes));
        return agreement;
    }

    /// <summary>Changes the template and the schedule; false (and nothing recorded) when nothing changed. Ended agreements are history.</summary>
    public bool Update(AgreementTemplate template, Schedule schedule, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
    {
        EnsureNotEnded();
        var changes = Fields(Template, Schedule, names).Zip(Fields(template, schedule, names))
            .Where(pair => !string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal))
            .Select(pair => new FieldChange(pair.First.Field, pair.First.Value, pair.Second.Value))
            .ToList();
        if (changes.Count == 0)
        {
            return false;
        }

        Apply(template, schedule);
        UpdatedAt = now;
        Raise(new AgreementEvent(this, "agreement.updated", $"Updated agreement “{Title}”: {string.Join(", ", changes.Select(c => Label(c.Field)))}", changes));
        return true;
    }

    public void Pause(DateTimeOffset now)
    {
        if (Status != AgreementStatus.Active)
        {
            throw NotInStatus("paused", "active");
        }

        SetStatus(AgreementStatus.Paused, null, $"Paused agreement “{Title}”", now);
    }

    /// <summary>The caller checks that the client is active and the plan has room (SPEC-V2 5.1, 4.8).</summary>
    public void Resume(DateTimeOffset now)
    {
        if (Status != AgreementStatus.Paused)
        {
            throw NotInStatus("resumed", "paused");
        }

        SetStatus(AgreementStatus.Active, null, $"Resumed agreement “{Title}”", now);
    }

    /// <summary>The caller cancels the future visits that are New or Scheduled ("Agreement ended").</summary>
    public void End(DateTimeOffset now)
    {
        EnsureNotEnded();
        SetStatus(AgreementStatus.Ended, null, $"Ended agreement “{Title}”", now);
    }

    /// <summary>Archiving the client pauses its active agreements (SPEC-V2 5.1); recorded as the archiving user's change.</summary>
    public bool PauseForArchivedClient(string clientName, DateTimeOffset now)
    {
        if (Status != AgreementStatus.Active)
        {
            return false;
        }

        SetStatus(AgreementStatus.Paused, GenerationHold.ClientArchived, $"Paused agreement “{Title}” because client “{clientName}” was archived", now);
        return true;
    }

    /// <summary>
    /// After a generation run: <paramref name="limitReached"/> when the open-jobs limit left visits out. Recorded (as
    /// the system) only when the hold starts or ends, not on every daily run.
    /// </summary>
    public void RecordGeneration(DateOnly through, bool limitReached, DateTimeOffset now)
    {
        GeneratedThrough = through;
        var held = PausedReason == GenerationHold.PlanLimit;
        if (limitReached == held || Status != AgreementStatus.Active)
        {
            return;
        }

        PausedReason = limitReached ? GenerationHold.PlanLimit : null;
        UpdatedAt = now;
        var summary = limitReached
            ? $"Stopped generating visits for “{Title}”: the plan's open-jobs limit is reached"
            : $"Generating visits for “{Title}” again";
        Raise(new AgreementEvent(this, limitReached ? "agreement.generation_held" : "agreement.generation_resumed", summary, [], SystemActor));
    }

    /// <summary>The template's assignee left or was removed (SPEC-V2 5.1, as v1 5.2 for jobs).</summary>
    public bool ClearAssignee(string memberName, string why, DateTimeOffset now)
    {
        if (AssigneeUserId is null || Status == AgreementStatus.Ended)
        {
            return false;
        }

        AssigneeUserId = null;
        UpdatedAt = now;
        Raise(new AgreementEvent(this, "agreement.unassigned", $"Unassigned agreement “{Title}” because {memberName} {why}", [new FieldChange("assignee", memberName, null)]));
        return true;
    }

    /// <summary>Records the deletion of an agreement that never had a visit, with its client; the caller removes the row.</summary>
    public void MarkDeleted() => Raise(new AgreementEvent(this, "agreement.deleted", $"Deleted agreement “{Title}”", []));

    private static (string Field, string? Value)[] Fields(AgreementTemplate template, Schedule schedule, IReadOnlyDictionary<Guid, string> names) =>
    [
        ("title", template.Title),
        ("description", template.Description),
        ("assignee", JobDetails.Name(names, template.AssigneeUserId)),
        ("priority", JobLabels.Of(template.Priority)),
        ("quotedAmount", JobDetails.Money(template.QuotedAmount)),
        ("estimatedMinutes", JobDetails.Minutes(template.EstimatedMinutes)),
        ("checklist", template.Checklist.Count == 0 ? null : string.Join("; ", template.Checklist.Select(e => e.Required ? $"{e.Text} (required)" : e.Text))),
        ("recurrence", schedule.Recurrence.Describe()),
        ("startsOn", JobDetails.Date(schedule.StartsOn)),
        ("endsOn", JobDetails.Date(schedule.EndsOn)),
        ("maxVisits", schedule.MaxVisits?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    private static string Label(string field) => field switch
    {
        "quotedAmount" => "quoted amount",
        "estimatedMinutes" => "estimated time",
        "startsOn" => "start",
        "endsOn" => "end date",
        "maxVisits" => "number of visits",
        _ => field,
    };

    private void Apply(AgreementTemplate template, Schedule schedule)
    {
        Title = template.Title;
        Description = template.Description;
        AssigneeUserId = template.AssigneeUserId;
        Priority = template.Priority;
        QuotedAmount = template.QuotedAmount;
        EstimatedMinutes = template.EstimatedMinutes;
        ChecklistTemplate = template.Checklist;
        Recurrence = schedule.Recurrence;
        StartsOn = schedule.StartsOn;
        EndsOn = schedule.EndsOn;
        MaxVisits = schedule.MaxVisits;
    }

    private void SetStatus(AgreementStatus status, GenerationHold? hold, string summary, DateTimeOffset now)
    {
        var changes = new List<FieldChange> { new("status", Status.ToString(), status.ToString()) };
        Status = status;
        PausedReason = hold;
        UpdatedAt = now;
        var action = status switch
        {
            AgreementStatus.Active => "agreement.resumed",
            AgreementStatus.Paused => "agreement.paused",
            _ => "agreement.ended",
        };
        Raise(new AgreementEvent(this, action, summary, changes));
    }

    private void EnsureNotEnded()
    {
        if (Status == AgreementStatus.Ended)
        {
            throw new DomainException("agreement-ended", $"The agreement “{Title}” has ended and can no longer change.");
        }
    }

    private DomainException NotInStatus(string verb, string required) =>
        new("agreement-status", $"Only {(required == "active" ? "an" : "a")} {required} agreement can be {verb}; “{Title}” is {Status.ToString().ToLowerInvariant()}.");
}

public sealed record AgreementEvent(
    Agreement Agreement,
    string Action,
    string Summary,
    IReadOnlyList<FieldChange> Changes,
    AuditActor? Actor = null) : IAuditableEvent
{
    public Guid OrganizationId => Agreement.OrganizationId;

    public string EntityType => "agreement";

    public Guid EntityId => Agreement.Id;
}
