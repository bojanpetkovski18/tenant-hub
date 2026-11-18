using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Invitations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Portal;

/// <summary>Who a portal session is: taken only from the signed session cookie, never from a request (SPEC-V2 3.2).</summary>
public sealed record PortalIdentity(Guid OrganizationId, Guid ClientId, Guid ContactId);

/// <summary>
/// Portal sign-in by email link (US-81, SPEC-V2 5.3, D040). Before a session exists the request names its
/// organization by slug, as a staff route names it by id, and the handler enters that one organization with no user
/// and no permissions: the tenant filter and row-level security apply exactly as for a request, so nothing is read
/// across organizations and no policy is widened. What it reads is one contact by email, or one link by its hash.
/// </summary>
public sealed class PortalSignIn(
    IAppDbContext db,
    TenantContext tenant,
    IOutbox outbox,
    IEmailSender email,
    IAppLinks links,
    TimeProvider time)
{
    public const string InvalidLinkCode = "portal-link-invalid";

    /// <summary>At most one link per contact in this time, however often or from wherever it is asked for (per email).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Always the same answer to the caller: an unknown organization, a plan without the portal, an unknown or removed
    /// contact, an archived client and the cooldown all end here silently. The email leaves through the outbox.
    /// </summary>
    public async Task RequestAsync(string? organizationSlug, string? emailAddress, CancellationToken cancellationToken)
    {
        var normalized = EmailAddress.Normalize(emailAddress ?? "");
        if (!email.IsEnabled || !EmailAddress.IsValid(normalized)
            || await EnterAsync(organizationSlug, cancellationToken) is not { } organization)
        {
            return;
        }

        var contact = await db.ClientContacts
            .Where(c => c.NormalizedEmail == normalized && c.RemovedAt == null)
            .Join(db.Clients.Where(c => c.Status == ClientStatus.Active), c => c.ClientId, c => c.Id, (contact, _) => contact)
            .FirstOrDefaultAsync(cancellationToken);
        var now = time.GetUtcNow();
        if (contact is null
            || await db.PortalSignInTokens.AnyAsync(t => t.ContactId == contact.Id && t.CreatedAt > now - Cooldown, cancellationToken))
        {
            return;
        }

        var (token, hash) = InvitationTokens.Create();
        db.PortalSignInTokens.Add(PortalSignInToken.Issue(contact, hash, now));
        outbox.Add(PortalEmails.SignIn, PortalEmails.BuildSignIn(contact.Email, contact.Name, organization.Name, links.PortalSignIn(organization.Slug, token)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Spends a link and says who signed in; any problem with it is the same 400, so links cannot be probed.</summary>
    public async Task<(PortalIdentity Identity, PortalSessionView Session)> ConfirmAsync(string? organizationSlug, string? token, CancellationToken cancellationToken)
    {
        var hash = InvitationTokens.HashOf(token ?? "");
        if (hash is null || await EnterAsync(organizationSlug, cancellationToken) is not { } organization)
        {
            throw InvalidLink();
        }

        var now = time.GetUtcNow();
        var link = await db.PortalSignInTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (link is null || !InvitationTokens.Matches(link.TokenHash, hash) || !link.IsUsable(now))
        {
            throw InvalidLink();
        }

        // Spent by one conditional update: of two requests with the same link, exactly one changes the row.
        var spent = await db.PortalSignInTokens
            .Where(t => t.Id == link.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, now), cancellationToken);
        if (spent != 1)
        {
            throw InvalidLink();
        }

        var contact = await db.ClientContacts.SingleAsync(c => c.Id == link.ContactId, cancellationToken);
        var clientName = await db.Clients
            .Where(c => c.Id == contact.ClientId && c.Status == ClientStatus.Active)
            .Select(c => c.Name)
            .SingleOrDefaultAsync(cancellationToken);
        if (!contact.IsActive || clientName is null)
        {
            throw InvalidLink();
        }

        contact.SignedIn(now);
        await db.SaveChangesAsync(cancellationToken);
        return (
            new PortalIdentity(contact.OrganizationId, contact.ClientId, contact.Id),
            new PortalSessionView(new PortalOrganizationView(organization.Name, organization.Slug), new PortalContactView(contact.Id, contact.Name, contact.Email, clientName)));
    }

    /// <summary>The organization with this slug, entered, when it exists and its plan has the portal.</summary>
    private async Task<(string Name, string Slug)?> EnterAsync(string? organizationSlug, CancellationToken cancellationToken)
    {
        var slug = (organizationSlug ?? "").Trim().ToLowerInvariant();
        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Slug == slug)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Plan })
            .SingleOrDefaultAsync(cancellationToken);
        if (organization is null || !PlanFeatures.Includes(organization.Plan, PlanFeature.Portal))
        {
            return null;
        }

        tenant.EnterAsSystem(organization.Id);
        return (organization.Name, organization.Slug);
    }

    private static DomainException InvalidLink() => new(
        InvalidLinkCode,
        "This sign-in link has expired or has already been used. Ask for a new one.",
        DomainErrorKind.Validation);
}
