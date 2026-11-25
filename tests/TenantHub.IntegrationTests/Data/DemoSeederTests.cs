using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Data;

/// <summary>SPEC 8.3: the demo data, its history, idempotency and determinism.</summary>
public sealed class DemoSeederTests(PostgresFixture postgres)
{
    private const string Password = "Seed-Password-77";
    private static readonly DateTimeOffset Today = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Settings => new() { ["Seed:Enabled"] = "true", [DemoSeeder.PasswordSetting] = Password };

    private async Task<string> SeedAtAsync(DateTimeOffset now, string? connectionString = null)
    {
        connectionString ??= await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, Settings, services => services.AddSingleton<TimeProvider>(new FixedClock(now)));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        return connectionString;
    }

    [Fact]
    public async Task Seed_creates_both_organizations_as_specified_and_is_idempotent()
    {
        var connectionString = await SeedAtAsync(Today);
        await SeedAtAsync(Today.AddHours(5), connectionString);

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var harbor = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.HarborPineSlug, Ct);
        Assert.Equal(2, await db.Organizations.CountAsync(Ct));
        Assert.True(await db.Organizations.AllAsync(o => o.IsDemoSeed, Ct));
        Assert.Equal(8, await db.Users.CountAsync(Ct));
        Assert.Equal(8, await db.Users.CountAsync(u => u.EmailConfirmed, Ct)); // demo accounts count as verified (SPEC-V2 4.1)
        Assert.All(DemoSeeder.Emails, email => Assert.EndsWith(".example", email, StringComparison.Ordinal));

        // Brightline: Team, six members, forty clients (five archived), 150 planned jobs in every status, and three
        // agreements whose visits are numbered in the same sequence (M16).
        Assert.Equal((Plan.Team, 6), (brightline.Plan, await Members(db, brightline.Id)));
        Assert.Equal((40, 5), (await Clients(db, brightline.Id, null), await Clients(db, brightline.Id, ClientStatus.Archived)));
        var brightJobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == brightline.Id).ToListAsync(Ct);
        Assert.Equal(Enumerable.Range(1, brightJobs.Count), brightJobs.Select(j => j.Number).Order());
        Assert.Equal(150, brightJobs.Count(j => j.AgreementId is null));
        Assert.All(Enum.GetValues<JobStatus>(), status => Assert.Contains(brightJobs, j => j.Status == status));
        var today = Organization.TodayIn(brightline.TimeZone, Today);
        Assert.Contains(brightJobs, j => j.IsOverdue(today));
        Assert.Contains(brightJobs, j => j.DueOn > today.AddDays(14) && j.Status.IsOpen());
        Assert.Contains(brightJobs, j => j.ScheduledOn < today.AddDays(-100));

        // Agreements: past visits done by their assignee, the rest scheduled up to 60 days ahead, by the system.
        var agreements = await db.Agreements.AcrossTenants().AsNoTracking().Where(a => a.OrganizationId == brightline.Id).ToListAsync(Ct);
        Assert.Equal(3, agreements.Count);
        Assert.All(agreements, a => Assert.Equal((AgreementStatus.Active, today.AddDays(60)), (a.Status, a.GeneratedThrough!.Value)));
        var visits = brightJobs.Where(j => j.AgreementId is not null).ToList();
        Assert.All(visits.Where(v => v.OccurrenceOn < today), v => Assert.Equal(JobStatus.Completed, v.Status));
        Assert.All(visits.Where(v => v.OccurrenceOn >= today), v => Assert.Equal(JobStatus.Scheduled, v.Status));
        Assert.Contains(visits, v => v.OccurrenceOn > today.AddDays(50));

        // Harbor & Pine: Free and close to every limit (SPEC 8.3, "to show limit handling").
        var harborJobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == harbor.Id).ToListAsync(Ct);
        var open = harborJobs.Count(j => j.Status.IsOpen());
        Assert.Equal((Plan.Free, 3), (harbor.Plan, await Members(db, harbor.Id)));
        Assert.Equal(22, await Clients(db, harbor.Id, ClientStatus.Active));
        Assert.InRange(open, 40, 50);
        Assert.Equal(Enumerable.Range(1, harborJobs.Count), harborJobs.Select(j => j.Number).Order());
        // Its agreement, created last, filled the room that was left and is held at the limit (shown on its page).
        var lawnCare = await db.Agreements.AcrossTenants().AsNoTracking().SingleAsync(a => a.OrganizationId == harbor.Id, Ct);
        Assert.Equal((50, GenerationHold.PlanLimit), (open, lawnCare.PausedReason!.Value));

        // The one person in both organizations, and no audit entries beyond the seed's own history.
        var sam = await db.Users.SingleAsync(u => u.Email == "sam@tenanthub.example", Ct);
        Assert.Equal(2, await db.Memberships.AcrossTenants().CountAsync(m => m.UserId == sam.Id, Ct));
    }

    [Fact]
    public async Task The_history_agrees_with_the_data_and_was_made_by_the_people_who_would_do_it()
    {
        var connectionString = await SeedAtAsync(Today);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking().Where(j => j.OrganizationId == brightline.Id).ToListAsync(Ct);
        var audit = await db.AuditEntries.AcrossTenants().AsNoTracking().Where(a => a.OrganizationId == brightline.Id).ToListAsync(Ct);

        // Every entry is in the past, spread over months, and each job's creation entry is at its created_at.
        Assert.All(audit, a => Assert.True(a.OccurredAt <= Today));
        Assert.True(audit.Min(a => a.OccurredAt) < Today.AddDays(-100));
        var created = audit.Where(a => a.Action == "job.created").ToDictionary(a => a.EntityId, a => a.OccurredAt);
        Assert.All(jobs, j => Assert.Equal(j.CreatedAt, created[j.Id]));

        // Completed jobs: completed_at is the moment of the move to Completed, made by the assignee.
        var names = await db.Users.ToDictionaryAsync(u => u.Id, u => u.DisplayName, Ct);
        foreach (var job in jobs.Where(j => j.Status == JobStatus.Completed))
        {
            var completion = audit.Single(a => a.EntityId == job.Id && a.Action == "job.status_changed" && a.Summary.EndsWith("to Completed", StringComparison.Ordinal));
            Assert.Equal(job.CompletedAt, completion.OccurredAt);
            Assert.Equal(names[job.AssigneeUserId!.Value], completion.ActorName);
        }

        // Numbers follow creation time; several people did the work.
        Assert.Equal(jobs.OrderBy(j => j.CreatedAt).ThenBy(j => j.Number).Select(j => j.Number), jobs.OrderBy(j => j.Number).Select(j => j.Number));
        Assert.True(audit.Select(a => a.ActorName).Distinct().Count() >= 4, string.Join(", ", audit.Select(a => a.ActorName).Distinct()));
        Assert.Contains(audit, a => a.Action == "client.updated");
        Assert.Contains(audit, a => a.Action == "client.archived");
        Assert.Contains(audit, a => a.Action == "job.status_changed" && a.Summary.StartsWith("Cancelled JOB-", StringComparison.Ordinal) && a.Summary.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recent_completed_work_has_its_proof_and_two_contacts_have_signed_off()
    {
        // M19: visits carry their agreement's checklist, completed work of the last three weeks has a generated photo,
        // and two of the three portal contacts answered.
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, Settings, services => services.AddSingleton<TimeProvider>(new FixedClock(Today)));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);

        await using var db = PostgresFixture.CreateDbContext(connectionString);
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);
        var harbor = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.HarborPineSlug, Ct);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking().Include(j => j.Checklist).Where(j => j.OrganizationId == brightline.Id).ToListAsync(Ct);
        var photos = await db.Attachments.AcrossTenants().AsNoTracking().Where(a => a.OrganizationId == brightline.Id).ToListAsync(Ct);

        var visits = jobs.Where(j => j.AgreementId is not null).ToList();
        Assert.All(visits, v => Assert.Equal(4, v.Checklist.Count));
        Assert.All(visits.Where(v => v.Status == JobStatus.Completed), v => Assert.All(v.Checklist, i => Assert.True(i.IsDone)));
        Assert.All(visits.Where(v => v.Status != JobStatus.Completed), v => Assert.All(v.Checklist, i => Assert.False(i.IsDone)));
        Assert.All(jobs.Where(j => j.Status == JobStatus.Completed), j => Assert.Null(Checklists.WhyNotComplete(j.Checklist)));

        Assert.InRange(photos.Count, 5, 60);
        var completedAt = jobs.ToDictionary(j => j.Id, j => j.CompletedAt);
        Assert.All(photos, p => Assert.True(completedAt[p.JobId] >= Today.AddDays(-21), "a photo on old or unfinished work"));
        Assert.All(photos, p =>
        {
            Assert.True(File.Exists(Path.Combine(factory.FilesRoot, p.StorageKey[..32], p.StorageKey[33..])));
            Assert.True(File.Exists(Path.Combine(factory.FilesRoot, p.ThumbnailKey[..32], p.ThumbnailKey[33..])));
            Assert.True(p.Width <= 2560 && p.Height <= 2560);
        });
        Assert.Equal(0, await db.Attachments.AcrossTenants().CountAsync(a => a.OrganizationId == harbor.Id, Ct));

        Assert.Equal(
            [ClientFeedback.Confirmed, ClientFeedback.ProblemReported],
            jobs.Where(j => j.ClientFeedback is not null).Select(j => j.ClientFeedback!.Value).Order());
        var signOffs = await db.AuditEntries.AcrossTenants().AsNoTracking()
            .Where(a => a.OrganizationId == brightline.Id && (a.Action == "job.client_confirmed" || a.Action == "job.client_reported_problem"))
            .ToListAsync(Ct);
        Assert.Equal(2, signOffs.Count);
        Assert.All(signOffs, a => Assert.Equal(TenantHub.Domain.ActorKind.Contact, a.ActorKind));
    }

    [Fact]
    public async Task Two_seeds_on_the_same_day_produce_the_same_organizations()
    {
        var first = await SeedAtAsync(Today);
        var second = await SeedAtAsync(Today);

        Assert.Equal(await DemoFingerprint.OfAsync(first), await DemoFingerprint.OfAsync(second));
    }

    [Fact]
    public async Task Seeded_users_can_sign_in_and_the_shared_user_sees_both_organizations()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), Settings);
        using var browser = new BrowserClient(factory);

        (await browser.SignInAsync("sam@tenanthub.example", Password)).EnsureSuccessStatusCode();

        var memberships = (await browser.SessionAsync()).User!.Memberships;
        Assert.Equal(
            [("brightline-cleaning-co", Role.Member), ("harbor-pine-landscaping", Role.Admin)],
            memberships.Select(m => (m.Slug, m.Role)));
    }

    [Fact]
    public async Task Seed_is_skipped_without_a_demo_password()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new ApiFactory(connectionString, new Dictionary<string, string?> { ["Seed:Enabled"] = "true" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        await using var db = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(0, await db.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task A_weak_demo_password_stops_startup_with_a_clear_message()
    {
        await using var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), new Dictionary<string, string?>
        {
            ["Seed:Enabled"] = "true",
            [DemoSeeder.PasswordSetting] = "weak",
        });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("could not be created", error.Message, StringComparison.Ordinal);
    }

    private static Task<int> Members(AppDbContext db, Guid org) =>
        db.Memberships.AcrossTenants().CountAsync(m => m.OrganizationId == org && m.RemovedAt == null, Ct);

    private static Task<int> Clients(AppDbContext db, Guid org, ClientStatus? status) =>
        db.Clients.AcrossTenants().CountAsync(c => c.OrganizationId == org && (status == null || c.Status == status), Ct);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
