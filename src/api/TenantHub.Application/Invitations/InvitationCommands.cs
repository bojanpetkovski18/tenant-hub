using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Organizations;
using TenantHub.Domain;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Invitations;

public sealed record InvitationView(
    Guid Id, string Email, Role Role, InvitationState State, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string InvitedBy);

/// <summary>How the invitee gets the link: emailed, or (email disabled or failed) the link for the inviter to pass on.</summary>
public enum InvitationDelivery
{
    Sent,
    Link,
    Failed,
}

/// <summary>The link is returned only when it was not emailed (D011); it is never logged or stored.</summary>
public sealed record InvitationSent(InvitationView Invitation, InvitationDelivery Delivery, string? Link);

public sealed record InviteCommand(string? Email, Role? Role);

/// <summary>
/// Invitation management inside the current organization (US-10). The token exists only in the link: a new one is
/// made on every send and only its hash is saved. Email goes out after the save, so a slow or failing mail server
/// never loses an invitation; the inviter then gets the link instead.
/// </summary>
public sealed class InvitationCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IIdentityService identity,
    IDatabaseLocks locks,
    IDatabaseErrors errors,
    IEmailSender email,
    IAppLinks links,
    PlanLimitsCheck limits,
    TimeProvider time)
{
    public const string OpenEmailIndex = "ux_invitations_open_email";

    /// <summary>Open invitations (pending or expired; accepted and revoked ones are history in the audit log).</summary>
    public async Task<IReadOnlyList<InvitationView>> ListAsync(CancellationToken cancellationToken)
    {
        var invitations = await db.Invitations
            .AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ToViewsAsync(invitations, cancellationToken);
    }

    public async Task<InvitationSent> InviteAsync(InviteCommand command, CancellationToken cancellationToken)
    {
        if (command.Role is not { } role)
        {
            throw new RequestValidationException(FieldErrors.From([("role", "Choose a role.")]));
        }

        var emailAddress = command.Email?.Trim() ?? "";
        var actor = Actor();
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);

        if (EmailAddress.IsValid(emailAddress) && await IsActiveMemberAsync(emailAddress, cancellationToken))
        {
            throw new DomainException("already-member", $"{emailAddress} is already a member of this organization.");
        }

        var (token, hash) = InvitationTokens.Create();
        var normalized = EmailAddress.IsValid(emailAddress) ? EmailAddress.Normalize(emailAddress) : null;
        var open = normalized is null
            ? null
            : await db.Invitations.SingleOrDefaultAsync(
                i => i.NormalizedEmail == normalized && i.AcceptedAt == null && i.RevokedAt == null, cancellationToken);

        // Seats (SPEC 5.3, 5.5): a pending invitation already holds one, so replacing it takes none; a new one, or
        // renewing an expired one, takes a seat. Under the organization lock taken above, so two concurrent
        // invitations cannot both take the last seat. An invalid email is left to the validation below (400).
        if (normalized is not null && open?.StateAt(now) != InvitationState.Pending)
        {
            await limits.EnsureRoomForAsync(LimitKind.Seats, cancellationToken);
        }

        Invitation invitation;
        if (open is not null)
        {
            // Inviting the same email again replaces the open invitation: same row, new token, new week (SPEC 5.3).
            open.Resend(hash, role, actor, now);
            invitation = open;
        }
        else
        {
            invitation = Invitation.Create(tenant.OrganizationId!.Value, emailAddress, role, hash, actor, now);
            db.Invitations.Add(invitation);
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await DeliverAsync(invitation, token, cancellationToken);
    }

    public async Task<InvitationSent> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        var invitation = await LoadAsync(id, cancellationToken);
        var now = time.GetUtcNow();

        // An expired invitation holds no seat; renewing it takes one again.
        if (invitation.StateAt(now) == InvitationState.Expired)
        {
            await limits.EnsureRoomForAsync(LimitKind.Seats, cancellationToken);
        }

        var (token, hash) = InvitationTokens.Create();
        invitation.Resend(hash, invitation.Role, Actor(), now);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await DeliverAsync(invitation, token, cancellationToken);
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var invitation = await LoadAsync(id, cancellationToken);
        invitation.Revoke(time.GetUtcNow());
        await SaveAsync(cancellationToken);
    }

    private Actor Actor() => new(tenant.UserId!.Value, tenant.Role!.Value);

    private async Task<bool> IsActiveMemberAsync(string emailAddress, CancellationToken cancellationToken) =>
        await identity.FindUserIdByEmailAsync(emailAddress, cancellationToken) is { } userId
        && await db.Memberships.AnyAsync(m => m.UserId == userId && m.RemovedAt == null, cancellationToken);

    private async Task<Invitation> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Invitations.SingleOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw InvitationErrors.NotFound();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (errors.IsUniqueViolation(exception, OpenEmailIndex))
        {
            // Only reachable if the organization lock were missing; kept so the answer stays a clean conflict.
            throw new DomainException("invitation-pending", "An invitation to this email is already pending.");
        }
    }

    private async Task<InvitationSent> DeliverAsync(Invitation invitation, string token, CancellationToken cancellationToken)
    {
        var link = links.Invitation(token);
        var view = (await ToViewsAsync([invitation], cancellationToken))[0];
        if (!email.IsEnabled)
        {
            return new InvitationSent(view, InvitationDelivery.Link, link);
        }

        var organizationName = await db.Organizations
            .Where(o => o.Id == invitation.OrganizationId)
            .Select(o => o.Name)
            .SingleAsync(cancellationToken);
        var message = InvitationEmails.Build(invitation.Email, view.InvitedBy, organizationName, invitation.Role, link, invitation.ExpiresAt);
        return await email.SendAsync(message, cancellationToken)
            ? new InvitationSent(view, InvitationDelivery.Sent, null)
            : new InvitationSent(view, InvitationDelivery.Failed, link);
    }

    private async Task<IReadOnlyList<InvitationView>> ToViewsAsync(IReadOnlyList<Invitation> invitations, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var inviters = await identity.FindProfilesAsync([.. invitations.Select(i => i.CreatedByUserId).Distinct()], cancellationToken);
        return [.. invitations.Select(i => new InvitationView(
            i.Id, i.Email, i.Role, i.StateAt(now), i.CreatedAt, i.ExpiresAt,
            inviters.TryGetValue(i.CreatedByUserId, out var inviter) ? inviter.DisplayName : "A former member"))];
    }
}

internal static class InvitationErrors
{
    public static DomainException NotFound() =>
        new("invitation-not-found", "Invitation not found.", DomainErrorKind.NotFound);
}
