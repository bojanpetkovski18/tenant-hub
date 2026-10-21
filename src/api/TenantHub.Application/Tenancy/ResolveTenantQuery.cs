using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Tenancy;

/// <summary>
/// Step one of every tenant-scoped request (SPEC 3.2): the caller's active membership in the organization named by
/// the route. No tenant is entered yet, so this is one of the allow-listed bypasses. Null means "not a member" and
/// the API answers 404, so other organizations' existence is not revealed.
/// </summary>
public sealed class ResolveTenantQuery(IAppDbContext db, TenantContext tenant)
{
    public async Task<bool> TryEnterAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await db.Memberships
            .AcrossTenants()
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId && m.RemovedAt == null)
            .Select(m => (Role?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);

        if (role is not { } found)
        {
            return false;
        }

        tenant.Enter(organizationId, userId, found);
        return true;
    }
}
