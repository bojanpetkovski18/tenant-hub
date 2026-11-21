using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Integrations;

/// <summary>
/// Turns one catalog event into one delivery message per enabled endpoint that subscribes to it (SPEC-V2 5.8), as the
/// system inside the event's organization. The body is written once, here, and never changes after: every retry and
/// every "deliver again" sends the same bytes with the same <c>id</c>. A fan-out delivered twice after a crash queues
/// the event twice; the stable id is what lets receivers drop the copy.
/// </summary>
public sealed class WebhookFanOut(IAppDbContext db, TenantContext tenant, IOutbox outbox)
{
    public async Task DeliverAsync(Guid organizationId, WebhookEventMessage message, CancellationToken cancellationToken)
    {
        tenant.EnterAsSystem(organizationId);
        var endpoints = await db.WebhookEndpoints.AsNoTracking()
            .Where(e => e.DisabledAt == null && e.Events.Contains(message.Type))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return;
        }

        var plan = await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Plan).SingleAsync(cancellationToken);
        if (!PlanFeatures.Includes(plan, PlanFeature.Integrations))
        {
            return; // endpoints kept from a Business plan stay quiet below it
        }

        var body = WebhookDeliveryMessage.BodyFor(message.EventId, message.Type, message.OccurredAt, organizationId, message.Data);
        foreach (var endpoint in endpoints)
        {
            outbox.Add(WebhookDeliveryMessage.Deliver, new WebhookDeliveryMessage(endpoint, message.EventId, message.Type, body));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// One attempt at one delivery (SPEC-V2 5.8): signs with the current secret (and the previous one during its overlap),
/// posts through the guarded sender, records the attempt and the endpoint's health, and fails the outbox message so the
/// dispatcher retries on its schedule. Three days of failures disable the endpoint, which notifies its Owners (through
/// the audit of <c>webhook.disabled</c>) and stops the retries.
/// </summary>
public sealed class WebhookDeliverer(
    IAppDbContext db,
    TenantContext tenant,
    IWebhookSecretProtector secrets,
    IWebhookSender sender,
    IDatabaseLocks locks,
    TimeProvider time)
{

    public async Task DeliverAsync(Guid organizationId, WebhookDeliveryMessage message, int attempt, IReadOnlyList<TimeSpan> retryDelays, CancellationToken cancellationToken)
    {
        tenant.EnterAsSystem(organizationId);
        var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == message.EndpointId, cancellationToken);
        if (endpoint is null || !endpoint.IsEnabled)
        {
            return; // removed or disabled since: nothing to send, and nothing to retry
        }

        var plan = await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Plan).SingleAsync(cancellationToken);
        if (!PlanFeatures.Includes(plan, PlanFeature.Integrations))
        {
            return;
        }

        var now = time.GetUtcNow();
        var signingSecrets = new List<string> { secrets.Unprotect(endpoint.SecretEncrypted) };
        if (endpoint.PreviousSecretAt(now) is { } previous)
        {
            signingSecrets.Add(secrets.Unprotect(previous));
        }

        var signature = WebhookSignature.Sign(signingSecrets, now, message.Body);
        var result = await sender.SendAsync(new Uri(endpoint.Url), message.Body, signature, message.EventId, cancellationToken);

        var disabled = await RecordAsync(message, attempt, retryDelays, result, cancellationToken);
        if (!result.Succeeded)
        {
            // The class of error only (the row and the log never carry the target's response).
            throw new OutboxDeliveryException(result.Error!, permanent: disabled);
        }
    }

    /// <summary>
    /// Writes the attempt with the endpoint's health in one save. Deliveries to one endpoint run side by side, so the
    /// endpoint's row is locked while its failure window is read and changed.
    /// </summary>
    private async Task<bool> RecordAsync(WebhookDeliveryMessage message, int attempt, IReadOnlyList<TimeSpan> retryDelays, WebhookResult result, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockWebhookEndpointAsync(message.EndpointId, cancellationToken);
        var endpoint = await db.WebhookEndpoints.SingleOrDefaultAsync(e => e.Id == message.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return true; // removed while we were sending: its deliveries went with it
        }

        var now = time.GetUtcNow();
        var disabled = false;
        if (result.Succeeded)
        {
            endpoint.RecordSuccess();
        }
        else
        {
            disabled = endpoint.RecordFailure(now);
        }

        var retries = !result.Succeeded && endpoint.IsEnabled && attempt <= retryDelays.Count;
        db.WebhookDeliveries.Add(WebhookDelivery.Record(
            endpoint,
            message.EventId,
            message.EventType,
            message.Body,
            attempt,
            result.StatusCode,
            result.DurationMs,
            result.Error,
            retries ? now + retryDelays[attempt - 1] : null,
            now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return disabled || !endpoint.IsEnabled;
    }
}
