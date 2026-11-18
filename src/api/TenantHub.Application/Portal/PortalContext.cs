using TenantHub.Application.Abstractions;

namespace TenantHub.Application.Portal;

/// <summary>
/// The portal session a handler works in (SPEC-V2 3.2): the organization, client and contact, entered by the portal
/// filter from the session cookie only. Portal handlers take the client from here and from nowhere else: never from
/// a route, body, query string or header.
/// </summary>
public sealed class PortalContext(ITenantContext tenant)
{
    public Guid OrganizationId => tenant.OrganizationId ?? throw NotPortal();

    public Guid ClientId => tenant.ClientId ?? throw NotPortal();

    public Guid ContactId => tenant.ContactId ?? throw NotPortal();

    private static InvalidOperationException NotPortal() => new("This handler runs in a portal session only.");
}
