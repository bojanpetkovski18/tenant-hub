namespace TenantHub.Domain.Organizations;

/// <summary>What a plan bounds (SPEC 5.5). The names are the API's stable identifiers for each limit.</summary>
public enum LimitKind
{
    Seats,
    ActiveClients,
    OpenJobs,
}

/// <summary>A plan's limits; null means unlimited.</summary>
public sealed record PlanLimits(int? Seats, int? ActiveClients, int? OpenJobs)
{
    public static PlanLimits For(Plan plan) => plan switch
    {
        Plan.Free => new(Seats: 3, ActiveClients: 25, OpenJobs: 50),
        Plan.Team => new(Seats: 10, ActiveClients: 500, OpenJobs: 2_000),
        Plan.Business => new(Seats: 50, ActiveClients: null, OpenJobs: null),
        _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown plan."),
    };

    public int? Of(LimitKind kind) => kind switch
    {
        LimitKind.Seats => Seats,
        LimitKind.ActiveClients => ActiveClients,
        LimitKind.OpenJobs => OpenJobs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>
/// What an organization uses (SPEC 5.5): seats are active members plus pending invitations; archived clients and
/// closed jobs do not count.
/// </summary>
public sealed record PlanUsage(int Seats, int ActiveClients, int OpenJobs)
{
    public int Of(LimitKind kind) => kind switch
    {
        LimitKind.Seats => Seats,
        LimitKind.ActiveClients => ActiveClients,
        LimitKind.OpenJobs => OpenJobs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>One limit that is reached or exceeded: which, its value on the plan, and the usage.</summary>
public sealed record LimitBreach(LimitKind Limit, int Allowed, int Usage);

/// <summary>
/// Whether a change fits the plan (D013). The callers count usage under the organization-row lock and ask before
/// writing, so two concurrent requests cannot both take the last unit.
/// </summary>
public static class PlanPolicy
{
    public static readonly IReadOnlyList<LimitKind> Kinds = [LimitKind.Seats, LimitKind.ActiveClients, LimitKind.OpenJobs];

    /// <summary>Refuses (403 <c>plan-limit-reached</c>) when adding <paramref name="adding"/> would exceed the limit.</summary>
    public static void EnsureRoomFor(Plan plan, PlanUsage usage, LimitKind kind, int adding = 1)
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

    public static string Describe(LimitKind kind, int count) => (kind, count) switch
    {
        (LimitKind.Seats, 1) => "1 seat",
        (LimitKind.Seats, _) => $"{count} seats",
        (LimitKind.ActiveClients, 1) => "1 active client",
        (LimitKind.ActiveClients, _) => $"{count} active clients",
        (LimitKind.OpenJobs, 1) => "1 open job",
        _ => $"{count} open jobs",
    };
}

/// <summary>
/// A limit is reached (SPEC 5.5, D013): 403 with code <c>plan-limit-reached</c> and the limit, its value and the usage,
/// so every screen can show the same message.
/// </summary>
public sealed class PlanLimitReachedException(Plan plan, LimitBreach breach)
    : DomainException(
        "plan-limit-reached",
        $"Your {plan} plan allows {PlanPolicy.Describe(breach.Limit, breach.Allowed)}, and {breach.Usage} {(breach.Usage == 1 ? "is" : "are")} in use.",
        DomainErrorKind.Forbidden)
{
    public Plan Plan { get; } = plan;

    public LimitBreach Breach { get; } = breach;
}

/// <summary>A downgrade does not fit (409 <c>plan-downgrade-blocked</c>); lists every limit in the way.</summary>
public sealed class PlanDowngradeBlockedException(Plan target, IReadOnlyList<LimitBreach> blocking)
    : DomainException(
        "plan-downgrade-blocked",
        $"Your organization uses more than the {target} plan allows: {string.Join(", ", blocking.Select(b => $"{PlanPolicy.Describe(b.Limit, b.Usage)} (limit {b.Allowed})"))}.")
{
    public Plan Target { get; } = target;

    public IReadOnlyList<LimitBreach> Blocking { get; } = blocking;
}
