using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
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
        target.ChangeRole(newRole, profile?.DisplayName ?? "A member");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(cancellationToken);
        var target = await LoadActiveAsync(userId, cancellationToken);
        MembershipRules.EnsureCanRemove(Actor(), target, await ActiveOwnersAsync(cancellationToken));

        var profile = await identity.FindProfileAsync(userId, cancellationToken);
        target.Remove(time.GetUtcNow(), profile?.DisplayName ?? "a member");
        // M06: unassign the member's jobs that are not Completed or Cancelled here, in the same save (SPEC 5.2).
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await BeginAsync(cancellationToken);
        var self = await LoadActiveAsync(tenant.UserId!.Value, cancellationToken);
        MembershipRules.EnsureCanLeave(self, await ActiveOwnersAsync(cancellationToken));

        var profile = await identity.FindProfileAsync(self.UserId, cancellationToken);
        self.Leave(time.GetUtcNow(), profile?.DisplayName ?? "A member");
        // M06: unassign open jobs, as for removal.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
