using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

public sealed record OrganizationDetails(Guid Id, string Name, string Slug, Plan Plan, string TimeZone, DateTimeOffset CreatedAt);

public sealed record MemberSummary(Guid UserId, string DisplayName, string Email, Role Role, DateTimeOffset JoinedAt);

/// <summary>The current organization (tenant-scoped; the organization comes from <see cref="ITenantContext"/>).</summary>
public sealed class GetOrganizationQuery(IAppDbContext db, ITenantContext tenant)
{
    public Task<OrganizationDetails> HandleAsync(CancellationToken cancellationToken) =>
        db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new OrganizationDetails(o.Id, o.Name, o.Slug, o.Plan, o.TimeZone, o.CreatedAt))
            .SingleAsync(cancellationToken);
}

/// <summary>Active members of the current organization (SPEC 7, <c>GET /members</c>).</summary>
public sealed class ListMembersQuery(IAppDbContext db, IIdentityService identity)
{
    public async Task<IReadOnlyList<MemberSummary>> HandleAsync(CancellationToken cancellationToken)
    {
        // No organization condition here on purpose: the tenant query filter scopes the query, and the
        // cross-tenant tests prove that nothing from another organization comes back.
        var memberships = await db.Memberships
            .AsNoTracking()
            .Where(m => m.RemovedAt == null)
            .Select(m => new { m.UserId, m.Role, m.JoinedAt })
            .ToListAsync(cancellationToken);

        var profiles = await identity.FindProfilesAsync([.. memberships.Select(m => m.UserId)], cancellationToken);
        return [.. memberships
            .Where(m => profiles.ContainsKey(m.UserId))
            .Select(m => new MemberSummary(m.UserId, profiles[m.UserId].DisplayName, profiles[m.UserId].Email, m.Role, m.JoinedAt))
            .OrderBy(m => m.Role)
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }
}
