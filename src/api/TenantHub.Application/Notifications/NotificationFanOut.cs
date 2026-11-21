using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;

namespace TenantHub.Application.Notifications;

/// <summary>
/// Delivers a <see cref="NotificationRequest"/> (SPEC-V2 5.7), as the system inside the change's organization:
/// <list type="bullet">
/// <item>the recipients are the named people and, when asked, the managers (whoever holds <c>jobs.manage</c>), all of them
/// active members, never the actor;</item>
/// <item>one notification each, and an email to those whose preference is on, written in one save;</item>
/// <item>a message delivered twice notifies nobody twice: its id is every notification's <c>SourceId</c>, checked first
/// and unique per person.</item>
/// </list>
/// </summary>
public sealed class NotificationFanOut(
    IAppDbContext db,
    TenantContext tenant,
    IOutbox outbox,
    IIdentityService identity,
    IAppLinks links,
    TimeProvider time)
{
    public async Task DeliverAsync(Guid sourceId, Guid organizationId, NotificationRequest request, CancellationToken cancellationToken)
    {
        tenant.EnterAsSystem(organizationId);
        if (await db.Notifications.AnyAsync(n => n.SourceId == sourceId, cancellationToken))
        {
            return;
        }

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.RemovedAt == null)
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(cancellationToken);
        var active = members.Select(m => m.UserId).ToHashSet();
        var recipients = request.Users
            .Concat(request.Managers ? members.Where(m => Permissions.For(m.Role).Contains(Permissions.JobsManage)).Select(m => m.UserId) : [])
            .Where(active.Contains)
            .Where(id => id != request.ActorUserId)
            .Distinct()
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => new { o.Name, o.Slug })
            .SingleAsync(cancellationToken);
        if (await ContentAsync(request, cancellationToken) is not { } content)
        {
            return; // the job, request or agreement is gone: nothing left to look at
        }

        await NotifyAsync(organizationId, organization.Name, organization.Slug, recipients, request.Type, content, sourceId, cancellationToken);
    }

    /// <summary>Writes the notifications and queues the emails; shared with the digest.</summary>
    internal async Task NotifyAsync(
        Guid organizationId,
        string organizationName,
        string slug,
        IReadOnlyCollection<Guid> recipients,
        NotificationType type,
        NotificationContent content,
        Guid sourceId,
        CancellationToken cancellationToken,
        Func<Guid, (NotificationContent Content, IReadOnlyList<string> Lines)?>? personal = null)
    {
        var now = time.GetUtcNow();
        var profiles = await identity.FindProfilesAsync(recipients, cancellationToken);
        var choices = await db.NotificationPreferences.AsNoTracking()
            .Where(p => recipients.Contains(p.UserId) && p.Type == type)
            .ToDictionaryAsync(p => p.UserId, p => p.Email, cancellationToken);
        foreach (var userId in recipients)
        {
            var (own, lines) = personal?.Invoke(userId) ?? (content, [content.Text]);
            db.Notifications.Add(Notification.Create(organizationId, userId, type, own, sourceId, now));
            if (profiles.TryGetValue(userId, out var profile) && choices.GetValueOrDefault(userId, NotificationTypes.EmailByDefault(type)))
            {
                var link = links.App(Path(slug, own));
                var email = lines.Count == 1
                    ? NotificationEmails.Build(profile.Email, profile.DisplayName, organizationName, own.Text, Action(own), link, links.App(PreferencesPath))
                    : NotificationEmails.Build(profile.Email, $"{organizationName}: {own.Text}", profile.DisplayName, organizationName, lines, Action(own), link, links.App(PreferencesPath));
                outbox.Add(NotificationEmails.Message, email);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public const string PreferencesPath = "account/notifications";

    /// <summary>Where a notification leads in the app, relative to its address.</summary>
    public static string Path(string slug, NotificationContent content) => content.Link switch
    {
        NotificationLink.Job => $"o/{slug}/jobs/{content.LinkId}",
        NotificationLink.Request => $"o/{slug}/requests",
        NotificationLink.Agreement => $"o/{slug}/agreements/{content.LinkId}",
        _ => $"o/{slug}/jobs?overdue=true",
    };

    private static string Action(NotificationContent content) => content.Link switch
    {
        NotificationLink.Job => "Open the job",
        NotificationLink.Request => "Open the requests",
        NotificationLink.Agreement => "Open the agreement",
        _ => "See the overdue jobs",
    };

    /// <summary>What the notification says, from the entity as it is now; null when it no longer exists.</summary>
    private async Task<NotificationContent?> ContentAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        var actor = request.ActorName;
        switch (request.Link)
        {
            case NotificationLink.Job:
                var job = await db.Jobs.AsNoTracking().Where(j => j.Id == request.EntityId)
                    .Select(j => new { j.Number, j.Title }).SingleOrDefaultAsync(cancellationToken);
                if (job is null)
                {
                    return null;
                }

                var named = $"{Job.FormatNumber(job.Number)} “{job.Title}”";
                var text = request.Type switch
                {
                    NotificationType.Assigned => $"{actor} assigned {named} to you.",
                    NotificationType.VisitChanged when request.Detail == "cancelled" => $"{actor} cancelled {named}.",
                    NotificationType.VisitChanged when request.Detail?.StartsWith("rescheduled:", StringComparison.Ordinal) == true =>
                        $"{actor} moved {named} to {Day(request.Detail["rescheduled:".Length..])}.",
                    NotificationType.VisitChanged => $"{actor} took {named} off the schedule.",
                    NotificationType.QuoteDecided => $"{actor} {request.Detail ?? "decided on"} the quote for {named}.",
                    _ => $"{actor} reported a problem with {named}.",
                };
                return new NotificationContent(text, NotificationLink.Job, request.EntityId);
            case NotificationLink.Request:
                var work = await db.WorkRequests.AsNoTracking().Where(r => r.Id == request.EntityId)
                    .Select(r => new { r.Title, Client = db.Clients.Where(c => c.Id == r.ClientId).Select(c => c.Name).Single() })
                    .SingleOrDefaultAsync(cancellationToken);
                return work is null ? null : new NotificationContent($"{actor} of {work.Client} asked for work: “{work.Title}”.", NotificationLink.Request, request.EntityId);
            default:
                var agreement = await db.Agreements.AsNoTracking().Where(a => a.Id == request.EntityId)
                    .Select(a => a.Title).SingleOrDefaultAsync(cancellationToken);
                return agreement is null
                    ? null
                    : new NotificationContent($"“{agreement}” stopped generating visits: the plan's open-jobs limit is reached.", NotificationLink.Agreement, request.EntityId);
        }
    }

    private static string Day(string isoDate) =>
        DateOnly.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("ddd d MMM", CultureInfo.InvariantCulture);
}
