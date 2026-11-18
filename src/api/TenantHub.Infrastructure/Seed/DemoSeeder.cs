using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Agreements;
using TenantHub.Application.Demo;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Background;
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
    IIdentityService identity,
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

    // Repeating work (M16): the visits are generated and, in the past, done by the assignee.
    private static readonly SeedAgreement[] BrightlineAgreements =
    [
        new("Weekly office cleaning", Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday]), 28, "maya@brightline.example", 180m, 120),
        new("Window cleaning", Recurrence.Weekly([DayOfWeek.Tuesday], everyWeeks: 2), 35, "noah@brightline.example", 140m, 90),
        new("Monthly deep clean", Recurrence.MonthlyOnWeekday(WeekOfMonth.Last, DayOfWeek.Friday), 70, "maya@brightline.example", 420m, 240),
    ];

    // Free plan, at 3 of 3 seats and near 50 open jobs: an agreement started today fills the room that is left, and its
    // page shows generation held at the plan limit (SPEC 8.3: the demo shows limit handling).
    private static readonly SeedAgreement[] HarborPineAgreements =
    [
        new("Weekly lawn care", Recurrence.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday]), 0, "leo@harborpine.example", 85m, 60),
    ];

    public static IReadOnlyList<string> Emails { get; } = [.. People.Select(u => u.Email)];

    /// <summary>The demo accounts, one per role, all in Brightline: what the "Try as …" buttons sign in as (US-61).</summary>
    public static IReadOnlyList<SeedPerson> DemoAccounts { get; } =
    [
        .. new[] { Role.Owner, Role.Admin, Role.Member, Role.Viewer }
            .Select(role => Brightline.People.First(p => p.Role == role && p.Email.EndsWith("@brightline.example", StringComparison.Ordinal))),
    ];

    public static async Task SeedIfEnabledAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        // The public demo always has its data; elsewhere the seed is opt-in.
        if (!configuration.GetValue("Seed:Enabled", false) && !services.GetRequiredService<DemoMode>().Enabled)
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

            // Demo accounts count as verified (SPEC-V2 4.1): the addresses are fictional and the demo sends no email.
            var user = new AppUser { UserName = seed.Email, Email = seed.Email, EmailConfirmed = true, DisplayName = seed.DisplayName, CreatedAt = now };
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
        foreach (var (organization, clients, archivedEvery, jobs, firstDay, span, agreements) in new[]
        {
            (Brightline, DemoClients.CleaningCompany(), 8, 150, -118, 147, BrightlineAgreements),
            (HarborPine, DemoClients.Landscaper(), 12, 70, -36, 70, HarborPineAgreements),
        })
        {
            if (await db.Organizations.AnyAsync(o => o.Slug == organization.Slug, cancellationToken))
            {
                continue;
            }

            var steps = DemoPlan.For(organization, clients, archivedEvery, jobs, firstDay, span, now, agreements);
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
            var agreementIds = new Dictionary<int, Guid>();
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
                var tenant = new TenantContext();
                if (step is GenerateVisitsStep)
                {
                    tenant.EnterAsSystem(organization.Id);
                }
                else
                {
                    tenant.Enter(organization.Id, actorId, role);
                }

                await using var context = new AppDbContext(options, tenant, clock);
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
                            plan.QuotedAmount,
                            plan.EstimatedMinutes);
                        var job = Job.Create(organization.Id, tracked.TakeJobNumber(), details, JobActor.From(actorId, role), names, step.At);
                        context.Jobs.Add(job);
                        jobIds[plan.Job] = job.Id;
                        break;
                    case MoveJobStep move:
                        (await context.Jobs.SingleAsync(j => j.Id == jobIds[move.Job], cancellationToken))
                            .TransitionTo(move.To, JobActor.From(actorId, role), move.Reason, step.At);
                        break;
                    case CreateAgreementStep create:
                        var home = await context.Organizations.SingleAsync(o => o.Id == organization.Id, cancellationToken);
                        var agreement = Agreement.Create(
                            organization.Id,
                            clientIds[create.Client],
                            AgreementTemplate.Validate(create.Plan.Title, null, ids[create.Plan.AssigneeEmail], JobPriority.Normal, create.Plan.QuotedAmount, create.Plan.EstimatedMinutes),
                            new Schedule(create.Plan.Recurrence, create.StartsOn, null, null),
                            home.TodayAt(step.At),
                            actorId,
                            names,
                            step.At);
                        context.Agreements.Add(agreement);
                        agreementIds[create.Agreement] = agreement.Id;
                        await Generator(context, tenant, clock).GenerateAsync(home, [agreement], cancellationToken);
                        await new RecurringWork(context).EnsureDailyGenerationAsync(organization.Id, VisitGenerator.NextDailyRun(home, step.At), cancellationToken);
                        break;
                    case CompleteVisitStep visit:
                        // Absent only when the plan's open-jobs limit left it out (Harbor & Pine is on Free): nothing to do then.
                        if (await context.Jobs.SingleOrDefaultAsync(j => j.AgreementId == agreementIds[visit.Agreement] && j.OccurrenceOn == visit.Date, cancellationToken) is not { } done)
                        {
                            break;
                        }

                        done.TransitionTo(JobStatus.InProgress, JobActor.From(actorId, role), null, step.At.AddHours(-2));
                        done.TransitionTo(JobStatus.Completed, JobActor.From(actorId, role), null, step.At);
                        break;
                    case GenerateVisitsStep:
                        var current = await context.Organizations.SingleAsync(o => o.Id == organization.Id, cancellationToken);
                        var active = await context.Agreements.Where(a => a.Status == AgreementStatus.Active).ToListAsync(cancellationToken);
                        await Generator(context, tenant, clock).GenerateAsync(current, active, cancellationToken);
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

    /// <summary>The application's generator over a seed context, so seeded visits follow the same rules as real ones.</summary>
    private VisitGenerator Generator(AppDbContext context, TenantContext tenant, TimeProvider clock) =>
        new(context, new PlanLimitsCheck(context, tenant, clock), new JobViews(context, tenant, identity, clock), clock);

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
