using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Invitations;

/// <summary>
/// What the accept page shows (SPEC 7). The email is masked; <see cref="AccountExists"/> chooses between the sign-in
/// and sign-up forms, and the signed-in fields between "join", "different email" and "already a member". Only the
/// link's holder can ask, and the link was sent to that address.
/// </summary>
public sealed record InvitationPreview(
    string OrganizationName,
    string InvitedBy,
    Role Role,
    string MaskedEmail,
    InvitationState State,
    DateTimeOffset ExpiresAt,
    bool AccountExists,
    bool? SignedInEmailMatches,
    Guid? MemberOfOrganizationId);

public sealed record AcceptInvitationResult(Guid OrganizationId, Guid UserId, bool AlreadyMember);

public sealed record InvitationSignUpCommand(string Token, string DisplayName, string Password);

/// <summary>
/// Looking up and accepting an invitation by its link (US-11). The invitee is not a member yet, so no tenant can be
/// entered for the lookup: this file is the allow-listed bypass for invitations (SPEC 3.3, point 5; approved in the
/// M05 brief). The lookup is by the token's hash only, and before anything is written the scope enters the
/// invitation's organization, so every write passes the tenant guard like any other tenant write.
/// </summary>
public sealed class InvitationAcceptance(
    IAppDbContext db,
    TenantContext tenant,
    IIdentityService identity,
    IDatabaseLocks locks,
    TimeProvider time)
{
    public async Task<InvitationPreview> PreviewAsync(string token, Guid? signedInUserId, CancellationToken cancellationToken)
    {
        var invitation = await FindAsync(token, tracked: false, cancellationToken);
        var organizationName = await db.Organizations
            .Where(o => o.Id == invitation.OrganizationId)
            .Select(o => o.Name)
            .SingleAsync(cancellationToken);
        var inviter = await identity.FindProfileAsync(invitation.CreatedByUserId, cancellationToken);
        var accountExists = await identity.FindUserIdByEmailAsync(invitation.Email, cancellationToken) is not null;

        bool? emailMatches = null;
        Guid? memberOf = null;
        if (signedInUserId is { } userId && await identity.FindProfileAsync(userId, cancellationToken) is { } account)
        {
            emailMatches = EmailAddress.Normalize(account.Email) == invitation.NormalizedEmail;
            if (await IsActiveMemberAsync(invitation.OrganizationId, userId, cancellationToken))
            {
                memberOf = invitation.OrganizationId;
            }
        }

        return new InvitationPreview(
            organizationName,
            inviter?.DisplayName ?? "A former member",
            invitation.Role,
            InvitationTokens.MaskEmail(invitation.Email),
            invitation.StateAt(time.GetUtcNow()),
            invitation.ExpiresAt,
            accountExists,
            emailMatches,
            memberOf);
    }

    /// <summary>Signed-in acceptance. Someone who is already a member simply goes to the organization.</summary>
    public async Task<AcceptInvitationResult> AcceptAsync(string token, Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await FindLockedAsync(token, cancellationToken);
        if (await IsActiveMemberAsync(invitation.OrganizationId, userId, cancellationToken))
        {
            return new AcceptInvitationResult(invitation.OrganizationId, userId, AlreadyMember: true);
        }

        var account = await identity.FindProfileAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("The signed-in account no longer exists.");
        await JoinAsync(invitation, userId, account.Email, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptInvitationResult(invitation.OrganizationId, userId, AlreadyMember: false);
    }

    /// <summary>
    /// A new account for the invited email, and its membership, in one transaction: the account is not created if
    /// the invitation cannot be accepted, and the invitation is not used if the account cannot be created.
    /// </summary>
    public async Task<AcceptInvitationResult> SignUpAsync(InvitationSignUpCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await FindLockedAsync(command.Token, cancellationToken);
        invitation.EnsureAcceptable(time.GetUtcNow());

        var user = await identity.CreateUserAsync(invitation.Email, command.DisplayName, command.Password, cancellationToken);
        if (user.UserId is not { } userId)
        {
            throw new RequestValidationException(user.Errors);
        }

        await JoinAsync(invitation, userId, invitation.Email, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcceptInvitationResult(invitation.OrganizationId, userId, AlreadyMember: false);
    }

    private async Task JoinAsync(Invitation invitation, Guid userId, string accountEmail, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        tenant.Enter(invitation.OrganizationId, userId, invitation.Role);
        invitation.Accept(userId, accountEmail, now);

        // A removed member invited again gets their old row back (SPEC 5.2), so past jobs and audit still name them.
        var former = await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (former is not null)
        {
            former.Rejoin(invitation.Role, invitation.CreatedByUserId, now);
        }
        else
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == invitation.OrganizationId, cancellationToken);
            db.Memberships.Add(organization.AddMember(userId, invitation.Role, now, invitation.CreatedByUserId));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The invitation, re-read under its organization's lock so two accepts cannot both use it.</summary>
    private async Task<Invitation> FindLockedAsync(string token, CancellationToken cancellationToken)
    {
        var found = await FindAsync(token, tracked: false, cancellationToken);
        await locks.LockOrganizationAsync(found.OrganizationId, cancellationToken);
        return await FindAsync(token, tracked: true, cancellationToken);
    }

    private async Task<Invitation> FindAsync(string token, bool tracked, CancellationToken cancellationToken)
    {
        if (InvitationTokens.HashOf(token) is not { } hash)
        {
            throw InvitationErrors.NotFound();
        }

        var invitations = db.Invitations.AcrossTenants();
        var invitation = await (tracked ? invitations : invitations.AsNoTracking())
            .SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        return invitation is not null && InvitationTokens.Matches(invitation.TokenHash, hash)
            ? invitation
            : throw InvitationErrors.NotFound();
    }

    private Task<bool> IsActiveMemberAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken) =>
        db.Memberships.AcrossTenants()
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId && m.RemovedAt == null, cancellationToken);
}
