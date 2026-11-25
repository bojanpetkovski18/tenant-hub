namespace TenantHub.Domain.Integrations;

/// <summary>The events a webhook endpoint can subscribe to (SPEC-V2 5.8): a fixed catalog with stable names.</summary>
public static class WebhookEvents
{
    public const string JobCreated = "job.created";
    public const string JobStatusChanged = "job.status_changed";
    public const string JobAssigned = "job.assigned";
    public const string QuoteApproved = "quote.approved";
    public const string QuoteDeclined = "quote.declined";
    public const string RequestCreated = "request.created";
    public const string VisitConfirmed = "visit.confirmed";
    public const string VisitProblemReported = "visit.problem_reported";

    /// <summary>Sent only by "Send a test event"; never subscribed to.</summary>
    public const string Ping = "webhook.ping";

    public static readonly IReadOnlyList<string> Catalog =
    [
        JobCreated, JobStatusChanged, JobAssigned, QuoteApproved, QuoteDeclined, RequestCreated, VisitConfirmed, VisitProblemReported,
    ];
}

/// <summary>
/// Where an organization's events are posted (US-111, SPEC-V2 5.8). At most five per organization, <c>https</c> only,
/// events from the catalog. The secret is kept encrypted, because signing needs it; rotating keeps the previous one
/// valid for a day, and both signatures are sent meanwhile. After three days of failed deliveries the endpoint is
/// disabled; enabling it again starts afresh.
/// </summary>
public sealed class WebhookEndpoint : AggregateRoot, ITenantOwned
{
    public const int MaxPerOrganization = 5;
    public const int UrlMaxLength = 500;

    public static readonly TimeSpan RotationOverlap = TimeSpan.FromHours(24);

    public static readonly TimeSpan FailureWindow = TimeSpan.FromDays(3);

    private WebhookEndpoint()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Url { get; private set; } = "";

    public string[] Events { get; private set; } = [];

    public string SecretEncrypted { get; private set; } = "";

    public string? PreviousSecretEncrypted { get; private set; }

    public DateTimeOffset? PreviousSecretExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    /// <summary>When deliveries started failing without a success since; null while they succeed.</summary>
    public DateTimeOffset? FailingSince { get; private set; }

    public uint Version { get; private set; }

    public bool IsEnabled => DisabledAt is null;

    /// <summary>A URL as an address to deliver to: <c>https</c>, absolute, with a host and no credentials.</summary>
    public static Uri ValidateUrl(string? url, bool allowHttpFor = false)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > UrlMaxLength
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (allowHttpFor && uri.Scheme == Uri.UriSchemeHttp))
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["url"] = ["Use an https:// address, without a user name or password."] });
        }

        return uri;
    }

    public static IReadOnlyList<string> ValidateEvents(IReadOnlyList<string>? events)
    {
        var chosen = (events ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (chosen.Count == 0 || chosen.Any(e => !WebhookEvents.Catalog.Contains(e)))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["events"] = ["Choose at least one event from the list."] });
        }

        return [.. WebhookEvents.Catalog.Where(chosen.Contains)];
    }

    public static WebhookEndpoint Add(Guid organizationId, Uri url, IReadOnlyList<string> events, string secretEncrypted, int existing, DateTimeOffset now)
    {
        if (existing >= MaxPerOrganization)
        {
            throw new DomainException("webhook-limit-reached", $"An organization has at most {MaxPerOrganization} webhook endpoints.");
        }

        var endpoint = new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Url = url.ToString(),
            Events = [.. events],
            SecretEncrypted = secretEncrypted,
            CreatedAt = now,
        };
        endpoint.Raise(new WebhookEndpointEvent(endpoint, "webhook.created", $"Added the webhook endpoint {endpoint.Host}", [new FieldChange("events", null, string.Join(", ", events))]));
        return endpoint;
    }

    /// <summary>The host alone, for the audit and messages: the path may carry a customer's token.</summary>
    public string Host => new Uri(Url).Host;

    /// <summary>Changes the address or the events; enabling a disabled endpoint again starts its failure count afresh.</summary>
    public bool Update(Uri url, IReadOnlyList<string> events, bool enabled, DateTimeOffset now)
    {
        var changes = new List<FieldChange>();
        if (url.ToString() != Url)
        {
            changes.Add(new FieldChange("host", Host, url.Host));
            Url = url.ToString();
        }

        if (!events.SequenceEqual(Events))
        {
            changes.Add(new FieldChange("events", string.Join(", ", Events), string.Join(", ", events)));
            Events = [.. events];
        }

        if (enabled != IsEnabled)
        {
            changes.Add(new FieldChange("enabled", IsEnabled ? "yes" : "no", enabled ? "yes" : "no"));
            DisabledAt = enabled ? null : now;
            FailingSince = null;
        }

        if (changes.Count > 0)
        {
            Raise(new WebhookEndpointEvent(this, "webhook.updated", $"Updated the webhook endpoint {Host}", changes));
        }

        return changes.Count > 0;
    }

    /// <summary>A new secret; the old one keeps signing alongside it for <see cref="RotationOverlap"/>.</summary>
    public void RotateSecret(string newSecretEncrypted, DateTimeOffset now)
    {
        PreviousSecretEncrypted = SecretEncrypted;
        PreviousSecretExpiresAt = now + RotationOverlap;
        SecretEncrypted = newSecretEncrypted;
        Raise(new WebhookEndpointEvent(this, "webhook.secret_rotated", $"Rotated the signing secret of {Host}; the previous one works for another 24 hours", []));
    }

    /// <summary>The previous secret while it still signs; null otherwise.</summary>
    public string? PreviousSecretAt(DateTimeOffset now) => PreviousSecretExpiresAt > now ? PreviousSecretEncrypted : null;

    public void RecordSuccess() => FailingSince = null;

    /// <summary>A failed delivery. Returns true when it disables the endpoint: failing for longer than the window.</summary>
    public bool RecordFailure(DateTimeOffset now)
    {
        FailingSince ??= now;
        if (IsEnabled && now - FailingSince.Value >= FailureWindow)
        {
            DisabledAt = now;
            Raise(new WebhookEndpointEvent(this, "webhook.disabled", $"Disabled the webhook endpoint {Host}: deliveries have failed for three days", [], AuditActor.System("System")));
            return true;
        }

        return false;
    }

    /// <summary>Someone sent an event again, or the test event: audited, so managers can see who did it.</summary>
    public void RecordRedelivery(string eventType, Guid eventId) =>
        Raise(new WebhookEndpointEvent(
            this,
            eventType == WebhookEvents.Ping ? "webhook.test_sent" : "webhook.redelivered",
            eventType == WebhookEvents.Ping ? $"Sent a test event to {Host}" : $"Sent the {eventType} event again to {Host}",
            [new FieldChange("event", null, eventId.ToString())]));

    public void MarkDeleted() => Raise(new WebhookEndpointEvent(this, "webhook.deleted", $"Removed the webhook endpoint {Host}", []));
}

public sealed record WebhookEndpointEvent(
    WebhookEndpoint Endpoint,
    string Action,
    string Summary,
    IReadOnlyList<FieldChange> Changes,
    AuditActor? Actor = null) : IAuditableEvent
{
    public Guid OrganizationId => Endpoint.OrganizationId;

    public string EntityType => "webhook";

    public Guid EntityId => Endpoint.Id;
}

/// <summary>
/// One delivery attempt (SPEC-V2 5.8): the status code, how long it took and the class of error; never the response
/// body. <see cref="Body"/> is what was sent (the organization's own event), kept so it can be delivered again.
/// </summary>
public sealed class WebhookDelivery : ITenantOwned
{
    public const int ErrorMaxLength = 100;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private WebhookDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid EndpointId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = "";

    public int Attempt { get; private set; }

    public int? StatusCode { get; private set; }

    public int DurationMs { get; private set; }

    public string? Error { get; private set; }

    public string Body { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public DateTimeOffset? SucceededAt { get; private set; }

    public static WebhookDelivery Record(
        WebhookEndpoint endpoint,
        Guid eventId,
        string eventType,
        string body,
        int attempt,
        int? statusCode,
        int durationMs,
        string? error,
        DateTimeOffset? nextAttemptAt,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = endpoint.OrganizationId,
            EndpointId = endpoint.Id,
            EventId = eventId,
            EventType = eventType,
            Attempt = attempt,
            StatusCode = statusCode,
            DurationMs = durationMs,
            Error = error is { Length: > ErrorMaxLength } ? error[..ErrorMaxLength] : error,
            Body = body,
            CreatedAt = now,
            NextAttemptAt = nextAttemptAt,
            SucceededAt = error is null ? now : null,
        };
}
