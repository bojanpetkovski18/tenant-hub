using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Demo;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// The public demo's reset (SPEC 4.1): the seeded organizations are deleted and seeded again, and organizations
/// visitors created more than a day ago are deleted. The schedule is stored in the data itself: the next reset is due
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
        LogReset(logger, seeded, visitors);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo reset: {Seeded} seeded organizations re-seeded, {Visitors} visitor organizations older than a day deleted.")]
    private static partial void LogReset(ILogger logger, int seeded, int visitors);
}

/// <summary>A demo account is one of the seeded people (by email), whoever is signed in as them.</summary>
internal sealed class DemoAccounts(AppDbContext db) : IDemoAccounts
{
    private static readonly string[] Emails = [.. DemoSeeder.Emails];

    public Task<bool> IsDemoAccountAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id == userId && Emails.Contains(u.Email), cancellationToken);
}
