using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Organizations;

/// <summary>SPEC 5.5 as tables: every limit at, below and above its boundary, upgrades and downgrades.</summary>
public sealed class PlanPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_limits_are_the_spec()
    {
        Assert.Equal(new PlanLimits(3, 25, 50), PlanLimits.For(Plan.Free));
        Assert.Equal(new PlanLimits(10, 500, 2_000), PlanLimits.For(Plan.Team));
        Assert.Equal(new PlanLimits(50, null, null), PlanLimits.For(Plan.Business));
    }

    public static TheoryData<Plan, LimitKind, int, bool> Boundaries() => new()
    {
        // plan, limit, usage before adding one, allowed?
        { Plan.Free, LimitKind.Seats, 2, true },
        { Plan.Free, LimitKind.Seats, 3, false },
        { Plan.Free, LimitKind.ActiveClients, 24, true },
        { Plan.Free, LimitKind.ActiveClients, 25, false },
        { Plan.Free, LimitKind.OpenJobs, 49, true },
        { Plan.Free, LimitKind.OpenJobs, 50, false },
        { Plan.Team, LimitKind.Seats, 9, true },
        { Plan.Team, LimitKind.Seats, 10, false },
        { Plan.Team, LimitKind.ActiveClients, 499, true },
        { Plan.Team, LimitKind.ActiveClients, 500, false },
        { Plan.Team, LimitKind.OpenJobs, 1_999, true },
        { Plan.Team, LimitKind.OpenJobs, 2_000, false },
        { Plan.Business, LimitKind.Seats, 49, true },
        { Plan.Business, LimitKind.Seats, 50, false },
        { Plan.Business, LimitKind.ActiveClients, 1_000_000, true },
        { Plan.Business, LimitKind.OpenJobs, 1_000_000, true },
    };

    [Theory]
    [MemberData(nameof(Boundaries))]
    public void Adding_one_fits_only_below_the_limit(Plan plan, LimitKind kind, int used, bool allowed)
    {
        var usage = kind switch
        {
            LimitKind.Seats => new PlanUsage(used, 0, 0),
            LimitKind.ActiveClients => new PlanUsage(1, used, 0),
            _ => new PlanUsage(1, 0, used),
        };

        var error = Record.Exception(() => PlanPolicy.EnsureRoomFor(plan, usage, kind));

        if (allowed)
        {
            Assert.Null(error);
        }
        else
        {
            var reached = Assert.IsType<PlanLimitReachedException>(error);
            Assert.Equal(("plan-limit-reached", DomainErrorKind.Forbidden), (reached.Code, reached.Kind));
            Assert.Equal(new LimitBreach(kind, PlanLimits.For(plan).Of(kind)!.Value, used), reached.Breach);
        }
    }

    [Fact]
    public void The_message_names_the_plan_the_limit_and_the_usage()
    {
        var error = Assert.Throws<PlanLimitReachedException>(() => PlanPolicy.EnsureRoomFor(Plan.Free, new PlanUsage(3, 0, 0), LimitKind.Seats));

        Assert.Equal("Your Free plan allows 3 seats, and 3 are in use.", error.Message);
    }

    [Fact]
    public void A_downgrade_is_blocked_by_every_limit_the_usage_exceeds_and_only_those()
    {
        var usage = new PlanUsage(Seats: 4, ActiveClients: 25, OpenJobs: 51);

        var blocking = PlanPolicy.BlockingLimits(Plan.Free, usage);

        // 25 clients fit exactly; seats and open jobs do not.
        Assert.Equal([new LimitBreach(LimitKind.Seats, 3, 4), new LimitBreach(LimitKind.OpenJobs, 50, 51)], blocking);
        Assert.Empty(PlanPolicy.BlockingLimits(Plan.Free, new PlanUsage(3, 25, 50)));
        Assert.Empty(PlanPolicy.BlockingLimits(Plan.Business, new PlanUsage(50, 10_000, 10_000)));
    }

    [Fact]
    public void Changing_plan_records_the_change_and_refuses_a_downgrade_that_does_not_fit()
    {
        var organization = Organization.Create("Harbor & Pine", "harbor-pine", "UTC", Guid.NewGuid(), Now);

        Assert.False(organization.ChangePlan(Plan.Free, new PlanUsage(1, 0, 0)));
        Assert.True(organization.ChangePlan(Plan.Team, new PlanUsage(5, 30, 60)));
        var refused = Assert.Throws<PlanDowngradeBlockedException>(() => organization.ChangePlan(Plan.Free, new PlanUsage(5, 30, 60)));

        Assert.Equal(Plan.Team, organization.Plan);
        Assert.Equal("plan-downgrade-blocked", refused.Code);
        Assert.Equal([LimitKind.Seats, LimitKind.ActiveClients, LimitKind.OpenJobs], refused.Blocking.Select(b => b.Limit));
        Assert.Equal(
            "Your organization uses more than the Free plan allows: 5 seats (limit 3), 30 active clients (limit 25), 60 open jobs (limit 50).",
            refused.Message);
        var recorded = (OrganizationEvent)Assert.Single(organization.DomainEvents);
        Assert.Equal(("organization.plan_changed", "Changed the plan from Free to Team"), (recorded.Action, recorded.Summary));
        Assert.Equal((organization.Id, "organization", organization.Id), (recorded.OrganizationId, recorded.EntityType, recorded.EntityId));
    }

    [Fact]
    public void Settings_change_together_and_report_every_invalid_field()
    {
        var organization = Organization.Create("Harbor & Pine", "harbor-pine", "UTC", Guid.NewGuid(), Now);

        var invalid = Assert.Throws<DomainValidationException>(() => organization.UpdateSettings(" x ", "Mars/Olympus_Mons"));
        Assert.False(organization.UpdateSettings("  Harbor   &   Pine ", "UTC"));
        Assert.True(organization.UpdateSettings("Harbor & Pine Landscaping", "America/New_York"));

        Assert.Equal(["name", "timeZone"], invalid.Errors.Keys);
        Assert.Equal(("Harbor & Pine Landscaping", "America/New_York", "harbor-pine"), (organization.Name, organization.TimeZone, organization.Slug));
        var recorded = (OrganizationEvent)Assert.Single(organization.DomainEvents);
        Assert.Equal("Updated the organization: renamed it to “Harbor & Pine Landscaping” and set the time zone to America/New_York", recorded.Summary);
        Assert.Equal([new FieldChange("name", "Harbor & Pine", "Harbor & Pine Landscaping"), new FieldChange("timeZone", "UTC", "America/New_York")], recorded.Changes);
    }
}
