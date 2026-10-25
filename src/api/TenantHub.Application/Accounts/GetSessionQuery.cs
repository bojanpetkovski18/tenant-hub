using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Accounts;

public sealed record MembershipSummary(Guid OrganizationId, string Slug, string Name, Role Role)
{
    /// <summary>What this role may do (D008); sent to the SPA so it never re-implements the role map.</summary>
    public IReadOnlyList<string> Permissions => [.. Domain.Memberships.Permissions.For(Role).Order(StringComparer.Ordinal)];

    /// <summary>The roles this member may give others, and invite with (SPEC 4.1); for the role pickers.</summary>
    public IReadOnlyList<Role> GrantableRoles => MembershipRules.GrantableRoles(Role);

    public IReadOnlyList<Role> InvitableRoles => [.. MembershipRules.GrantableRoles(Role).Intersect(MembershipRules.InvitableRoles)];
}

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
