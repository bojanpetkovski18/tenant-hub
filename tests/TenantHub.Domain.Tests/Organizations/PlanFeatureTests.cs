using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Organizations;

/// <summary>SPEC-V2 4.8 as a table: which plan has which feature, and downgrades that would turn one off (D042).</summary>
public sealed class PlanFeatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly PlanUsage Small = new(1, 1, 1, 0);

    [Fact]
    public void The_features_are_the_spec()
    {
        Assert.Empty(PlanFeatures.For(Plan.Free));
        Assert.Equal([PlanFeature.Portal], PlanFeatures.For(Plan.Team));
        Assert.Equal([PlanFeature.Portal, PlanFeature.Integrations], PlanFeatures.For(Plan.Business));
        Assert.Equal((Plan.Team, Plan.Business), (PlanFeatures.CheapestWith(PlanFeature.Portal), PlanFeatures.CheapestWith(PlanFeature.Integrations)));
    }

    [Fact]
    public void A_missing_feature_is_refused_with_the_cheapest_plan_that_has_it()
    {
        var refused = Assert.Throws<PlanFeatureUnavailableException>(() => PlanFeatures.Ensure(Plan.Free, PlanFeature.Portal));
        PlanFeatures.Ensure(Plan.Team, PlanFeature.Portal);

        Assert.Equal(("plan-feature-unavailable", DomainErrorKind.Forbidden, Plan.Team), (refused.Code, refused.Kind, refused.Cheapest));
        Assert.Equal("The client portal is part of the Team plan. An Owner can change the plan in Settings.", refused.Message);
    }

    [Fact]
    public void A_downgrade_that_turns_off_a_feature_in_use_needs_confirmation_and_says_so()
    {
        var organization = Organization.Create("Harbor & Pine", "harbor-pine", "UTC", Guid.NewGuid(), Now, Plan.Team);
        organization.ClearDomainEvents();

        var refused = Assert.Throws<PlanFeatureLossException>(() => organization.ChangePlan(Plan.Free, Small, [PlanFeature.Portal], confirmFeatureLoss: false));
        Assert.Equal(Plan.Team, organization.Plan);
        Assert.Empty(organization.DomainEvents);

        Assert.True(organization.ChangePlan(Plan.Free, Small, [PlanFeature.Portal], confirmFeatureLoss: true));

        Assert.Equal("plan-feature-loss", refused.Code);
        Assert.Equal([PlanFeature.Portal], refused.Features);
        var recorded = (OrganizationEvent)Assert.Single(organization.DomainEvents);
        Assert.Equal("Changed the plan from Team to Free; turned off the client portal", recorded.Summary);
    }

    [Fact]
    public void Features_not_in_use_or_kept_by_the_target_need_no_confirmation()
    {
        var team = Organization.Create("Harbor & Pine", "harbor-pine", "UTC", Guid.NewGuid(), Now, Plan.Team);
        var business = Organization.Create("Brightline", "brightline", "UTC", Guid.NewGuid(), Now, Plan.Business);

        Assert.True(team.ChangePlan(Plan.Free, Small, [], confirmFeatureLoss: false));
        Assert.True(business.ChangePlan(Plan.Team, Small, [PlanFeature.Portal], confirmFeatureLoss: false));
    }
}
