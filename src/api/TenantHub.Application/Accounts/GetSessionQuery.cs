using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Accounts;

public sealed record MembershipSummary(Guid OrganizationId, string Slug, string Name, Role Role);

public sealed record SessionUser(Guid Id, string Email, string DisplayName, IReadOnlyList<MembershipSummary> Memberships);

/// <summary>The signed-in user and their active memberships (removed ones grant nothing, SPEC 5.2).</summary>
public sealed class GetSessionQuery(IAppDbContext db, IIdentityService identity)
{
    public async Task<SessionUser?> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await identity.FindProfileAsync(userId, cancellationToken) is not { } profile)
        {
            return null;
        }

        // User-scoped: the user's memberships in every organization (tenant bypass, allow-listed).
        var memberships = await db.Memberships
            .AcrossTenants()
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.RemovedAt == null)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Slug, o.Name, m.Role })
            .OrderBy(m => m.Name)
            .ThenBy(m => m.Slug)
            .Select(m => new MembershipSummary(m.Id, m.Slug, m.Name, m.Role))
            .ToListAsync(cancellationToken);

        return new SessionUser(profile.Id, profile.Email, profile.DisplayName, memberships);
    }
}
