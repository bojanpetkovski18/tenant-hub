namespace TenantHub.Domain.Memberships;

/// <summary>
/// A user's role in one organization. Removal is soft (<see cref="RemovedAt"/>, SPEC 5.2): the row stays so jobs
/// and audit entries can still name the person, but only active memberships grant access.
/// </summary>
public sealed class Membership
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

    internal static Membership Create(Guid organizationId, Guid userId, Role role, DateTimeOffset joinedAt, Guid? invitedByUserId) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = role,
            JoinedAt = joinedAt,
            InvitedByUserId = invitedByUserId,
        };
}
