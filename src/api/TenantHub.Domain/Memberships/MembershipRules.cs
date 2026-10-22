namespace TenantHub.Domain.Memberships;

/// <summary>The caller, as the rules see them: who they are and their role in this organization.</summary>
public sealed record Actor(Guid UserId, Role Role);

/// <summary>
/// The membership rules of SPEC 5.2 and 5.3, in one place. They need the organization's whole picture (how many
/// active Owners there are), so they live in a domain service rather than on one membership. The permission check
/// (<c>members.manage</c>) has already passed; these are the rules about <em>which</em> member and role.
/// </summary>
public static class MembershipRules
{
    /// <summary>Owners are made only by promoting an existing member, never by invitation (SPEC 4.1).</summary>
    public static IReadOnlyList<Role> InvitableRoles { get; } = [Role.Admin, Role.Member, Role.Viewer];

    /// <summary>The roles this actor may give to others: Owners any, Admins any but Owner.</summary>
    public static IReadOnlyList<Role> GrantableRoles(Role actorRole) => actorRole switch
    {
        Role.Owner => [Role.Owner, Role.Admin, Role.Member, Role.Viewer],
        Role.Admin => [Role.Admin, Role.Member, Role.Viewer],
        _ => [],
    };

    public static void EnsureCanInvite(Actor actor, Role role)
    {
        if (!InvitableRoles.Contains(role))
        {
            throw new DomainException(
                "owner-not-invitable",
                "Invite as Admin, Member or Viewer; make someone an Owner by changing an existing member's role.",
                DomainErrorKind.Validation);
        }

        if (!GrantableRoles(actor.Role).Contains(role))
        {
            throw Forbidden("role-not-grantable", $"You cannot give the {role} role.");
        }
    }

    public static void EnsureCanChangeRole(Actor actor, Membership target, Role role, int activeOwners)
    {
        if (target.UserId == actor.UserId)
        {
            throw new DomainException("cannot-change-own-role", "You cannot change your own role; ask another Owner or Admin.");
        }

        EnsureMayManage(actor, target);
        if (!GrantableRoles(actor.Role).Contains(role))
        {
            throw Forbidden("role-not-grantable", $"You cannot give the {role} role.");
        }

        if (target.Role == Role.Owner && role != Role.Owner && activeOwners <= 1)
        {
            throw LastOwner();
        }
    }

    public static void EnsureCanRemove(Actor actor, Membership target, int activeOwners)
    {
        if (target.UserId == actor.UserId)
        {
            throw new DomainException("use-leave", "To remove yourself, leave the organization instead.");
        }

        EnsureMayManage(actor, target);
        if (target.Role == Role.Owner && activeOwners <= 1)
        {
            throw LastOwner();
        }
    }

    public static void EnsureCanLeave(Membership self, int activeOwners)
    {
        if (self.Role == Role.Owner && activeOwners <= 1)
        {
            throw LastOwner();
        }
    }

    private static void EnsureMayManage(Actor actor, Membership target)
    {
        if (target.Role == Role.Owner && actor.Role != Role.Owner)
        {
            throw Forbidden("owner-protected", "Only an Owner can change or remove another Owner.");
        }
    }

    private static DomainException LastOwner() =>
        new("last-owner", "An organization needs at least one Owner. Make someone else an Owner first.");

    private static DomainException Forbidden(string code, string message) => new(code, message, DomainErrorKind.Forbidden);
}
