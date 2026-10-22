namespace TenantHub.Domain.Memberships;

/// <summary>
/// A user's role in one organization. Removal is soft (<see cref="RemovedAt"/>, SPEC 5.2): the row stays so jobs
/// and audit entries can still name the person, but only active memberships grant access. Whether a change is
/// allowed is decided by <see cref="MembershipRules"/>, which needs the whole organization's memberships.
/// </summary>
public sealed class Membership : AggregateRoot, ITenantOwned
{
    private Membership()
    {
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public Role Role { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public Guid? InvitedByUserId { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsActive => RemovedAt is null;

    /// <summary>Records the change; check <see cref="MembershipRules.EnsureCanChangeRole"/> first.</summary>
    public void ChangeRole(Role role, string memberName)
    {
        if (role == Role)
        {
            return;
        }

        var change = new FieldChange("role", Role.ToString(), role.ToString());
        Role = role;
        Raise(new MembershipEvent(this, "member.role_changed", $"Changed {memberName}'s role from {change.From} to {change.To}", [change]));
    }

    /// <summary>Soft removal by someone else; check <see cref="MembershipRules.EnsureCanRemove"/> first.</summary>
    public void Remove(DateTimeOffset now, string memberName) => End(now, "member.removed", $"Removed {memberName}");

    /// <summary>Soft removal by the member; check <see cref="MembershipRules.EnsureCanLeave"/> first.</summary>
    public void Leave(DateTimeOffset now, string memberName) => End(now, "member.left", $"{memberName} left the organization");

    /// <summary>A removed person invited again gets the same row back (SPEC 5.2); the invitation records it.</summary>
    public void Rejoin(Role role, Guid? invitedByUserId, DateTimeOffset now)
    {
        RemovedAt = null;
        Role = role;
        JoinedAt = now;
        InvitedByUserId = invitedByUserId;
    }

    internal static Membership Create(Guid organizationId, Guid userId, Role role, DateTimeOffset joinedAt, Guid? invitedByUserId) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = role,
            JoinedAt = joinedAt,
            InvitedByUserId = invitedByUserId,
        };

    private void End(DateTimeOffset now, string action, string summary)
    {
        if (!IsActive)
        {
            return;
        }

        RemovedAt = now;
        Raise(new MembershipEvent(this, action, summary, [new FieldChange("status", "Active", "Removed")]));
    }
}

public sealed record MembershipEvent(Membership Membership, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Membership.OrganizationId;

    public string EntityType => "member";

    public Guid EntityId => Membership.UserId;
}
