using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Demo;
using TenantHub.Application.Tenancy;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// The public demo's reset (SPEC 4.1): the seeded organizations are deleted and seeded again, organizations
/// visitors created more than a day ago are deleted, and so are visitor accounts over a day old that no organization
/// refers to any more (K2: the public demo should not keep the addresses people typed). The schedule is stored in the data itself: the next reset is due
/// one interval after the seeded organizations were created, so a restart neither resets early nor forgets one.
/// Deleting an organization cascades to everything it owns, its audit entries included (SPEC 6).
/// </summary>
public sealed partial class DemoReset(
    AppDbContext db,
    DemoSeeder seeder,
    DemoMode demo,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DemoReset> logger)
{
    public static readonly TimeSpan VisitorOrganizationLifetime = TimeSpan.FromHours(24);

    private static readonly string[] DemoEmails = [.. DemoSeeder.Emails];

    /// <summary>When the next reset is due; null when the seeded organizations are missing (due now).</summary>
    public async Task<DateTimeOffset?> NextResetAtAsync(CancellationToken cancellationToken)
    {
        var seededAt = await db.Organizations
            .Where(o => o.IsDemoSeed)
            .Select(o => (DateTimeOffset?)o.CreatedAt)
            .MinAsync(cancellationToken);
        return seededAt + demo.ResetInterval;
    }

    /// <summary>Resets when due; returns when the following reset is due.</summary>
    public async Task<DateTimeOffset> ResetIfDueAsync(CancellationToken cancellationToken)
    {
        if (await NextResetAtAsync(cancellationToken) is { } next && next > time.GetUtcNow())
        {
            return next;
        }

        await ResetAsync(cancellationToken);
        return await NextResetAtAsync(cancellationToken) ?? time.GetUtcNow() + demo.ResetInterval;
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cutoff = now - VisitorOrganizationLifetime;
        var seeded = await db.Organizations.Where(o => o.IsDemoSeed).ExecuteDeleteAsync(cancellationToken);
        var visitors = await db.Organizations.Where(o => !o.IsDemoSeed && o.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        await seeder.SeedAsync(configuration[DemoSeeder.PasswordSetting], cancellationToken);
        var accounts = await DeleteStaleVisitorAccountsAsync(cutoff, cancellationToken);
        LogReset(logger, seeded, visitors, accounts);
    }

    /// <summary>
    /// After the seed, so the demo accounts have their memberships back; they are excluded by email anyway. The
    /// bypass is approved for the demo reset (SPEC 3.3, M09 brief): a visitor's memberships and invitations are in
    /// other organizations, and only their absence everywhere makes the account safe to delete.
    /// </summary>
    private Task<int> DeleteStaleVisitorAccountsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        db.Users
            .Where(u => u.CreatedAt < cutoff && !DemoEmails.Contains(u.Email!))
            .Where(u => !db.Memberships.AcrossTenants().Any(m => m.UserId == u.Id))
            .Where(u => !db.Invitations.AcrossTenants().Any(i => i.CreatedByUserId == u.Id))
            .Where(u => !db.Organizations.Any(o => o.CreatedByUserId == u.Id))
            .ExecuteDeleteAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo reset: {Seeded} seeded organizations re-seeded, {Visitors} visitor organizations and {Accounts} visitor accounts older than a day deleted.")]
    private static partial void LogReset(ILogger logger, int seeded, int visitors, int accounts);
}

/// <summary>A demo account is one of the seeded people (by email), whoever is signed in as them.</summary>
internal sealed class DemoAccounts(AppDbContext db) : IDemoAccounts
{
    private static readonly string[] Emails = [.. DemoSeeder.Emails];

    public Task<bool> IsDemoAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id == userId && Emails.Contains(u.Email), cancellationToken);
}
