using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;

namespace TenantHub.Application.Demo;

/// <summary>
/// The public demo's switch (SPEC 4.1, <c>DEMO_MODE</c>) and how often the seeded organizations are reset
/// (<c>DEMO_RESET_INTERVAL_HOURS</c>).
/// </summary>
public sealed record DemoMode(bool Enabled, int ResetIntervalHours)
{
    public static readonly DemoMode Off = new(false, 6);

    public TimeSpan ResetInterval => TimeSpan.FromHours(ResetIntervalHours);
}

/// <summary>The seeded demo people, whoever signs in as them (the list lives with the seed).</summary>
public interface IDemoAccounts
{
    Task<bool> IsDemoAccountAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// In demo mode, every visitor shares the seeded accounts, so one visitor must not lock the others out or take the
/// demo apart before the next reset: the demo accounts keep their password and their places in the seeded
/// organizations, and the seeded organizations cannot be deleted. Outside demo mode nothing is refused here.
/// </summary>
public sealed class DemoProtection(DemoMode demo, IDemoAccounts accounts, IAppDbContext db, ITenantContext tenant)
{
    public const string Code = "demo-protected";

    public async Task EnsureCanChangePasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (demo.Enabled && await accounts.IsDemoAccountAsync(userId, cancellationToken))
        {
            throw Refused("Demo accounts keep their password, so every visitor can use them.");
        }
    }

    /// <summary>Role changes, removal and leaving, in the current organization.</summary>
    public async Task EnsureCanChangeMembershipAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (demo.Enabled && await IsSeededOrganizationAsync(cancellationToken) && await accounts.IsDemoAccountAsync(userId, cancellationToken))
        {
            throw Refused("Demo accounts keep their roles in the demo organizations. Invite someone to try this.");
        }
    }

    public void EnsureCanDelete(bool isDemoSeed)
    {
        if (demo.Enabled && isDemoSeed)
        {
            throw Refused("The demo organizations cannot be deleted. Create your own organization to try this.");
        }
    }

    private Task<bool> IsSeededOrganizationAsync(CancellationToken cancellationToken) =>
        db.Organizations.AnyAsync(o => o.Id == tenant.OrganizationId && o.IsDemoSeed, cancellationToken);

    private static DomainException Refused(string message) => new(Code, message, DomainErrorKind.Forbidden);
}
