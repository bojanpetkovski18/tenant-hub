namespace TenantHub.Domain.Organizations;

/// <summary>Subscription plan (SPEC 5.5); its limits are <see cref="PlanLimits"/>.</summary>
public enum Plan
{
    Free,
    Team,
    Business,
}
