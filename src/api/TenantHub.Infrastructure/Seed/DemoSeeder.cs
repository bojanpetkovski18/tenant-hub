using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Identity;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// Development and demo seed (SPEC 8.3, fictional): Brightline Cleaning Co. (Team, six people, forty clients, 150 jobs
/// over four months back and one ahead) and Harbor &amp; Pine Landscaping (Free, three people, close to every limit),
/// with one person in both to show the switcher.
/// The history is replayed through the domain in time order, each step saved at its own moment by the person who
/// would do it, so numbers, dates and the audit log agree. Deterministic and relative to today. Idempotent per user
/// and per organization; a failure part-way removes the half-seeded organization.
/// </summary>
public sealed partial class DemoSeeder(
    AppDbContext db,
    UserManager<AppUser> users,
    DbContextOptions<AppDbContext> options,
    TimeProvider time,
    ILogger<DemoSeeder> logger)
{
    public const string PasswordSetting = "DEMO_PASSWORD";

    internal const string BrightlineSlug = "brightline-cleaning-co";
    internal const string HarborPineSlug = "harbor-pine-landscaping";

    private static readonly SeedPerson[] People =
    [
        new("olivia@brightline.example", "Olivia Bennett", Role.Owner),
        new("adam@brightline.example", "Adam Clarke", Role.Admin),
        new("maya@brightline.example", "Maya Torres", Role.Member),
        new("victor@brightline.example", "Victor Lindqvist", Role.Viewer),
        new("noah@brightline.example", "Noah Fischer", Role.Member),
        new("hana@harborpine.example", "Hana Okafor", Role.Owner),
        new("leo@harborpine.example", "Leo Marsh", Role.Member),
        new("sam@tenanthub.example", "Sam Rivera", Role.Member),
    ];

    internal static readonly SeedOrganization Brightline = new(
        "Brightline Cleaning Co.",
        BrightlineSlug,
        "Europe/Amsterdam",
        Plan.Team,
        RandomSeed: 20_261_024,
        [
            Person("olivia@brightline.example", Role.Owner),
            Person("adam@brightline.example", Role.Admin),
            Person("maya@brightline.example", Role.Member),
            Person("victor@brightline.example", Role.Viewer),
            Person("noah@brightline.example", Role.Member),
            Person("sam@tenanthub.example", Role.Member),
        ]);

    internal static readonly SeedOrganization HarborPine = new(
        "Harbor & Pine Landscaping",
        HarborPineSlug,
        "America/New_York",
        Plan.Free,
        RandomSeed: 20_261_025,
        [
            Person("hana@harborpine.example", Role.Owner),
            Person("sam@tenanthub.example", Role.Admin),
            Person("leo@harborpine.example", Role.Member),
        ]);

    public static IReadOnlyList<string> Emails { get; } = [.. People.Select(u => u.Email)];

    /// <summary>The demo accounts, one per role, all in Brightline: what the "Try as …" buttons sign in as (US-61).</summary>
    public static IReadOnlyList<SeedPerson> DemoAccounts { get; } =
    [
        .. new[] { Role.Owner, Role.Admin, Role.Member, Role.Viewer }
            .Select(role => Brightline.People.First(p => p.Role == role && p.Email.EndsWith("@brightline.example", StringComparison.Ordinal))),
    ];

    public static async Task SeedIfEnabledAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Seed:Enabled", false))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(configuration[PasswordSetting], cancellationToken);
    }

    /// <summary>
    /// Creates missing users (and puts back demo users' names), then each missing organization. Returns how many
    /// organizations were created.
    /// </summary>
    public async Task<int> SeedAsync(string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            LogSkippedNoPassword(logger, PasswordSetting);
            return 0;
        }

        var now = time.GetUtcNow();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var seed in People)
        {
            if (await users.FindByEmailAsync(seed.Email) is { } existing)
            {
                if (existing.DisplayName != seed.DisplayName)
                {
                    existing.DisplayName = seed.DisplayName;
                    await users.UpdateAsync(existing);
                }

                ids[seed.Email] = existing.Id;
                continue;
            }

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

        var created = 0;
        foreach (var (organization, clients, archivedEvery, jobs, firstDay, span) in new[]
        {
            (Brightline, DemoClients.CleaningCompany(), 8, 150, -118, 147),
            (HarborPine, DemoClients.Landscaper(), 12, 70, -36, 70),
        })
        {
            if (await db.Organizations.AnyAsync(o => o.Slug == organization.Slug, cancellationToken))
            {
                continue;
            }

            var steps = DemoPlan.For(organization, clients, archivedEvery, jobs, firstDay, span, now);
            await SeedOrganizationAsync(organization, clients, steps, ids, now, cancellationToken);
            created++;
        }

        if (created > 0)
        {
            LogSeeded(logger, created, People.Length);
        }

        return created;
    }

    private static SeedPerson Person(string email, Role role) =>
        People.Single(p => p.Email == email) with { Role = role };

    private async Task SeedOrganizationAsync(
        SeedOrganization seed,
        IReadOnlyList<ClientDetails> clients,
        List<SeedStep> steps,
        Dictionary<string, Guid> ids,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var clock = new SeedClock(now);
        var owner = seed.People.Single(p => p.Role == Role.Owner);
        var organization = Organization.Create(seed.Name, seed.Slug, seed.TimeZone, ids[owner.Email], now, seed.Plan, isDemoSeed: true);
        await using (var plain = new AppDbContext(options, null, clock))
        {
            plain.Organizations.Add(organization);
            await plain.SaveChangesAsync(cancellationToken);
        }

        try
        {
            // Everyone joined before the history starts.
            var joined = steps.Count > 0 ? steps[0].At.AddDays(-3) : now;
            await using (var members = Context(organization.Id, ids[owner.Email], Role.Owner, clock))
            {
                foreach (var person in seed.People)
                {
                    members.Memberships.Add(person.Role == Role.Owner
                        ? organization.AddOwner(ids[person.Email], joined)
                        : organization.AddMember(ids[person.Email], person.Role, joined, ids[owner.Email]));
                }

                await members.SaveChangesAsync(cancellationToken);
            }

            var roles = seed.People.ToDictionary(p => p.Email, p => p.Role, StringComparer.Ordinal);
            var clientIds = new Guid[clients.Count];
            var jobIds = new Dictionary<int, Guid>();
            var names = new Dictionary<Guid, string>();
            foreach (var person in seed.People)
            {
                names[ids[person.Email]] = person.DisplayName;
            }

            foreach (var step in steps)
            {
                clock.Now = step.At;
                var actorId = ids[step.ActorEmail];
                var role = roles[step.ActorEmail];
                await using var context = Context(organization.Id, actorId, role, clock);
                switch (step)
                {
                    case CreateClientStep create:
                        var client = Client.Create(organization.Id, create.Details, step.At);
                        context.Clients.Add(client);
                        clientIds[create.Client] = client.Id;
                        names[client.Id] = client.Name;
                        break;
                    case UpdateClientStep update:
                        (await context.Clients.SingleAsync(c => c.Id == clientIds[update.Client], cancellationToken)).Update(update.Details, step.At);
                        break;
                    case ArchiveClientStep archive:
                        (await context.Clients.SingleAsync(c => c.Id == clientIds[archive.Client], cancellationToken)).Archive(step.At);
                        break;
                    case CreateJobStep plan:
                        var tracked = await context.Organizations.SingleAsync(o => o.Id == organization.Id, cancellationToken);
                        var details = JobDetails.Validate(
                            plan.Title,
                            plan.Description,
                            clientIds[plan.Client],
                            plan.AssigneeEmail is { } assignee ? ids[assignee] : null,
                            plan.Priority,
                            plan.ScheduledOn,
                            plan.DueOn,
                            plan.QuotedAmount);
                        var job = Job.Create(organization.Id, tracked.TakeJobNumber(), details, JobActor.From(actorId, role), names, step.At);
                        context.Jobs.Add(job);
                        jobIds[plan.Job] = job.Id;
                        break;
                    case MoveJobStep move:
                        (await context.Jobs.SingleAsync(j => j.Id == jobIds[move.Job], cancellationToken))
                            .TransitionTo(move.To, JobActor.From(actorId, role), move.Reason, step.At);
                        break;
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            LogSeededOrganization(logger, seed.Slug, steps.Count);
        }
        catch
        {
            // Half an organization is worse than none: the next start seeds it again from scratch.
            await using var cleanup = new AppDbContext(options);
            await cleanup.Organizations.Where(o => o.Id == organization.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>A context working in the organization as this person, writing at the seed clock's time.</summary>
    private AppDbContext Context(Guid organizationId, Guid userId, Role role, SeedClock clock)
    {
        var tenant = new TenantContext();
        tenant.Enter(organizationId, userId, role);
        return new AppDbContext(options, tenant, clock);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo seed skipped: {Setting} is not set (see .env.example).")]
    private static partial void LogSkippedNoPassword(ILogger logger, string setting);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo seed created {Organizations} organizations ({Users} demo users).")]
    private static partial void LogSeeded(ILogger logger, int organizations, int users);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Slug} with {Steps} steps of history.")]
    private static partial void LogSeededOrganization(ILogger logger, string slug, int steps);

    /// <summary>The seed's own clock: each step's audit entry is written at the moment the step happened.</summary>
    private sealed class SeedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
