using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Organizations;

/// <summary>
/// The tenant (SPEC 8.2). Everything tenant-owned belongs to exactly one organization. The slug is fixed at
/// creation; the name can change later (M08).
/// </summary>
public sealed class Organization
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 80;

    /// <summary>How many organizations one user may own (SPEC 5.1); limits abuse of open sign-up.</summary>
    public const int MaxOwnedPerUser = 5;

    private Organization()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = "";

    public string Slug { get; private set; } = "";

    public Plan Plan { get; private set; }

    /// <summary>IANA time-zone name; "today", "overdue" and "this month" are computed in it.</summary>
    public string TimeZone { get; private set; } = "";

    /// <summary>The next per-organization job number (SPEC 5.4).</summary>
    public int NextJobNumber { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>Created by the demo seed (M09 resets these and deletes the others after 24 hours).</summary>
    public bool IsDemoSeed { get; private set; }

    public static Organization Create(
        string name,
        string slug,
        string timeZone,
        Guid createdByUserId,
        DateTimeOffset now,
        Plan plan = Plan.Free,
        bool isDemoSeed = false)
    {
        if (!OrganizationSlug.IsValid(slug))
        {
            throw new ArgumentException($"'{slug}' is not a valid slug; use OrganizationSlug.Candidates.", nameof(slug));
        }

        return new Organization
        {
            Id = Guid.CreateVersion7(now),
            Name = NormalizeName(name),
            Slug = slug,
            Plan = plan,
            TimeZone = ValidateTimeZone(timeZone),
            NextJobNumber = 1,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
            IsDemoSeed = isDemoSeed,
        };
    }

    /// <summary>The creator's Owner membership, created together with the organization.</summary>
    public Membership AddOwner(Guid userId, DateTimeOffset now) => Membership.Create(Id, userId, Role.Owner, now, invitedByUserId: null);

    /// <summary>Seeding and, from M05, invitations: a member with any role.</summary>
    public Membership AddMember(Guid userId, Role role, DateTimeOffset now, Guid? invitedByUserId) =>
        Membership.Create(Id, userId, role, now, invitedByUserId);

    /// <summary>
    /// Hands out the next job number (SPEC 5.4). Callers hold the organization-row lock in the same transaction
    /// (D028), so numbers are sequential, unique and never reused even when jobs are created in parallel.
    /// </summary>
    public int TakeJobNumber() => NextJobNumber++;

    /// <summary>"Today" in the organization's time zone: what overdue, "this month" and due dates are judged against.</summary>
    public DateOnly TodayAt(DateTimeOffset now) => TodayIn(TimeZone, now);

    public static DateOnly TodayIn(string timeZone, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZone)).DateTime);

    /// <summary>
    /// The instant a local day starts in the organization's time zone, for date-range filters ("entries on 22
    /// October" means 22 October where the organization is). A day that starts inside a daylight-saving gap starts at
    /// the first valid local time after midnight.
    /// </summary>
    public static DateTimeOffset StartOfDayIn(string timeZone, DateOnly day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static string NormalizeName(string name)
    {
        // Collapse runs of whitespace so "Acme   Ltd " and "Acme Ltd" are the same name.
        var normalized = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < NameMinLength or > NameMaxLength)
        {
            throw new DomainException(
                "invalid-organization-name",
                $"The organization name must be {NameMinLength}–{NameMaxLength} characters.",
                DomainErrorKind.Validation);
        }

        return normalized;
    }

    public static bool IsValidTimeZone(string timeZone) =>
        TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _)
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _);

    private static string ValidateTimeZone(string timeZone) =>
        IsValidTimeZone(timeZone)
            ? timeZone
            : throw new DomainException("invalid-time-zone", "Choose a time zone from the list.", DomainErrorKind.Validation);
}
