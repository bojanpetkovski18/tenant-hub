namespace TenantHub.Domain.Organizations;

/// <summary>What a plan bounds (SPEC 5.5). The names are the API's stable identifiers for each limit.</summary>
public enum LimitKind
{
    Seats,
    ActiveClients,
    OpenJobs,

    /// <summary>Agreements that are active (not paused or ended), SPEC-V2 4.8.</summary>
    ActiveAgreements,

    /// <summary>Bytes of stored photos after re-encoding, thumbnails included (SPEC-V2 4.8, 5.6).</summary>
    Storage,
}

/// <summary>A plan's limits; null means unlimited. Storage is in bytes (binary units: 1 MB is 1,048,576 bytes).</summary>
public sealed record PlanLimits(int? Seats, int? ActiveClients, int? OpenJobs, int? ActiveAgreements, long? StorageBytes)
{
    public const long Megabyte = 1024L * 1024;

    public const long Gigabyte = 1024L * Megabyte;

    public static PlanLimits For(Plan plan) => plan switch
    {
        Plan.Free => new(Seats: 3, ActiveClients: 25, OpenJobs: 50, ActiveAgreements: 3, StorageBytes: 100 * Megabyte),
        Plan.Team => new(Seats: 10, ActiveClients: 500, OpenJobs: 2_000, ActiveAgreements: 200, StorageBytes: 5 * Gigabyte),
        Plan.Business => new(Seats: 50, ActiveClients: null, OpenJobs: null, ActiveAgreements: null, StorageBytes: 50 * Gigabyte),
        _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown plan."),
    };

    public long? Of(LimitKind kind) => kind switch
    {
        LimitKind.Seats => Seats,
        LimitKind.ActiveClients => ActiveClients,
        LimitKind.OpenJobs => OpenJobs,
        LimitKind.ActiveAgreements => ActiveAgreements,
        LimitKind.Storage => StorageBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// What an organization uses (SPEC 5.5): seats are active members plus pending invitations; archived clients and
/// closed jobs do not count.
/// </summary>
public sealed record PlanUsage(int Seats, int ActiveClients, int OpenJobs, int ActiveAgreements, long StorageBytes = 0)
{
    public long Of(LimitKind kind) => kind switch
    {
        LimitKind.Seats => Seats,
        LimitKind.ActiveClients => ActiveClients,
        LimitKind.OpenJobs => OpenJobs,
        LimitKind.ActiveAgreements => ActiveAgreements,
        LimitKind.Storage => StorageBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>One limit that is reached or exceeded: which, its value on the plan, and the usage.</summary>
public sealed record LimitBreach(LimitKind Limit, long Allowed, long Usage);

/// <summary>
/// Whether a change fits the plan (D013). The callers count usage under the organization-row lock and ask before
/// writing, so two concurrent requests cannot both take the last unit.
/// </summary>
public static class PlanPolicy
{
    public static readonly IReadOnlyList<LimitKind> Kinds = [LimitKind.Seats, LimitKind.ActiveClients, LimitKind.OpenJobs, LimitKind.ActiveAgreements, LimitKind.Storage];

    /// <summary>Refuses (403 <c>plan-limit-reached</c>) when adding <paramref name="adding"/> would exceed the limit.</summary>
    public static void EnsureRoomFor(Plan plan, PlanUsage usage, LimitKind kind, long adding = 1)
    {
        if (PlanLimits.For(plan).Of(kind) is { } allowed && usage.Of(kind) + adding > allowed)
        {
            throw new PlanLimitReachedException(plan, new LimitBreach(kind, allowed, usage.Of(kind)));
        }
    }

    /// <summary>The limits the current usage exceeds on <paramref name="target"/> (what blocks a downgrade).</summary>
    public static IReadOnlyList<LimitBreach> BlockingLimits(Plan target, PlanUsage usage)
    {
        var limits = PlanLimits.For(target);
        return
        [
            .. Kinds
                .Where(kind => limits.Of(kind) is { } allowed && usage.Of(kind) > allowed)
                .Select(kind => new LimitBreach(kind, limits.Of(kind)!.Value, usage.Of(kind))),
        ];
    }

    public static string Describe(LimitKind kind, long count) => (kind, count) switch
    {
        (LimitKind.Storage, _) => $"{Bytes(count)} of photo storage",
        (LimitKind.Seats, 1) => "1 seat",
        (LimitKind.Seats, _) => $"{count} seats",
        (LimitKind.ActiveClients, 1) => "1 active client",
        (LimitKind.ActiveClients, _) => $"{count} active clients",
        (LimitKind.OpenJobs, 1) => "1 open job",
        (LimitKind.OpenJobs, _) => $"{count} open jobs",
        (LimitKind.ActiveAgreements, 1) => "1 active agreement",
        _ => $"{count} active agreements",
    };

    /// <summary>Bytes as people read them: "100 MB", "4.2 GB", "512 KB" (binary units, at most one decimal).</summary>
    public static string Bytes(long bytes)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= PlanLimits.Gigabyte => $"{Math.Round(bytes / (double)PlanLimits.Gigabyte, 1).ToString("0.#", culture)} GB",
            >= PlanLimits.Megabyte => $"{Math.Round(bytes / (double)PlanLimits.Megabyte, 1).ToString("0.#", culture)} MB",
            >= 1024 => $"{Math.Round(bytes / 1024.0, 1).ToString("0.#", culture)} KB",
            1 => "1 byte",
            _ => $"{bytes} bytes",
        };
    }

    /// <summary>What a reached limit says: the plan's allowance and what is in use.</summary>
    internal static string Reached(Plan plan, LimitBreach breach) => breach.Limit == LimitKind.Storage
        ? $"Your {plan} plan allows {Describe(LimitKind.Storage, breach.Allowed)}, and {Bytes(breach.Usage)} is in use."
        : $"Your {plan} plan allows {Describe(breach.Limit, breach.Allowed)}, and {breach.Usage} {(breach.Usage == 1 ? "is" : "are")} in use.";
}

/// <summary>
/// A limit is reached (SPEC 5.5, D013): 403 with code <c>plan-limit-reached</c> and the limit, its value and the usage,
/// so every screen can show the same message.
/// </summary>
public sealed class PlanLimitReachedException(Plan plan, LimitBreach breach)
    : DomainException("plan-limit-reached", PlanPolicy.Reached(plan, breach), DomainErrorKind.Forbidden)
{
    public Plan Plan { get; } = plan;

    public LimitBreach Breach { get; } = breach;
}

/// <summary>A downgrade does not fit (409 <c>plan-downgrade-blocked</c>); lists every limit in the way.</summary>
public sealed class PlanDowngradeBlockedException(Plan target, IReadOnlyList<LimitBreach> blocking)
    : DomainException(
        "plan-downgrade-blocked",
        $"Your organization uses more than the {target} plan allows: {string.Join(", ", blocking.Select(b => $"{PlanPolicy.Describe(b.Limit, b.Usage)} (limit {(b.Limit == LimitKind.Storage ? PlanPolicy.Bytes(b.Allowed) : b.Allowed)})"))}.")
{
    public Plan Target { get; } = target;

    public IReadOnlyList<LimitBreach> Blocking { get; } = blocking;
}
