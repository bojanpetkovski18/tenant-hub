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

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    public void Enter(Guid organizationId, Guid userId, Role role)
    {
        if (OrganizationId is { } current && (current != organizationId || UserId != userId))
        {
            throw new InvalidOperationException("A scope works inside one organization for one user; it cannot switch.");
        }

        OrganizationId = organizationId;
        UserId = userId;
        Role = role;
        Permissions = Domain.Memberships.Permissions.For(role);
    }
}
