using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Organizations;

/// <summary>The organization's facts that go into its log line when it is deleted (its audit entries go with it).</summary>
public sealed record DeletedOrganization(Guid Id, string Slug, int Members);

/// <summary>
/// Organization settings (US-50, US-51): rename and re-zone (Admins and Owners), change plan and delete (Owners). The
/// route has already checked the permission; the plan change counts usage under the organization lock, so it cannot
/// race the commands that consume limits.
/// </summary>
public sealed class OrganizationCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IDatabaseLocks locks,
    PlanLimitsCheck limits,
    GetOrganizationQuery overview)
{
    public async Task<OrganizationOverview> UpdateSettingsAsync(string? name, string? timeZone, CancellationToken cancellationToken)
    {
        var organization = await LoadAsync(cancellationToken);
        if (organization.UpdateSettings(name, timeZone))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return await overview.HandleAsync(cancellationToken);
    }

    public async Task<OrganizationOverview> ChangePlanAsync(Plan? plan, CancellationToken cancellationToken)
    {
        if (plan is not { } target)
        {
            throw new RequestValidationException(FieldErrors.For("plan", "Choose a plan."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        var organization = await LoadAsync(cancellationToken);
        if (organization.ChangePlan(target, await limits.UsageAsync(cancellationToken)))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await overview.HandleAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the organization and, by cascade in the database, everything it owns, its audit entries included.
    /// The caller must type the organization's name (US-51), compared after collapsing whitespace, case-sensitively.
    /// </summary>
    public async Task<DeletedOrganization> DeleteAsync(string? confirmName, CancellationToken cancellationToken)
    {
        var organization = await LoadAsync(cancellationToken);
        var typed = string.Join(' ', (confirmName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!string.Equals(typed, organization.Name, StringComparison.Ordinal))
        {
            throw new RequestValidationException(FieldErrors.For("confirmName", $"Type the organization's name exactly: {organization.Name}"));
        }

        var members = await db.Memberships.CountAsync(m => m.RemovedAt == null, cancellationToken);
        db.Organizations.Remove(organization);
        await db.SaveChangesAsync(cancellationToken);
        return new DeletedOrganization(organization.Id, organization.Slug, members);
    }

    private async Task<Organization> LoadAsync(CancellationToken cancellationToken) =>
        await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, cancellationToken);
}
