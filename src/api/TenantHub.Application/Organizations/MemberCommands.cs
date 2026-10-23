using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Organizations;

/// <summary>
/// Role changes, removal and leaving (US-12). The rules need the organization's whole picture (how many active
/// Owners), so each command takes the organization lock first: two Owners demoting each other at the same moment
/// cannot leave the organization without one. Removal is soft (SPEC 5.2).
/// </summary>
public sealed class MemberCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IIdentityService identity,
    IDatabaseLocks locks,
    TimeProvider time)
{
    public async Task ChangeRoleAsync(Guid userId, Role? role, CancellationToken cancellationToken)
    {
        if (role is not { } newRole)
        {
            throw new RequestValidationException(FieldErrors.For("role", "Choose a role."));
        }

        await using var transaction = await BeginAsync(cancellationToken);
        var target = await LoadActiveAsync(userId, cancellationToken);
        MembershipRules.EnsureCanChangeRole(Actor(), target, newRole, await ActiveOwnersAsync(cancellationToken));

        var profile = await identity.FindProfileAsync(userId, cancellationToken);
        target.ChangeRole(newRole, profile?.DisplayName ?? "a member");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(cancellationToken);
        var target = await LoadActiveAsync(userId, cancellationToken);
        MembershipRules.EnsureCanRemove(Actor(), target, await ActiveOwnersAsync(cancellationToken));

        var profile = await identity.FindProfileAsync(userId, cancellationToken);
        var name = profile?.DisplayName ?? "a member";
        target.Remove(time.GetUtcNow(), name);
        await UnassignOpenJobsAsync(userId, name, "was removed", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(cancellationToken);
        var self = await LoadActiveAsync(tenant.UserId!.Value, cancellationToken);
        MembershipRules.EnsureCanLeave(self, await ActiveOwnersAsync(cancellationToken));

        var profile = await identity.FindProfileAsync(self.UserId, cancellationToken);
        var name = profile?.DisplayName ?? "A member";
        self.Leave(time.GetUtcNow(), name);
        await UnassignOpenJobsAsync(self.UserId, name, "left the organization", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Jobs that are not Completed or Cancelled lose their assignee in the same save as the removal, each with its own
    /// audit entry (SPEC 5.2). Closed jobs keep the former member as assignee, for history.
    /// </summary>
    private async Task UnassignOpenJobsAsync(Guid userId, string memberName, string why, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var open = await db.Jobs
            .Where(j => j.AssigneeUserId == userId && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
            .ToListAsync(cancellationToken);
        foreach (var job in open)
        {
            job.Unassign(memberName, why, now);
        }
    }

    private Actor Actor() => new(tenant.UserId!.Value, tenant.Role!.Value);

    private async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        return transaction;
    }

    private async Task<Membership> LoadActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Memberships.SingleOrDefaultAsync(m => m.UserId == userId && m.RemovedAt == null, cancellationToken)
            ?? throw new DomainException("member-not-found", "Member not found.", DomainErrorKind.NotFound);

    private Task<int> ActiveOwnersAsync(CancellationToken cancellationToken) =>
        db.Memberships.CountAsync(m => m.Role == Role.Owner && m.RemovedAt == null, cancellationToken);
}
