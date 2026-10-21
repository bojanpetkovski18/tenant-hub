using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

/// <summary>
/// Creates an organization with its Owner membership and a free slug. Shared by sign-up and "create
/// organization". Must run inside the caller's transaction: a failed insert rolls back to EF Core's automatic
/// savepoint, so the next slug candidate can be tried in the same transaction. Once the organization exists, the
/// scope enters it as its Owner, so the membership insert passes the tenant guard like any other tenant write.
/// </summary>
public sealed class OrganizationCreator(IAppDbContext db, IDatabaseErrors errors, TenantContext tenant, TimeProvider time)
{
    public const string SlugIndex = "ix_organizations_slug";
    private const int Attempts = 3;

    public async Task<Organization> CreateAsync(string name, string timeZone, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var normalizedName = Organization.NormalizeName(name);
        var candidates = OrganizationSlug.Candidates(normalizedName).ToList();

        for (var attempt = 1; ; attempt++)
        {
            var taken = await db.Organizations
                .Where(o => candidates.Contains(o.Slug))
                .Select(o => o.Slug)
                .ToListAsync(cancellationToken);
            var slug = candidates.FirstOrDefault(candidate => !taken.Contains(candidate))
                ?? throw new DomainException(
                    "organization-name-taken",
                    "Too many organizations already use a similar name; choose a more distinctive one.",
                    DomainErrorKind.Validation);

            var now = time.GetUtcNow();
            var organization = Organization.Create(normalizedName, slug, timeZone, ownerUserId, now);
            db.Organizations.Add(organization);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (errors.IsUniqueViolation(exception, SlugIndex) && attempt < Attempts)
            {
                // Another request took the slug between the check and the insert; forget this row and try again.
                db.Organizations.Entry(organization).State = EntityState.Detached;
                continue;
            }

            tenant.Enter(organization.Id, ownerUserId, Role.Owner);
            db.Memberships.Add(organization.AddOwner(ownerUserId, now));
            await db.SaveChangesAsync(cancellationToken);
            return organization;
        }
    }
}
