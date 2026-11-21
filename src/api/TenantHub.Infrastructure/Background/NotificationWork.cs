using TenantHub.Application.Abstractions;
using TenantHub.Application.Notifications;

namespace TenantHub.Infrastructure.Background;

/// <summary>Delivers a notification request from the outbox, inside its organization (SPEC-V2 5.7, D039).</summary>
internal sealed class NotificationFanOutHandler(NotificationFanOut fanOut) : IOutboxHandler
{
    public Task HandleAsync(OutboxEnvelope message, CancellationToken cancellationToken) =>
        fanOut.DeliverAsync(
            message.Id,
            message.OrganizationId ?? throw new OutboxDeliveryException("organization-missing", permanent: true),
            message.Read<NotificationRequest>(),
            cancellationToken);
}

/// <summary>The daily digest of one organization, at 07:00 in its time zone (SPEC-V2 5.7).</summary>
internal sealed class OverdueDigestTask(OverdueDigest digest) : IScheduledTask
{
    public Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken) =>
        digest.RunAsync(run.OrganizationId ?? throw new InvalidOperationException("The digest runs for one organization."), cancellationToken);
}
