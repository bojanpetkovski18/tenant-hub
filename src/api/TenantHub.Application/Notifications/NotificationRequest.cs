using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Notifications;

namespace TenantHub.Application.Notifications;

/// <summary>
/// A change that someone should hear about (SPEC-V2 5.7), as the outbox carries it from the change's transaction to the
/// fan-out. <see cref="Users"/> are the people named by the change (the assignee, the quote's sender); with
/// <see cref="Managers"/> the organization's managers are added. Who is told is decided at delivery, inside the tenant:
/// never the actor, never someone who is no longer a member.
/// </summary>
public sealed record NotificationRequest(
    NotificationType Type,
    NotificationLink Link,
    Guid EntityId,
    Guid? ActorUserId,
    string ActorName,
    IReadOnlyList<Guid> Users,
    bool Managers,
    string? Detail = null)
{
    public static readonly OutboxMessageType FanOut = new("notifications.fan-out", CarriesSecret: false);
}

/// <summary>
/// Which audited changes notify whom (SPEC-V2 5.7). Called for every audit entry, in the same save, so a rolled-back
/// command notifies nobody. Changes made by the system for an agreement (generated or following visits) notify
/// nobody: an edited agreement would otherwise send one message per future visit; its own hold is the exception.
/// </summary>
public static class NotificationTriggers
{
    public static NotificationRequest? For(IAuditableEvent change, Guid? actorUserId, string actorName) => change switch
    {
        JobEvent job when job.Actor is not { Kind: ActorKind.System } => ForJob(job, actorUserId, actorName),
        WorkRequestEvent { Action: "request.submitted" } request =>
            new(NotificationType.WorkRequested, NotificationLink.Request, request.Request.Id, actorUserId, actorName, [], Managers: true),
        AgreementEvent { Action: "agreement.generation_held" } held =>
            new(NotificationType.GenerationPaused, NotificationLink.Agreement, held.Agreement.Id, null, actorName, [], Managers: true),
        _ => null,
    };

    private static NotificationRequest? ForJob(JobEvent change, Guid? actorUserId, string actorName)
    {
        var job = change.Job;
        var assignee = job.AssigneeUserId is { } id ? new[] { id } : [];
        NotificationRequest Make(NotificationType type, IReadOnlyList<Guid> users, bool managers = false, string? detail = null) =>
            new(type, NotificationLink.Job, job.Id, actorUserId, actorName, users, managers, detail);
        bool Changed(string field) => change.Changes.Any(c => c.Field == field);

        return change.Action switch
        {
            "job.created" when assignee.Length > 0 => Make(NotificationType.Assigned, assignee),
            "job.updated" when Changed("assignee") && assignee.Length > 0 => Make(NotificationType.Assigned, assignee),
            "job.updated" when Changed("scheduledOn") && assignee.Length > 0 && job.Status.IsOpen() =>
                Make(NotificationType.VisitChanged, assignee, detail: job.ScheduledOn is { } day ? $"rescheduled:{day:yyyy-MM-dd}" : "unscheduled"),
            "job.status_changed" when job.Status == JobStatus.Cancelled && assignee.Length > 0 =>
                Make(NotificationType.VisitChanged, assignee, detail: "cancelled"),
            "job.quote_approved" or "job.quote_declined" =>
                Make(
                    NotificationType.QuoteDecided,
                    job.ApprovalSentByUserId is { } sender ? [sender] : [],
                    managers: true,
                    detail: change.Action == "job.quote_approved" ? "approved" : "declined"),
            "job.client_reported_problem" => Make(NotificationType.ProblemReported, assignee, managers: true),
            _ => null,
        };
    }
}
