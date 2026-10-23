using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

public sealed record OrganizationDetails(Guid Id, string Name, string Slug, Plan Plan, string TimeZone, DateTimeOffset CreatedAt);

/// <summary>A plan and its limits, for the plan cards (SPEC 6, Settings → Plan and usage).</summary>
public sealed record PlanOption(Plan Plan, PlanLimits Limits);

/// <summary>The organization with its usage against its plan (US-50: "Seats 3 of 3"), and every plan to compare.</summary>
public sealed record OrganizationOverview(
    Guid Id,
    string Name,
    string Slug,
    Plan Plan,
    string TimeZone,
    DateTimeOffset CreatedAt,
    PlanUsage Usage,
    PlanLimits Limits,
    IReadOnlyList<PlanOption> Plans)
{
    public static readonly IReadOnlyList<PlanOption> AllPlans =
        [.. Enum.GetValues<Plan>().Select(plan => new PlanOption(plan, PlanLimits.For(plan)))];
}

/// <summary>
/// A member, with what the caller may do to them under SPEC 5.2 (computed by <see cref="MembershipRules"/>, so the SPA
/// disables exactly what the API would refuse and never re-implements the rules).
/// </summary>
public sealed record MemberSummary(
    Guid UserId, string DisplayName, string Email, Role Role, DateTimeOffset JoinedAt,
    bool IsYou, bool CanChangeRole, bool CanRemove, bool CanLeave);

/// <summary>The current organization (tenant-scoped; the organization comes from <see cref="ITenantContext"/>).</summary>
public sealed class GetOrganizationQuery(IAppDbContext db, ITenantContext tenant, PlanLimitsCheck limits)
{
    public async Task<OrganizationOverview> HandleAsync(CancellationToken cancellationToken)
    {
        var o = await db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new OrganizationDetails(o.Id, o.Name, o.Slug, o.Plan, o.TimeZone, o.CreatedAt))
            .SingleAsync(cancellationToken);
        var usage = await limits.UsageAsync(cancellationToken);
        return new OrganizationOverview(o.Id, o.Name, o.Slug, o.Plan, o.TimeZone, o.CreatedAt, usage, PlanLimits.For(o.Plan), OrganizationOverview.AllPlans);
    }
}

/// <summary>Active members of the current organization (SPEC 7, <c>GET /members</c>).</summary>
public sealed class ListMembersQuery(IAppDbContext db, ITenantContext tenant, IIdentityService identity)
{
    public async Task<IReadOnlyList<MemberSummary>> HandleAsync(CancellationToken cancellationToken)
    {
        // No organization condition here on purpose: the tenant query filter scopes the query, and the
        // cross-tenant tests prove that nothing from another organization comes back.
        var memberships = await db.Memberships
            .AsNoTracking()
            .Where(m => m.RemovedAt == null)
            .ToListAsync(cancellationToken);
        var actor = new Actor(tenant.UserId!.Value, tenant.Role!.Value);
        var manages = tenant.HasPermission(Permissions.MembersManage);
        var owners = memberships.Count(m => m.Role == Role.Owner);

        var profiles = await identity.FindProfilesAsync([.. memberships.Select(m => m.UserId)], cancellationToken);
        return [.. memberships
            .Where(m => profiles.ContainsKey(m.UserId))
            .Select(m => new MemberSummary(
                m.UserId, profiles[m.UserId].DisplayName, profiles[m.UserId].Email, m.Role, m.JoinedAt,
                IsYou: m.UserId == actor.UserId,
                CanChangeRole: manages && MembershipRules.CanChangeRole(actor, m, owners),
                CanRemove: manages && m.UserId != actor.UserId && MembershipRules.CanRemove(actor, m, owners),
                CanLeave: m.UserId == actor.UserId && MembershipRules.CanLeave(m, owners)))
            .OrderBy(m => m.Role)
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }
}
