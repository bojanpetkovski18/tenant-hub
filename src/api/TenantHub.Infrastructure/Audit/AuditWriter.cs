using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Integrations;
using TenantHub.Application.Notifications;
using TenantHub.Domain;
using TenantHub.Domain.Audit;
using TenantHub.Infrastructure.Background;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Audit;

/// <summary>
/// Turns the auditable events of tracked aggregates into audit entries just before they are saved, so the change
/// and its entry are written in the same transaction: both or neither (D012). Events are cleared once turned into
/// entries, so a second SaveChanges in the same request cannot record them twice.
/// </summary>
internal sealed class AuditWriter(AppDbContext db, ITenantContext tenant, TimeProvider time)
{
    private const string SystemActor = "System";
    private string? _actorName;

    public async Task AddEntriesAsync(CancellationToken cancellationToken)
    {
        var aggregates = Pending();
        if (aggregates.Count == 0)
        {
            return;
        }

        _actorName ??= await ActorNameAsync(cancellationToken);
        Add(aggregates);
    }

    public void AddEntries()
    {
        var aggregates = Pending();
        if (aggregates.Count == 0)
        {
            return;
        }

        _actorName ??= ActorNameQuery().FirstOrDefault() ?? SystemActor;
        Add(aggregates);
    }

    private List<AggregateRoot> Pending() =>
        [.. db.ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0)];

    private void Add(List<AggregateRoot> aggregates)
    {
        var now = time.GetUtcNow();
        var traceId = Activity.Current?.TraceId.ToString();
        foreach (var aggregate in aggregates)
        {
            foreach (var change in aggregate.DomainEvents.OfType<IAuditableEvent>())
            {
                // A portal session acts as its contact, an API key as itself (SPEC-V2 5.9), unless the event names its own actor.
                IAuditableEvent named = change.Actor is not null ? change
                    : tenant.ContactId is { } contact ? new ContactActorEvent(change, new AuditActor(ActorKind.Contact, null, contact, _actorName!))
                    : tenant.ApiKeyId is { } key ? new ContactActorEvent(change, new AuditActor(ActorKind.ApiKey, null, key, $"API key {tenant.ApiKeyName}"))
                    : change;
                db.AuditEntries.Add(AuditEntry.From(named, tenant.UserId, _actorName!, now, traceId));

                // Who should hear about it (SPEC-V2 5.7) travels in the same save: a rolled-back change notifies nobody.
                // A key's change is nobody's own, so even the key's creator hears about it.
                var actorUser = tenant.ApiKeyId is null ? tenant.UserId : null;
                if (!db.NotifyNothing && NotificationTriggers.For(change, actorUser, named.Actor?.Name ?? _actorName!) is { } request)
                {
                    db.OutboxMessages.Add(Outbox.Plain(NotificationRequest.FanOut, request, change.OrganizationId, now));
                }

                // Catalog events for webhooks (SPEC-V2 5.8), with what they carry as it is now; the id stays the event's
                // through every retry. The fan-out finds the subscribers, if any, outside this request.
                if (!db.NotifyNothing)
                {
                    foreach (var (type, data) in WebhookTriggers.For(change))
                    {
                        var webhookEvent = new WebhookEventMessage(Guid.CreateVersion7(now), type, now, data);
                        db.OutboxMessages.Add(Outbox.Plain(WebhookEventMessage.FanOut, webhookEvent, change.OrganizationId, now));
                    }
                }
            }

            aggregate.ClearDomainEvents();
        }
    }

    private async Task<string> ActorNameAsync(CancellationToken cancellationToken) =>
        await ActorNameQuery().FirstOrDefaultAsync(cancellationToken) ?? SystemActor;

    private IQueryable<string> ActorNameQuery() => tenant.ContactId is { } contact
        ? db.ClientContacts.AsNoTracking().Where(c => c.Id == contact).Select(c => c.Name)
        : db.Users.AsNoTracking().Where(u => u.Id == tenant.UserId).Select(u => u.DisplayName);

    /// <summary>The same change, with the portal contact as its actor.</summary>
    private sealed record ContactActorEvent(IAuditableEvent Inner, AuditActor ContactActor) : IAuditableEvent
    {
        public Guid OrganizationId => Inner.OrganizationId;

        public string Action => Inner.Action;

        public string EntityType => Inner.EntityType;

        public Guid EntityId => Inner.EntityId;

        public string Summary => Inner.Summary;

        public IReadOnlyList<FieldChange> Changes => Inner.Changes;

        public AuditActor? Actor => ContactActor;
    }
}
