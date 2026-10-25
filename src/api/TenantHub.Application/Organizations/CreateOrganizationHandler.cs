using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Accounts;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

public sealed record CreateOrganizationCommand(Guid UserId, string Name, string TimeZone);

/// <summary>A signed-in user creates another organization and becomes its Owner (US-03, SPEC 5.1).</summary>
public sealed class CreateOrganizationHandler(
    IAppDbContext db,
    IDatabaseLocks locks,
    OrganizationCreator creator)
{
    public async Task<MembershipSummary> HandleAsync(CreateOrganizationCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Serializes this user's concurrent requests, so two at once cannot both pass the count below.
        await locks.LockUserAsync(command.UserId, cancellationToken);
        // Counts across all organizations (tenant bypass, allow-listed): the limit is per user, not per tenant.
        var owned = await db.Memberships.AcrossTenants().CountAsync(
            m => m.UserId == command.UserId && m.Role == Role.Owner && m.RemovedAt == null,
            cancellationToken);
        if (owned >= Organization.MaxOwnedPerUser)
        {
            throw new DomainException(
                "owned-organization-limit",
                $"You can own at most {Organization.MaxOwnedPerUser} organizations.");
        }

        var organization = await creator.CreateAsync(command.Name, command.TimeZone, command.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MembershipSummary(organization.Id, organization.Slug, organization.Name, Role.Owner);
    }
}
