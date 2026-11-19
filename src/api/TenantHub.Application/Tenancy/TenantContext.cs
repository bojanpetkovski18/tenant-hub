using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Tenancy;

/// <summary>
/// Request-scoped <see cref="ITenantContext"/>. Entered once per scope: the tenant-scoped endpoint filter enters
/// it after loading the caller's membership, and creating an organization enters the new one for its Owner. A
/// second, different organization in the same scope is refused, so code can never drift between tenants mid-request.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    public Guid? OrganizationId { get; private set; }

    public Guid? UserId { get; private set; }

    public Role? Role { get; private set; }

    public IReadOnlySet<string> Permissions { get; private set; } = NoPermissions;

    public Guid? ClientId { get; private set; }

    public Guid? ContactId { get; private set; }

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    public void Enter(Guid organizationId, Guid userId, Role role)
    {
        if (OrganizationId is { } current && (current != organizationId || UserId != userId || ContactId is not null))
        {
            throw new InvalidOperationException("A scope works inside one organization for one user; it cannot switch.");
        }

        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        Permissions = Domain.Memberships.Permissions.For(role);
    }

    /// <summary>
    /// Scheduled work for one organization (agreement generation, SPEC-V2 4.9): no user and no permissions. Whatever it
    /// writes passes the tenant guard and row-level security like a request; its audit entries name the system.
    /// </summary>
    public void EnterAsSystem(Guid organizationId)
    {
        if (OrganizationId is { } current && (current != organizationId || UserId is not null || ContactId is not null))
        {
            throw new InvalidOperationException("A scope works inside one organization for one actor; it cannot switch.");
        }

        OrganizationId = organizationId;
    }

    /// <summary>
    /// A portal request (SPEC-V2 3.2): the organization, client and contact come from the portal session only, after the
    /// portal filter checked that all three still exist and are active. No user and no staff permissions.
    /// </summary>
    public void EnterAsContact(Guid organizationId, Guid clientId, Guid contactId)
    {
        if (OrganizationId is { } current && (current != organizationId || UserId is not null || ClientId != clientId || ContactId != contactId))
        {
            throw new InvalidOperationException("A scope works inside one organization for one actor; it cannot switch.");
        }

        OrganizationId = organizationId;
        ClientId = clientId;
        ContactId = contactId;
    }
}
