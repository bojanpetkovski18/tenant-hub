using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Notifications;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;
using TenantHub.Infrastructure.Background;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Notifications;

/// <summary>
/// The daily digest of overdue jobs (US-100, SPEC-V2 5.7): 07:00 in the organization's zone, daylight-saving days
/// included; each assignee their jobs, managers a count; once a day however many schedulers run it; nothing when
/// nothing is overdue; and the same run keeps notifications for 90 days.
/// </summary>
public sealed class DigestTests(PostgresFixture postgres)
{
    // Friday 26 March 2027, noon UTC: Amsterdam moves its clocks forward on Sunday the 28th.
    private static readonly DateTimeOffset Friday = DateTimeOffset.Parse("2027-03-26T12:00:00Z", CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private async Task<(ApiTenants Tenants, MovingClock Clock, CapturedEmails Emails)> CreateAsync()
    {
        var clock = new MovingClock(Friday);
        var emails = new CapturedEmails();
        var tenants = await ApiTenants.CreateAsync(postgres, s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IEmailSender>(emails);
        });
        await tenants.Factory.DispatchOutboxAsync();
        await NotificationTests.ClearNotificationsAsync(tenants.ConnectionString);
        return (tenants, clock, emails);
    }

    [Fact]
    public async Task The_digest_runs_at_seven_local_through_the_clock_change_and_follows_a_new_zone()
    {
        var (tenants, clock, _) = await CreateAsync();
        await using var _ = tenants;

        // 07:00 CET is 06:00 UTC; from Sunday 07:00 CEST is 05:00 UTC.
        Assert.Equal(Utc("2027-03-27T06:00:00Z"), await DueAsync(tenants));
        clock.Now = Utc("2027-03-27T06:00:30Z");
        await RunAsync(tenants.Factory);
        Assert.Equal(Utc("2027-03-28T05:00:00Z"), await DueAsync(tenants));
        clock.Now = Utc("2027-03-28T05:00:30Z");
        await RunAsync(tenants.Factory);
        Assert.Equal(Utc("2027-03-29T05:00:00Z"), await DueAsync(tenants));

        // A new zone moves it to 07:00 there: New York is already on daylight time (UTC-4).
        var owner = await tenants.ClientForAsync(Role.Owner);
        (await owner.PatchJsonAsync($"/api/orgs/{tenants.OrgA}", new { name = "Alpha Cleaning", timeZone = "America/New_York" })).EnsureSuccessStatusCode();
        Assert.Equal(Utc("2027-03-28T11:00:00Z"), await DueAsync(tenants));
    }

    [Fact]
    public async Task Assignees_get_their_overdue_jobs_and_managers_a_count_once_a_day_with_two_schedulers()
    {
        var (tenants, clock, emails) = await CreateAsync();
        await using var _ = tenants;
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        await CreateJobAsync(tenants, owner, "Late window clean", memberId, new DateOnly(2027, 3, 20));
        await CreateJobAsync(tenants, owner, "Nobody's job", null, new DateOnly(2027, 3, 24));
        await CreateJobAsync(tenants, owner, "Due today", memberId, new DateOnly(2027, 3, 27));
        await tenants.Factory.DispatchOutboxAsync();
        var before = emails.Sent.Count;
        await using var second = new ApiFactory(tenants.ConnectionString, services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IEmailSender>(emails);
        });

        clock.Now = Utc("2027-03-27T06:01:00Z");
        await Task.WhenAll(Task.Run(() => RunAsync(tenants.Factory), Ct), Task.Run(() => RunAsync(second), Ct));
        await RunAsync(tenants.Factory); // again the same morning: already done
        await tenants.Factory.DispatchOutboxAsync();
        await second.DispatchOutboxAsync();

        var memberInbox = await DigestsAsync(tenants, member);
        var adminInbox = await DigestsAsync(tenants, await tenants.ClientForAsync(Role.Admin));
        var viewerInbox = await DigestsAsync(tenants, await tenants.ClientForAsync(Role.Viewer));
        Assert.Equal(["You have 1 overdue job."], memberInbox.Select(i => i.Text));
        Assert.Equal(["2 jobs are overdue in Alpha Cleaning."], adminInbox.Select(i => i.Text));
        Assert.Empty(viewerInbox);
        Assert.Equal(NotificationLink.OverdueJobs, memberInbox[0].Link);

        // Alpha's: the member, the Admin and both Owners (Bravo's digest runs on the same schedulers).
        var digests = emails.Sent.Skip(before).Where(m => m.Subject.StartsWith("Alpha Cleaning: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, digests.Count);
        Assert.All(digests, m => Assert.Contains("overdue", m.Subject, StringComparison.Ordinal));
        var mine = digests.Single(m => m.To == tenants.EmailsA[Role.Member]);
        Assert.Contains("“Late window clean”, due Sat 20 Mar", mine.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Due today", mine.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.tenanthub.example/o/alpha-cleaning/jobs?overdue=true", mine.TextBody, StringComparison.Ordinal);

        // The next morning is another day's digest.
        clock.Now = Utc("2027-03-28T05:01:00Z");
        await RunAsync(tenants.Factory);
        Assert.Equal(2, (await DigestsAsync(tenants, member)).Count);
    }

    [Fact]
    public async Task Nothing_overdue_sends_nothing_and_old_notifications_are_removed()
    {
        var (tenants, clock, emails) = await CreateAsync();
        await using var _ = tenants;
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var old = await tenants.CreateNotificationAsync(tenants.OrgA, memberId); // written today, months before the moved clock
        var before = emails.Sent.Count;

        clock.Now = Friday.AddDays(91).AddHours(-6);
        await RunAsync(tenants.Factory);
        await tenants.Factory.DispatchOutboxAsync();

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.False(await db.Notifications.AcrossTenants().AnyAsync(n => n.Id == old, Ct), "older than 90 days");
        Assert.Equal(0, await db.Notifications.AcrossTenants().CountAsync(n => n.OrganizationId == tenants.OrgA && n.Type == NotificationType.OverdueDigest, Ct));
        Assert.DoesNotContain(emails.Sent.Skip(before), m => m.Subject.StartsWith("Alpha Cleaning: ", StringComparison.Ordinal));
        Assert.True(await DueAsync(tenants) > clock.Now, "the next run is still planned");
    }

    private static async Task<DateTimeOffset> DueAsync(ApiTenants tenants)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.ScheduledTasks.AsNoTracking()
            .Where(t => t.OrganizationId == tenants.OrgA && t.Name == OverdueDigest.TaskName)
            .Select(t => t.DueAt)
            .SingleAsync(Ct);
    }

    private static Task<int> RunAsync(ApiFactory factory) => factory.Services.GetRequiredService<Scheduler>().RunDueAsync(Ct);

    private static async Task CreateJobAsync(ApiTenants tenants, BrowserClient client, string title, Guid? assignee, DateOnly dueOn) =>
        (await client.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs", new { title, clientId = tenants.ClientA, assigneeUserId = assignee, priority = "normal", dueOn }))
            .EnsureSuccessStatusCode();

    private static async Task<List<NotificationView>> DigestsAsync(ApiTenants tenants, BrowserClient client) =>
        [.. (await client.Http.GetFromJsonAsync<NotificationList>($"/api/orgs/{tenants.OrgA}/notifications", BrowserClient.Json, Ct))!.Items
            .Where(i => i.Type == NotificationType.OverdueDigest)];
}
