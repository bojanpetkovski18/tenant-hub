using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Notifications;

/// <summary>
/// The daily digest (SPEC-V2 5.7), run by the scheduler for one organization at 07:00 in its time zone. Each assignee
/// with overdue jobs gets them listed; each manager gets the organization's count (and their own list, if they have
/// one). Nothing overdue, nothing sent. One run per local day: the run's source id is the organization and the date,
/// so a second scheduler, or a run repeated after a lost lease, finds the day done. The same run removes the
/// organization's notifications older than 90 days.
/// </summary>
public sealed class OverdueDigest(IAppDbContext db, TenantContext tenant, NotificationFanOut fanOut, TimeProvider time)
{
    private const int Listed = 10;

    public const string TaskName = "notifications.digest";

    /// <summary>Returns when the next run is due: tomorrow's 07:00 in the organization's zone.</summary>
    public async Task<DateTimeOffset> RunAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        tenant.EnterAsSystem(organizationId);
        var now = time.GetUtcNow();
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId, cancellationToken);
        var before = now - NotificationTypes.Retention;
        await db.Notifications.Where(n => n.CreatedAt < before).ExecuteDeleteAsync(cancellationToken);

        // The webhook delivery log goes after 30 days (SPEC-V2 5.8), in the same daily run of the organization.
        var deliveriesBefore = now - WebhookDelivery.Retention;
        await db.WebhookDeliveries.Where(d => d.CreatedAt < deliveriesBefore).ExecuteDeleteAsync(cancellationToken);

        var today = organization.TodayAt(now);
        var sourceId = SourceId(organizationId, today);
        if (!await db.Notifications.AnyAsync(n => n.SourceId == sourceId, cancellationToken))
        {
            await SendAsync(organization, today, sourceId, cancellationToken);
        }

        return DigestSchedule.Next(organization.TimeZone, now);
    }

    /// <summary>The same id for the same organization and day, whoever computes it.</summary>
    public static Guid SourceId(Guid organizationId, DateOnly day) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{TaskName}:{organizationId:N}:{day:yyyy-MM-dd}")).AsSpan(0, 16));

    private async Task SendAsync(Organization organization, DateOnly today, Guid sourceId, CancellationToken cancellationToken)
    {
        var overdue = await db.Jobs.AsNoTracking()
            .Where(j => j.DueOn < today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
            .OrderBy(j => j.DueOn).ThenBy(j => j.Number)
            .Select(j => new { j.Id, j.Number, j.Title, j.DueOn, j.AssigneeUserId })
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return;
        }

        var members = await db.Memberships.AsNoTracking().Where(m => m.RemovedAt == null)
            .Select(m => new { m.UserId, m.Role }).ToListAsync(cancellationToken);
        var managers = members.Where(m => Permissions.For(m.Role).Contains(Permissions.JobsManage)).Select(m => m.UserId).ToHashSet();
        var byAssignee = overdue.Where(j => j.AssigneeUserId is not null).ToLookup(j => j.AssigneeUserId!.Value);
        var recipients = members.Select(m => m.UserId).Where(u => managers.Contains(u) || byAssignee.Contains(u)).ToList();

        (NotificationContent, IReadOnlyList<string>)? Personal(Guid userId)
        {
            var own = byAssignee[userId].ToList();
            var parts = new List<string>();
            if (own.Count > 0)
            {
                parts.Add(own.Count == 1 ? "You have 1 overdue job." : $"You have {own.Count} overdue jobs.");
            }

            if (managers.Contains(userId))
            {
                // Not "in {name}.": the subject starts with the name already, and a name may end in a full stop ("Co.").
                parts.Add(overdue.Count == 1 ? "1 job is overdue in the organization." : $"{overdue.Count} jobs are overdue in the organization.");
            }

            var lines = new List<string> { string.Join(" ", parts) };
            lines.AddRange(own.Take(Listed).Select(j =>
                $"{Job.FormatNumber(j.Number)} “{j.Title}”, due {j.DueOn!.Value.ToString("ddd d MMM", CultureInfo.InvariantCulture)}"));
            if (own.Count > Listed)
            {
                lines.Add($"and {own.Count - Listed} more");
            }

            return (new NotificationContent(lines[0], NotificationLink.OverdueJobs, null), lines);
        }

        await fanOut.NotifyAsync(
            organization.Id,
            organization.Name,
            organization.Slug,
            recipients,
            NotificationType.OverdueDigest,
            new NotificationContent("Overdue jobs", NotificationLink.OverdueJobs, null),
            sourceId,
            cancellationToken,
            Personal);
    }
}
