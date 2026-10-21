using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// Development and demo seed (SPEC 8.3, fictional): two organizations and one user per role, plus one person in
/// both organizations to show the switcher. Idempotent: it does nothing once the first organization exists.
/// Clients, jobs and the full demo data set arrive with their milestones (M04, M06, M09).
/// </summary>
public sealed partial class DemoSeeder(AppDbContext db, UserManager<AppUser> users, TimeProvider time, ILogger<DemoSeeder> logger)
{
    public const string PasswordSetting = "DEMO_PASSWORD";

    internal const string BrightlineSlug = "brightline-cleaning-co";

    private static readonly SeedUser[] Users =
    [
        new("olivia@brightline.example", "Olivia Bennett"),
        new("adam@brightline.example", "Adam Clarke"),
        new("maya@brightline.example", "Maya Torres"),
        new("victor@brightline.example", "Victor Lindqvist"),
        new("hana@harborpine.example", "Hana Okafor"),
        new("sam@tenanthub.example", "Sam Rivera"),
    ];

    private static readonly SeedOrganization[] Organizations =
    [
        new("Brightline Cleaning Co.", BrightlineSlug, "Europe/Amsterdam", Plan.Team,
        [
            ("olivia@brightline.example", Role.Owner),
            ("adam@brightline.example", Role.Admin),
            ("maya@brightline.example", Role.Member),
            ("victor@brightline.example", Role.Viewer),
            ("sam@tenanthub.example", Role.Member),
        ]),
        new("Harbor & Pine Landscaping", "harbor-pine-landscaping", "America/New_York", Plan.Free,
        [
            ("hana@harborpine.example", Role.Owner),
            ("sam@tenanthub.example", Role.Admin),
        ]),
    ];

    public static IReadOnlyList<string> Emails { get; } = [.. Users.Select(u => u.Email)];

    public static async Task SeedIfEnabledAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Seed:Enabled", false))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(configuration[PasswordSetting], cancellationToken);
    }

    public async Task SeedAsync(string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            LogSkippedNoPassword(logger, PasswordSetting);
            return;
        }

        if (await db.Organizations.AnyAsync(o => o.Slug == BrightlineSlug, cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var seed in Users)
        {
            var user = new AppUser { UserName = seed.Email, Email = seed.Email, DisplayName = seed.DisplayName, CreatedAt = now };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                // A weak DEMO_PASSWORD is a configuration error: say so instead of seeding half the data.
                throw new InvalidOperationException(
                    $"Demo user {seed.Email} could not be created: {string.Join(" ", result.Errors.Select(e => e.Description))}");
            }

            ids[seed.Email] = user.Id;
        }

        foreach (var seed in Organizations)
        {
            var (ownerEmail, _) = seed.Members[0];
            var organization = Organization.Create(seed.Name, seed.Slug, seed.TimeZone, ids[ownerEmail], now, seed.Plan, isDemoSeed: true);
            db.Organizations.Add(organization);
            foreach (var (email, role) in seed.Members)
            {
                db.Memberships.Add(role == Role.Owner
                    ? organization.AddOwner(ids[email], now)
                    : organization.AddMember(ids[email], role, now, invitedByUserId: ids[ownerEmail]));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogSeeded(logger, Organizations.Length, Users.Length);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo seed skipped: {Setting} is not set (see .env.example).")]
    private static partial void LogSkippedNoPassword(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo seed created {Organizations} organizations and {Users} users.")]
    private static partial void LogSeeded(ILogger logger, int organizations, int users);

    private sealed record SeedUser(string Email, string DisplayName);

    private sealed record SeedOrganization(string Name, string Slug, string TimeZone, Plan Plan, (string Email, Role Role)[] Members);
}
