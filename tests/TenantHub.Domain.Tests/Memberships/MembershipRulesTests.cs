using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Domain.Tests.Memberships;

/// <summary>SPEC 5.2 and 5.3 as tables: every combination of actor, target and role that matters.</summary>
public sealed class MembershipRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.Parse("0199f0a0-0000-7000-8000-00000000000a");
    private static readonly Guid TargetId = Guid.Parse("0199f0a0-0000-7000-8000-00000000000b");

    private static Membership MemberWith(Role role, Guid? userId = null)
    {
        var organization = Organization.Create("Acme", "acme", "UTC", ActorId, Now);
        return organization.AddMember(userId ?? TargetId, role, Now, invitedByUserId: null);
    }

    private static string? Outcome(Action rule)
    {
        try
        {
            rule();
            return null;
        }
        catch (DomainException error)
        {
            return error.Code;
        }
    }

    public static TheoryData<Role, Role, string?> Invitations => new()
    {
        // actor role, invited role, refusal code (null = allowed)
        { Role.Owner, Role.Admin, null },
        { Role.Owner, Role.Member, null },
        { Role.Owner, Role.Viewer, null },
        { Role.Owner, Role.Owner, "owner-not-invitable" },
        { Role.Admin, Role.Admin, null },
        { Role.Admin, Role.Member, null },
        { Role.Admin, Role.Viewer, null },
        { Role.Admin, Role.Owner, "owner-not-invitable" },
    };

    [Theory]
    [MemberData(nameof(Invitations))]
    public void Who_may_invite_with_which_role(Role actorRole, Role role, string? refusal) =>
        Assert.Equal(refusal, Outcome(() => MembershipRules.EnsureCanInvite(new Actor(ActorId, actorRole), role)));

    public static TheoryData<Role, Role, Role, int, string?> RoleChanges => new()
    {
        // actor role, target's current role, new role, active owners, refusal code
        { Role.Owner, Role.Member, Role.Admin, 1, null },
        { Role.Owner, Role.Member, Role.Owner, 1, null },
        { Role.Owner, Role.Owner, Role.Admin, 2, null },
        { Role.Owner, Role.Owner, Role.Admin, 1, "last-owner" },
        { Role.Admin, Role.Member, Role.Viewer, 1, null },
        { Role.Admin, Role.Viewer, Role.Admin, 1, null },
        { Role.Admin, Role.Member, Role.Owner, 1, "role-not-grantable" },
        { Role.Admin, Role.Owner, Role.Member, 2, "owner-protected" },
    };

    [Theory]
    [MemberData(nameof(RoleChanges))]
    public void Who_may_change_which_role(Role actorRole, Role targetRole, Role role, int owners, string? refusal) =>
        Assert.Equal(refusal, Outcome(() =>
            MembershipRules.EnsureCanChangeRole(new Actor(ActorId, actorRole), MemberWith(targetRole), role, owners)));

    [Fact]
    public void Nobody_changes_their_own_role() =>
        Assert.Equal("cannot-change-own-role", Outcome(() =>
            MembershipRules.EnsureCanChangeRole(new Actor(ActorId, Role.Owner), MemberWith(Role.Owner, ActorId), Role.Admin, 2)));

    public static TheoryData<Role, Role, int, string?> Removals => new()
    {
        // actor role, target role, active owners, refusal code
        { Role.Owner, Role.Member, 1, null },
        { Role.Owner, Role.Admin, 1, null },
        { Role.Owner, Role.Owner, 2, null },
        { Role.Owner, Role.Owner, 1, "last-owner" },
        { Role.Admin, Role.Member, 1, null },
        { Role.Admin, Role.Admin, 1, null },
        { Role.Admin, Role.Owner, 2, "owner-protected" },
    };

    [Theory]
    [MemberData(nameof(Removals))]
    public void Who_may_remove_whom(Role actorRole, Role targetRole, int owners, string? refusal) =>
        Assert.Equal(refusal, Outcome(() =>
            MembershipRules.EnsureCanRemove(new Actor(ActorId, actorRole), MemberWith(targetRole), owners)));

    [Fact]
    public void Removing_yourself_is_leaving() =>
        Assert.Equal("use-leave", Outcome(() =>
            MembershipRules.EnsureCanRemove(new Actor(ActorId, Role.Owner), MemberWith(Role.Member, ActorId), 1)));

    [Theory]
    [InlineData(Role.Owner, 1, "last-owner")]
    [InlineData(Role.Owner, 2, null)]
    [InlineData(Role.Admin, 1, null)]
    [InlineData(Role.Viewer, 1, null)]
    public void The_last_owner_cannot_leave(Role role, int owners, string? refusal) =>
        Assert.Equal(refusal, Outcome(() => MembershipRules.EnsureCanLeave(MemberWith(role), owners)));

    public static TheoryData<Role, Role, int, bool, bool> Abilities => new()
    {
        // actor role, target role, active Owners, may change the target's role, may remove the target (the
        // members.manage permission is checked before the rules, so only roles that hold it appear here)
        { Role.Owner, Role.Owner, 2, true, true },
        { Role.Owner, Role.Admin, 1, true, true },
        { Role.Admin, Role.Owner, 2, false, false },
        { Role.Admin, Role.Viewer, 1, true, true },
        { Role.Owner, Role.Owner, 1, false, false },
    };

    [Theory]
    [MemberData(nameof(Abilities))]
    public void The_ui_flags_match_the_rules(Role actorRole, Role targetRole, int owners, bool canChange, bool canRemove)
    {
        var actor = new Actor(ActorId, actorRole);
        var target = MemberWith(targetRole);

        Assert.Equal((canChange, canRemove), (MembershipRules.CanChangeRole(actor, target, owners), MembershipRules.CanRemove(actor, target, owners)));
        Assert.False(MembershipRules.CanChangeRole(new Actor(TargetId, Role.Owner), target, owners));
        Assert.Equal(targetRole != Role.Owner || owners > 1, MembershipRules.CanLeave(target, owners));
    }

    [Fact]
    public void Refusals_about_authority_are_forbidden_and_the_rest_conflicts()
    {
        var protectedOwner = Assert.Throws<DomainException>(() =>
            MembershipRules.EnsureCanRemove(new Actor(ActorId, Role.Admin), MemberWith(Role.Owner), 2));
        var lastOwner = Assert.Throws<DomainException>(() => MembershipRules.EnsureCanLeave(MemberWith(Role.Owner), 1));

        Assert.Equal(DomainErrorKind.Forbidden, protectedOwner.Kind);
        Assert.Equal(DomainErrorKind.Conflict, lastOwner.Kind);
    }

    [Fact]
    public void Membership_changes_are_recorded_and_rejoining_reactivates_quietly()
    {
        var member = MemberWith(Role.Member);

        member.ChangeRole(Role.Admin, "Maya Torres");
        member.ChangeRole(Role.Admin, "Maya Torres");
        member.Remove(Now, "Maya Torres");
        member.Remove(Now, "Maya Torres");
        member.Rejoin(Role.Viewer, ActorId, Now.AddDays(1));

        Assert.Equal(
            ["Changed Maya Torres's role from Member to Admin", "Removed Maya Torres"],
            member.DomainEvents.Cast<MembershipEvent>().Select(e => e.Summary));
        Assert.True(member.IsActive);
        Assert.Equal((Role.Viewer, Now.AddDays(1)), (member.Role, member.JoinedAt));
    }
}
