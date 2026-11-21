namespace TenantHub.Domain.Notifications;

/// <summary>What a notification is about (SPEC-V2 5.7); the names are the API's and the preferences' stable identifiers.</summary>
public enum NotificationType
{
    /// <summary>A job was assigned to you.</summary>
    Assigned,

    /// <summary>Your visit was rescheduled or cancelled.</summary>
    VisitChanged,

    /// <summary>A quote was approved or declined (managers, and whoever sent it).</summary>
    QuoteDecided,

    /// <summary>A client asked for work through the portal (managers).</summary>
    WorkRequested,

    /// <summary>A client reported a problem with a completed visit (managers and the assignee).</summary>
    ProblemReported,

    /// <summary>An agreement stopped generating visits at the plan's limit (Owners and Admins).</summary>
    GenerationPaused,

    /// <summary>The daily digest of overdue jobs (each assignee, and managers a count).</summary>
    OverdueDigest,
}

/// <summary>The types in the order screens list them, with whether email is on until a person chooses (SPEC-V2 5.7).</summary>
public static class NotificationTypes
{
    public static readonly IReadOnlyList<NotificationType> All = Enum.GetValues<NotificationType>();

    /// <summary>How long notifications are kept (SPEC-V2 5.7).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public static bool EmailByDefault(NotificationType type) => type != NotificationType.VisitChanged;

    public static string Label(NotificationType type) => type switch
    {
        NotificationType.Assigned => "A job is assigned to me",
        NotificationType.VisitChanged => "My visit is rescheduled or cancelled",
        NotificationType.QuoteDecided => "A client approves or declines a quote",
        NotificationType.WorkRequested => "A client asks for work",
        NotificationType.ProblemReported => "A client reports a problem with a visit",
        NotificationType.GenerationPaused => "An agreement stops at the plan's limit",
        _ => "Daily digest of overdue jobs",
    };
}

/// <summary>Where a notification leads: a job, a work request, an agreement, or the overdue jobs list.</summary>
public enum NotificationLink
{
    Job,
    Request,
    Agreement,
    OverdueJobs,
}

/// <summary>What a notification says and where it leads; written once, with the names as they were then.</summary>
public sealed record NotificationContent(string Text, NotificationLink Link, Guid? LinkId);

/// <summary>
/// One person's notification in one organization (SPEC-V2 5.7). <see cref="SourceId"/> is the outbox message (or digest
/// run) that produced it: a message delivered twice cannot notify anyone twice.
/// </summary>
public sealed class Notification : ITenantOwned
{
    public const int TextMaxLength = 500;

    private Notification()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    public NotificationContent Content { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public Guid SourceId { get; private set; }

    public static Notification Create(Guid organizationId, Guid userId, NotificationType type, NotificationContent content, Guid sourceId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        OrganizationId = organizationId,
        UserId = userId,
        Type = type,
        Content = content with { Text = content.Text.Length > TextMaxLength ? content.Text[..(TextMaxLength - 1)] + "…" : content.Text },
        CreatedAt = now,
        SourceId = sourceId,
    };

    public bool MarkRead(DateTimeOffset now)
    {
        if (ReadAt is not null)
        {
            return false;
        }

        ReadAt = now;
        return true;
    }
}

/// <summary>A person's choice to get one type by email too; without a row, <see cref="NotificationTypes.EmailByDefault"/> applies.</summary>
public sealed class NotificationPreference
{
    private NotificationPreference()
    {
    }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    public bool Email { get; private set; }

    public static NotificationPreference For(Guid userId, NotificationType type, bool email) => new() { UserId = userId, Type = type, Email = email };

    public void Set(bool email) => Email = email;
}

/// <summary>When the daily digest runs: 07:00 in the organization's time zone (SPEC-V2 5.7).</summary>
public static class DigestSchedule
{
    public static readonly TimeOnly At = new(7, 0);

    /// <summary>The next 07:00 local after <paramref name="now"/>, as an instant; daylight-saving days included.</summary>
    public static DateTimeOffset Next(string timeZone, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        var candidate = At7(zone, day);
        return candidate > now ? candidate : At7(zone, day.AddDays(1));
    }

    private static DateTimeOffset At7(TimeZoneInfo zone, DateOnly day)
    {
        var local = day.ToDateTime(At, DateTimeKind.Unspecified);
        // 07:00 falls in a daylight-saving gap in no zone in use today; the loop keeps that an assumption, not a crash.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
