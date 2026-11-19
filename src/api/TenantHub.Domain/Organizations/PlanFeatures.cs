namespace TenantHub.Domain.Organizations;

/// <summary>What a plan includes beyond its limits (SPEC-V2 4.8). The names are the API's stable identifiers.</summary>
public enum PlanFeature
{
    /// <summary>Client contacts and the client portal (M18).</summary>
    Portal,

    /// <summary>API keys and webhooks (M21).</summary>
    Integrations,
}

/// <summary>Which plan has which feature (D042). Checked in the domain, like the limits (D013).</summary>
public static class PlanFeatures
{
    public static readonly IReadOnlyList<PlanFeature> All = [PlanFeature.Portal, PlanFeature.Integrations];

    public static IReadOnlyList<PlanFeature> For(Plan plan) => plan switch
    {
        Plan.Free => [],
        Plan.Team => [PlanFeature.Portal],
        Plan.Business => [PlanFeature.Portal, PlanFeature.Integrations],
        _ => throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown plan."),
    };

    public static bool Includes(Plan plan, PlanFeature feature) => For(plan).Contains(feature);

    /// <summary>The cheapest plan with the feature, for "part of the Team plan" messages.</summary>
    public static Plan CheapestWith(PlanFeature feature) => Enum.GetValues<Plan>().First(plan => Includes(plan, feature));

    /// <summary>Refuses (403 <c>plan-feature-unavailable</c>) when the plan lacks the feature.</summary>
    public static void Ensure(Plan plan, PlanFeature feature)
    {
        if (!Includes(plan, feature))
        {
            throw new PlanFeatureUnavailableException(feature);
        }
    }

    public static string Describe(PlanFeature feature) => feature switch
    {
        PlanFeature.Portal => "the client portal",
        PlanFeature.Integrations => "API keys and webhooks",
        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, null),
    };
}

/// <summary>The plan lacks a feature (403 <c>plan-feature-unavailable</c>), with the cheapest plan that has it.</summary>
public sealed class PlanFeatureUnavailableException(PlanFeature feature)
    : DomainException(
        "plan-feature-unavailable",
        $"{Capitalize(PlanFeatures.Describe(feature))} is part of the {PlanFeatures.CheapestWith(feature)} plan. An Owner can change the plan in Settings.",
        DomainErrorKind.Forbidden)
{
    public PlanFeature Feature { get; } = feature;

    public Plan Cheapest { get; } = PlanFeatures.CheapestWith(feature);

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// A downgrade would turn off features in use (409 <c>plan-feature-loss</c>): the Owner repeats it with
/// <c>confirmFeatureLoss</c>. The data stays and comes back with an upgrade (SPEC-V2 4.8).
/// </summary>
public sealed class PlanFeatureLossException(Plan target, IReadOnlyList<PlanFeature> features)
    : DomainException(
        "plan-feature-loss",
        $"The {target} plan does not include {string.Join(" or ", features.Select(PlanFeatures.Describe))}, which your organization uses. " +
        "Confirm to turn it off; its data is kept and comes back if you change the plan again.")
{
    public Plan Target { get; } = target;

    public IReadOnlyList<PlanFeature> Features { get; } = features;
}
