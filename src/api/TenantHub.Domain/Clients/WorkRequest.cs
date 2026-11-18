namespace TenantHub.Domain.Clients;

public enum WorkRequestStatus
{
    Open,
    Converted,
    Declined,
}

/// <summary>
/// Work a client asks for through the portal (US-84, SPEC-V2 5.5). Open until staff convert it into a job (it then
/// links to the job) or decline it with a reason the contact can read; no other moves.
/// </summary>
public sealed class WorkRequest : AggregateRoot, IClientOwned
{
    public const int TitleMinLength = 3;
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 2000;
    public const int ReasonMaxLength = 500;
    public const int MaxOpenPerContact = 10;

    private WorkRequest()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid ContactId { get; private set; }

    public string Title { get; private set; } = "";

    public string? Description { get; private set; }

    public DateOnly? PreferredOn { get; private set; }

    public WorkRequestStatus Status { get; private set; }

    public Guid? JobId { get; private set; }

    public string? DeclineReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    /// <summary>Optimistic-concurrency token: two people deciding at once make one decision.</summary>
    public uint Version { get; private set; }

    /// <summary>
    /// A contact asks for work. <paramref name="openRequests"/> is the contact's open requests, counted by the caller;
    /// <paramref name="today"/> is the organization's (a preferred date may not be in the past).
    /// </summary>
    public static WorkRequest Submit(ClientContact contact, string? title, string? description, DateOnly? preferredOn, int openRequests, DateOnly today, DateTimeOffset now)
    {
        var normalizedTitle = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var errors = new Dictionary<string, string[]>();
        if (normalizedTitle.Length is < TitleMinLength or > TitleMaxLength)
        {
            errors["title"] = [$"Describe the work in {TitleMinLength}–{TitleMaxLength} characters."];
        }

        if (normalizedDescription is { Length: > DescriptionMaxLength })
        {
            errors["description"] = [$"Use at most {DescriptionMaxLength} characters."];
        }

        if (preferredOn is { } preferred && preferred < today)
        {
            errors["preferredOn"] = ["Choose today or a later day."];
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        if (openRequests >= MaxOpenPerContact)
        {
            throw new DomainException(
                "request-limit-reached",
                $"You have {MaxOpenPerContact} open requests. Wait for an answer to one of them before asking for more.");
        }

        var request = new WorkRequest
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = contact.OrganizationId,
            ClientId = contact.ClientId,
            ContactId = contact.Id,
            Title = normalizedTitle,
            Description = normalizedDescription,
            PreferredOn = preferredOn,
            Status = WorkRequestStatus.Open,
            CreatedAt = now,
        };
        request.Raise(new WorkRequestEvent(request, "request.submitted", $"Requested work: “{request.Title}”", [new FieldChange("title", null, request.Title)]));
        return request;
    }

    /// <summary>Staff turned it into <paramref name="jobLabel"/>; the contact sees the job.</summary>
    public void Convert(Guid jobId, string jobLabel, Guid userId, DateTimeOffset now)
    {
        EnsureOpen();
        Status = WorkRequestStatus.Converted;
        JobId = jobId;
        DecidedAt = now;
        DecidedByUserId = userId;
        Raise(new WorkRequestEvent(this, "request.converted", $"Converted the request “{Title}” into {jobLabel}", [new FieldChange("status", "Open", "Converted")]));
    }

    /// <summary>Staff decline it, saying why; the contact reads the reason.</summary>
    public void Decline(string? reason, Guid userId, DateTimeOffset now)
    {
        EnsureOpen();
        var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalized is null or { Length: > ReasonMaxLength })
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["reason"] = [$"Give a reason of 1–{ReasonMaxLength} characters."] });
        }

        Status = WorkRequestStatus.Declined;
        DeclineReason = normalized;
        DecidedAt = now;
        DecidedByUserId = userId;
        Raise(new WorkRequestEvent(this, "request.declined", $"Declined the request “{Title}”: {normalized}", [new FieldChange("status", "Open", "Declined")]));
    }

    /// <summary>Its job was deleted (a New job can be): the request stays converted, without the link.</summary>
    public void ForgetJob() => JobId = null;

    private void EnsureOpen()
    {
        if (Status != WorkRequestStatus.Open)
        {
            throw new DomainException("request-not-open", $"This request was already {Status.ToString().ToLowerInvariant()}.");
        }
    }
}

public sealed record WorkRequestEvent(WorkRequest Request, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Request.OrganizationId;

    public string EntityType => "request";

    public Guid EntityId => Request.Id;
}
