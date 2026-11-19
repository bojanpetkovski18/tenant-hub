using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Portal;

/// <summary>The business as its portal shows it: its name and nothing else about it (SPEC-V2 2.3).</summary>
public sealed record PortalOrganizationView(string Name, string Slug);

public sealed record PortalContactView(Guid Id, string Name, string Email, string ClientName);

/// <summary><c>GET /api/portal/session</c>: no contact when signed out; no organization for an unknown portal.</summary>
public sealed record PortalSessionView(PortalOrganizationView? Organization, PortalContactView? Contact);

/// <summary>
/// The portal's equivalent of tenant resolution (SPEC-V2 3.2, mirroring D006): on every portal request the
/// organization must still have the portal on its plan, the client must be active and the contact not removed;
/// otherwise the session has ended. Entering happens only from a signed session cookie.
/// </summary>
public sealed class PortalSessions(IAppDbContext db, TenantContext tenant)
{
    public const string SessionEndedCode = "portal-session-ended";

    /// <summary>Enters the session's organization, client and contact; false when any of them is gone.</summary>
    public async Task<bool> TryEnterAsync(PortalIdentity identity, CancellationToken cancellationToken)
    {
        var plan = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == identity.OrganizationId)
            .Select(o => (Plan?)o.Plan)
            .SingleOrDefaultAsync(cancellationToken);
        if (plan is not { } current || !PlanFeatures.Includes(current, PlanFeature.Portal))
        {
            return false;
        }

        tenant.EnterAsContact(identity.OrganizationId, identity.ClientId, identity.ContactId);
        return await db.ClientContacts.AnyAsync(c => c.Id == identity.ContactId && c.ClientId == identity.ClientId && c.RemovedAt == null, cancellationToken)
            && await db.Clients.AnyAsync(c => c.Id == identity.ClientId && c.Status == ClientStatus.Active, cancellationToken);
    }

    /// <summary>The signed-in contact (already entered), with their organization and client.</summary>
    public async Task<PortalSessionView> CurrentAsync(CancellationToken cancellationToken)
    {
        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new PortalOrganizationView(o.Name, o.Slug))
            .SingleAsync(cancellationToken);
        var contact = await db.ClientContacts.AsNoTracking()
            .Where(c => c.Id == tenant.ContactId)
            .Join(db.Clients, c => c.ClientId, c => c.Id, (c, client) => new PortalContactView(c.Id, c.Name, c.Email, client.Name))
            .SingleAsync(cancellationToken);
        return new PortalSessionView(organization, contact);
    }

    /// <summary>Signed out: the portal named by the slug, for its sign-in page, when it exists and is on.</summary>
    public async Task<PortalSessionView> SignedOutAsync(string? organizationSlug, CancellationToken cancellationToken)
    {
        var slug = (organizationSlug ?? "").Trim().ToLowerInvariant();
        var organization = await db.Organizations.AsNoTracking()
            .Where(o => o.Slug == slug)
            .Select(o => new { o.Name, o.Slug, o.Plan })
            .SingleOrDefaultAsync(cancellationToken);
        return new PortalSessionView(
            organization is not null && PlanFeatures.Includes(organization.Plan, PlanFeature.Portal) ? new(organization.Name, organization.Slug) : null,
            null);
    }
}
