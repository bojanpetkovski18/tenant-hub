using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Organizations;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Integrations;

/// <summary>Keeps webhook secrets readable to the signer only (Data Protection, a purpose of its own).</summary>
public interface IWebhookSecretProtector
{
    string Protect(string secret);

    string Unprotect(string protectedSecret);
}

/// <summary>What one delivery attempt came to: the status (none when no response came), how long, and the class of error.</summary>
public sealed record WebhookResult(int? StatusCode, int DurationMs, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Posts a signed body to a customer's URL (SPEC-V2 5.8): public addresses only, checked at connection time; no
/// redirects; 10 seconds. Never throws for the target's failures: they come back as <see cref="WebhookResult.Error"/>.
/// </summary>
public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(Uri url, string body, string signature, Guid eventId, CancellationToken cancellationToken);
}

/// <summary>
/// The signature (SPEC-V2 5.8, D044): <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "&lt;t&gt;.&lt;body&gt;")&gt;</c>,
/// with a second <c>v1</c> while a rotated secret still overlaps. Receivers check any <c>v1</c> and reject old <c>t</c>.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "TenantHub-Signature";

    public static string Sign(IEnumerable<string> secrets, DateTimeOffset at, string body)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var signatures = secrets.Select(secret => "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)));
        return $"t={timestamp},{string.Join(",", signatures)}";
    }

    /// <summary>A new secret: <c>whsec_</c> and 32 random bytes in hex.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>A catalog event, as the change that caused it saw it; written to the outbox in the change's own save.</summary>
public sealed record WebhookEventMessage(Guid EventId, string Type, DateTimeOffset OccurredAt, JsonElement Data)
{
    public static readonly OutboxMessageType FanOut = new("webhooks.fan-out", CarriesSecret: false);
}

/// <summary>
/// One endpoint's copy of an event, with the body exactly as it is sent every time: the event id and body never change
/// across retries or a redelivery, so receivers can drop duplicates by <c>id</c>.
/// </summary>
public sealed record WebhookDeliveryMessage(Guid EndpointId, Guid EventId, string EventType, string Body)
{
    /// <summary>Eight attempts over about a day (SPEC-V2 5.8): 1 and 5 minutes, half an hour, 2, 4, 8 and 10 hours.</summary>
    public static readonly OutboxMessageType Deliver = new(
        "webhooks.deliver",
        CarriesSecret: false,
        RetryDelays: [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(10)]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The documented payload (SPEC-V2 5.8): <c>{ id, type, occurredAt, organizationId, data }</c>.</summary>
    public static string BodyFor(Guid eventId, string type, DateTimeOffset occurredAt, Guid organizationId, JsonElement data) =>
        JsonSerializer.Serialize(new { id = eventId, type, occurredAt, organizationId, data }, Json);
}

/// <summary>
/// Which audited changes are catalog events (SPEC-V2 5.8), and what they carry: the job or request as it is after the
/// change, ids and plain values only. Changes the system makes for an agreement are events too: a receiver that mirrors
/// jobs needs the generated visits.
/// </summary>
public static class WebhookTriggers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<(string Type, JsonElement Data)> For(IAuditableEvent change)
    {
        switch (change)
        {
            case JobEvent job:
                var types = job.Action switch
                {
                    "job.created" when job.Job.AssigneeUserId is not null => [WebhookEvents.JobCreated, WebhookEvents.JobAssigned],
                    "job.created" => [WebhookEvents.JobCreated],
                    "job.updated" when job.Changes.Any(c => c.Field == "assignee") && job.Job.AssigneeUserId is not null => [WebhookEvents.JobAssigned],
                    "job.status_changed" => [WebhookEvents.JobStatusChanged],
                    "job.quote_approved" => [WebhookEvents.QuoteApproved],
                    "job.quote_declined" => [WebhookEvents.QuoteDeclined],
                    "job.client_confirmed" => [WebhookEvents.VisitConfirmed],
                    "job.client_reported_problem" => [WebhookEvents.VisitProblemReported],
                    _ => Array.Empty<string>(),
                };
                return [.. types.Select(t => (t, Data(JobData(job.Job))))];
            case WorkRequestEvent { Action: "request.submitted" } request:
                var r = request.Request;
                return [(WebhookEvents.RequestCreated, Data(new { id = r.Id, title = r.Title, description = r.Description, clientId = r.ClientId, preferredOn = r.PreferredOn, status = Camel(r.Status.ToString()) }))];
            default:
                return [];
        }
    }

    private static object JobData(Job j) => new
    {
        id = j.Id,
        number = j.Number,
        label = j.Label,
        title = j.Title,
        status = Camel(j.Status.ToString()),
        clientId = j.ClientId,
        assigneeUserId = j.AssigneeUserId,
        scheduledOn = j.ScheduledOn,
        dueOn = j.DueOn,
        quotedAmount = j.QuotedAmount,
        quote = new { status = Camel(j.ApprovalStatus.ToString()), approvedAmount = j.ApprovedAmount, comment = j.ApprovalComment },
        clientFeedback = j.ClientFeedback is { } feedback ? new { kind = Camel(feedback.ToString()), text = j.ClientFeedbackText } : null,
        agreementId = j.AgreementId,
    };

    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Json);

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}

public sealed record WebhookEndpointView(
    Guid Id,
    string Url,
    IReadOnlyList<string> Events,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    DateTimeOffset? FailingSince,
    bool RotationOverlap,
    uint Version,
    WebhookDeliveryView? LastDelivery);

/// <summary>A new endpoint or a rotated secret, with the secret shown this once.</summary>
public sealed record WebhookWithSecret(WebhookEndpointView Endpoint, string Secret);

public sealed record WebhookDeliveryView(
    Guid Id,
    Guid EventId,
    string EventType,
    int Attempt,
    int? StatusCode,
    int DurationMs,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    bool Succeeded);

public sealed record WebhookInput(string? Url, IReadOnlyList<string>? Events, bool Enabled = true, uint? Version = null);

/// <summary>
/// Webhook endpoints (US-111, SPEC-V2 5.8): a Business feature for those with <c>integrations.manage</c>. Secrets are
/// generated here, shown once and kept encrypted. Deliveries are read-only, except "deliver again" and the test event.
/// </summary>
public sealed class WebhookCommands(
    IAppDbContext db,
    ITenantContext tenant,
    PlanLimitsCheck limits,
    IDatabaseLocks locks,
    IWebhookSecretProtector secrets,
    IOutbox outbox,
    WebhookAddresses addresses,
    TimeProvider time)
{
    public async Task<IReadOnlyList<WebhookEndpointView>> ListAsync(CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var endpoints = await db.WebhookEndpoints.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync(cancellationToken);
        var ids = endpoints.Select(e => e.Id).ToList();
        var last = (await db.WebhookDeliveries.AsNoTracking()
                .Where(d => ids.Contains(d.EndpointId))
                .GroupBy(d => d.EndpointId)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync(cancellationToken))
            .ToDictionary(d => d.EndpointId);
        return [.. endpoints.Select(e => View(e, last.GetValueOrDefault(e.Id)))];
    }

    public async Task<WebhookWithSecret> CreateAsync(WebhookInput input, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var (url, events) = Validate(input);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        var secret = WebhookSignature.NewSecret();
        var endpoint = WebhookEndpoint.Add(
            tenant.OrganizationId!.Value, url, events, secrets.Protect(secret), await db.WebhookEndpoints.CountAsync(cancellationToken), time.GetUtcNow());
        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WebhookWithSecret(View(endpoint, null), secret);
    }

    public async Task<WebhookEndpointView> UpdateAsync(Guid id, WebhookInput input, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var (url, events) = Validate(input);
        var endpoint = await LoadAsync(id, cancellationToken);
        if (input.Version is { } version)
        {
            db.Entry(endpoint).Property(e => e.Version).OriginalValue = version;
        }

        endpoint.Update(url, events, input.Enabled, time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return View(endpoint, null);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var endpoint = await LoadAsync(id, cancellationToken);
        endpoint.MarkDeleted();
        db.WebhookEndpoints.Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<WebhookWithSecret> RotateSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var endpoint = await LoadAsync(id, cancellationToken);
        var secret = WebhookSignature.NewSecret();
        endpoint.RotateSecret(secrets.Protect(secret), time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return new WebhookWithSecret(View(endpoint, null), secret);
    }

    public async Task<PagedList<WebhookDeliveryView>> DeliveriesAsync(Guid id, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        await LoadAsync(id, cancellationToken, tracked: false);
        var paging = PageRequest.From(page, pageSize);
        var query = db.WebhookDeliveries.AsNoTracking().Where(d => d.EndpointId == id);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Skip(paging.Skip).Take(paging.PageSize).ToListAsync(cancellationToken);
        return new PagedList<WebhookDeliveryView>([.. rows.Select(DeliveryView)], paging.Page, paging.PageSize, total);
    }

    /// <summary>Sends the same body again (same event id), as a new series of attempts. Audited: managers can see who did it.</summary>
    public async Task RedeliverAsync(Guid id, Guid deliveryId, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var endpoint = await LoadAsync(id, cancellationToken);
        var delivery = await db.WebhookDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deliveryId && d.EndpointId == id, cancellationToken)
            ?? throw new DomainException("delivery-not-found", "Delivery not found.", DomainErrorKind.NotFound);
        EnsureEnabled(endpoint);
        outbox.Add(WebhookDeliveryMessage.Deliver, new WebhookDeliveryMessage(endpoint.Id, delivery.EventId, delivery.EventType, delivery.Body));
        endpoint.RecordRedelivery(delivery.EventType, delivery.EventId);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>"Send a test event": a <c>webhook.ping</c> through the same signing, guard and log as real events.</summary>
    public async Task SendTestAsync(Guid id, CancellationToken cancellationToken)
    {
        await limits.EnsureFeatureAsync(PlanFeature.Integrations, cancellationToken);
        var endpoint = await LoadAsync(id, cancellationToken);
        EnsureEnabled(endpoint);
        var now = time.GetUtcNow();
        var eventId = Guid.CreateVersion7(now);
        var data = JsonSerializer.SerializeToElement(new { message = "A test event from TenantHub." });
        outbox.Add(
            WebhookDeliveryMessage.Deliver,
            new WebhookDeliveryMessage(endpoint.Id, eventId, WebhookEvents.Ping, WebhookDeliveryMessage.BodyFor(eventId, WebhookEvents.Ping, now, endpoint.OrganizationId, data)));
        endpoint.RecordRedelivery(WebhookEvents.Ping, eventId);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureEnabled(WebhookEndpoint endpoint)
    {
        if (!endpoint.IsEnabled)
        {
            throw new DomainException("webhook-disabled", "This endpoint is disabled. Enable it first.");
        }
    }

    private (Uri Url, IReadOnlyList<string> Events) Validate(WebhookInput input)
    {
        var errors = new Dictionary<string, string[]>();
        Uri? url = null;
        IReadOnlyList<string> events = [];
        try
        {
            url = addresses.Validate(input.Url);
        }
        catch (DomainValidationException invalid)
        {
            foreach (var (k, v) in invalid.Errors)
            {
                errors[k] = v;
            }
        }

        try
        {
            events = WebhookEndpoint.ValidateEvents(input.Events);
        }
        catch (DomainValidationException invalid)
        {
            foreach (var (k, v) in invalid.Errors)
            {
                errors[k] = v;
            }
        }

        return errors.Count > 0 ? throw new DomainValidationException(errors) : (url!, events);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException("concurrency-conflict", "Someone else changed this endpoint after you opened it. Reload to see their changes.");
        }
    }

    private async Task<WebhookEndpoint> LoadAsync(Guid id, CancellationToken cancellationToken, bool tracked = true)
    {
        var query = db.WebhookEndpoints.Where(e => e.Id == id);
        return await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken)
            ?? throw new DomainException("webhook-not-found", "Webhook endpoint not found.", DomainErrorKind.NotFound);
    }

    private WebhookEndpointView View(WebhookEndpoint e, WebhookDelivery? last) => new(
        e.Id,
        e.Url,
        e.Events,
        e.IsEnabled,
        e.CreatedAt,
        e.DisabledAt,
        e.FailingSince,
        e.PreviousSecretAt(time.GetUtcNow()) is not null,
        e.Version,
        last is null ? null : DeliveryView(last));

    private static WebhookDeliveryView DeliveryView(WebhookDelivery d) =>
        new(d.Id, d.EventId, d.EventType, d.Attempt, d.StatusCode, d.DurationMs, d.Error, d.CreatedAt, d.NextAttemptAt, d.SucceededAt is not null);
}

/// <summary>
/// Which webhook URLs are accepted (SPEC-V2 5.8): <c>https</c> only. A development-only setting names one local receiver
/// (<see cref="AllowedReceiver"/>), which may be <c>http</c> and private; the guard lets exactly that host and port through.
/// </summary>
public sealed class WebhookAddresses(Uri? allowedReceiver)
{
    public Uri? AllowedReceiver { get; } = allowedReceiver;

    public Uri Validate(string? url)
    {
        if (AllowedReceiver is { } allowed && Uri.TryCreate(url, UriKind.Absolute, out var candidate) && IsAllowedReceiver(candidate.Host, candidate.Port))
        {
            return WebhookEndpoint.ValidateUrl(url, allowHttpFor: allowed.Scheme == Uri.UriSchemeHttp);
        }

        return WebhookEndpoint.ValidateUrl(url);
    }

    public bool IsAllowedReceiver(string host, int port) =>
        AllowedReceiver is { } allowed
        && string.Equals(allowed.Host, host, StringComparison.OrdinalIgnoreCase)
        && allowed.Port == port;
}
