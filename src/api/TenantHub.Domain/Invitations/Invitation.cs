using TenantHub.Domain.Memberships;

namespace TenantHub.Domain.Invitations;

public enum InvitationState
{
    Pending,
    Expired,
    Revoked,
    Accepted,
}

/// <summary>
/// An invitation to join an organization with a role (SPEC 5.3, D011). Only a hash of the link's token is kept;
/// the token itself exists in the link alone. Single use and valid for <see cref="ValidFor"/>; resending issues a
/// new token, so the old link stops working.
/// </summary>
public sealed class Invitation : AggregateRoot, ITenantOwned
{
    public static readonly TimeSpan ValidFor = TimeSpan.FromDays(7);

    private Invitation()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Email { get; private set; } = "";

    public string NormalizedEmail { get; private set; } = "";

    public Role Role { get; private set; }

    /// <summary>SHA-256 of the token in the link.</summary>
    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public Guid? AcceptedByUserId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public static Invitation Create(Guid organizationId, string email, Role role, byte[] tokenHash, Actor invitedBy, DateTimeOffset now)
    {
        var trimmed = email.Trim();
        if (!EmailAddress.IsValid(trimmed))
        {
            throw new DomainValidationException(new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });
        }

        MembershipRules.EnsureCanInvite(invitedBy, role);
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Email = trimmed,
            NormalizedEmail = EmailAddress.Normalize(trimmed),
            Role = role,
            TokenHash = tokenHash,
            CreatedAt = now,
            CreatedByUserId = invitedBy.UserId,
            ExpiresAt = now + ValidFor,
        };
        invitation.Raise(new InvitationEvent(invitation, "invitation.created", $"Invited {trimmed} as {role}",
            [new FieldChange("email", null, trimmed), new FieldChange("role", null, role.ToString())]));
        return invitation;
    }

    public InvitationState StateAt(DateTimeOffset now) =>
        AcceptedAt is not null ? InvitationState.Accepted
        : RevokedAt is not null ? InvitationState.Revoked
        : now >= ExpiresAt ? InvitationState.Expired
        : InvitationState.Pending;

    /// <summary>A new token and a new week; also how inviting the same email again replaces the old invitation.</summary>
    public void Resend(byte[] tokenHash, Role role, Actor actor, DateTimeOffset now)
    {
        EnsureOpen(now);
        MembershipRules.EnsureCanInvite(actor, role);
        var changes = role == Role ? [] : new List<FieldChange> { new("role", Role.ToString(), role.ToString()) };
        Role = role;
        TokenHash = tokenHash;
        ExpiresAt = now + ValidFor;
        Raise(new InvitationEvent(this, "invitation.resent", $"Resent the invitation to {Email}", changes));
    }

    public void Revoke(DateTimeOffset now)
    {
        EnsureOpen(now);
        RevokedAt = now;
        Raise(new InvitationEvent(this, "invitation.revoked", $"Revoked the invitation to {Email}", []));
    }

    /// <summary>
    /// Accepts for the given account. The account's email must be the invited one (SPEC 5.3); the caller creates or
    /// reactivates the membership in the same save.
    /// </summary>
    public void Accept(Guid userId, string accountEmail, DateTimeOffset now)
    {
        switch (StateAt(now))
        {
            case InvitationState.Accepted:
                throw new DomainException("invitation-used", "This invitation has already been used.");
            case InvitationState.Revoked:
                throw new DomainException("invitation-revoked", "This invitation was withdrawn. Ask for a new one.");
            case InvitationState.Expired:
                throw new DomainException("invitation-expired", "This invitation has expired. Ask for a new one.");
        }

        if (EmailAddress.Normalize(accountEmail) != NormalizedEmail)
        {
            throw new DomainException("invitation-email-mismatch", "This invitation is for a different email address.");
        }

        AcceptedAt = now;
        AcceptedByUserId = userId;
        Raise(new InvitationEvent(this, "invitation.accepted", $"{Email} accepted the invitation and joined as {Role}", []));
    }

    private void EnsureOpen(DateTimeOffset now)
    {
        if (StateAt(now) is InvitationState.Accepted or InvitationState.Revoked)
        {
            throw new DomainException("invitation-closed", "This invitation has already been used or withdrawn.");
        }
    }
}

public sealed record InvitationEvent(Invitation Invitation, string Action, string Summary, IReadOnlyList<FieldChange> Changes) : IAuditableEvent
{
    public Guid OrganizationId => Invitation.OrganizationId;

    public string EntityType => "invitation";

    public Guid EntityId => Invitation.Id;
}
