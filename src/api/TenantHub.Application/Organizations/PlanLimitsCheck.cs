using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

/// <summary>
/// Counts the current organization's usage and checks a change against its plan (SPEC 5.5, D013). Callers that
/// consume a limit hold the organization-row lock in the same transaction first, so the count cannot go stale
/// before their write: two requests for the last seat are served one after the other, and the second is refused.
/// </summary>
public sealed class PlanLimitsCheck(IAppDbContext db, ITenantContext tenant, TimeProvider time)
{
    /// <summary>Seats are active members plus pending (not accepted, revoked or expired) invitations.</summary>
    public async Task<PlanUsage> UsageAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var members = await db.Memberships.CountAsync(m => m.RemovedAt == null, cancellationToken);
        var pending = await db.Invitations.CountAsync(
            i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, cancellationToken);
        var clients = await db.Clients.CountAsync(c => c.Status == ClientStatus.Active, cancellationToken);
        var jobs = await db.Jobs.CountAsync(j => j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled, cancellationToken);
        return new PlanUsage(members + pending, clients, jobs);
    }

    public async Task<Plan> PlanAsync(CancellationToken cancellationToken) =>
        await db.Organizations.Where(o => o.Id == tenant.OrganizationId).Select(o => o.Plan).SingleAsync(cancellationToken);

    /// <summary>
    /// Refuses with 403 <c>plan-limit-reached</c> when <paramref name="adding"/> more would not fit. Call under the
    /// organization lock. <paramref name="alreadyCounted"/> is usage the change replaces (an invitation becoming a
    /// membership), so it is not counted twice.
    /// </summary>
    public async Task EnsureRoomForAsync(LimitKind kind, CancellationToken cancellationToken, int adding = 1, int alreadyCounted = 0)
    {
        var usage = await UsageAsync(cancellationToken);
        var counted = kind switch
        {
            LimitKind.Seats => usage with { Seats = usage.Seats - alreadyCounted },
            LimitKind.ActiveClients => usage with { ActiveClients = usage.ActiveClients - alreadyCounted },
            _ => usage with { OpenJobs = usage.OpenJobs - alreadyCounted },
        };
        PlanPolicy.EnsureRoomFor(await PlanAsync(cancellationToken), counted, kind, adding);
    }
}
