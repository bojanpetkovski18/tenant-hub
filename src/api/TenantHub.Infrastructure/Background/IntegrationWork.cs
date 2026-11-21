using TenantHub.Application.Abstractions;
using TenantHub.Application.Integrations;

namespace TenantHub.Infrastructure.Background;

/// <summary>Queues one catalog event for each subscribed endpoint of its organization (SPEC-V2 5.8, D044).</summary>
internal sealed class WebhookFanOutHandler(WebhookFanOut fanOut) : IOutboxHandler
{
    public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken) =>
        fanOut.DeliverAsync(
            message.OrganizationId ?? throw new OutboxDeliveryException("organization-missing", permanent: true),
            message.Read<WebhookEventMessage>(),
            cancellationToken);
}

/// <summary>One attempt at one delivery; a failure goes back to the dispatcher, which retries on the webhook schedule.</summary>
internal sealed class WebhookDeliveryHandler(WebhookDeliverer deliverer) : IOutboxHandler
{
    public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken) =>
        deliverer.DeliverAsync(
            message.OrganizationId ?? throw new OutboxDeliveryException("organization-missing", permanent: true),
            message.Read<WebhookDeliveryMessage>(),
            message.Attempt,
            OutboxDispatcher.DelaysFor(message.Type),
            cancellationToken);
}
