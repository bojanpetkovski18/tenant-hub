using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Notifications;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Notifications;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;
using TenantHub.IntegrationTests.Portal;

namespace TenantHub.IntegrationTests.Notifications;

/// <summary>
/// US-100 and US-101 through the API (SPEC-V2 5.7): who hears about what, in the app and by email; never the actor,
/// never a former member; nothing from a change that did not happen; preferences per type; each person's own list.
/// </summary>
public sealed class NotificationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTenants Tenants, CapturedEmails Emails)> CreateAsync()
    {
        var emails = new CapturedEmails();
        var tenants = await ApiTenants.CreateAsync(postgres, s => s.AddSingleton<IEmailSender>(emails));
        // The fixture's own setup notifies too (its standing job is assigned): start each test from an empty inbox.
        await tenants.Factory.DispatchOutboxAsync();
        await ClearNotificationsAsync(tenants.ConnectionString);
        return (tenants, emails);
    }

    [Fact]
    public async Task Assigning_a_job_notifies_the_assignee_in_the_app_and_by_email_and_nobody_else()
    {
        var (tenants, emails) = await CreateAsync();
        await using var _ = tenants;
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var before = emails.Sent.Count;

        var job = await CreateJobAsync(tenants, owner, "Deep clean, ground floor", memberId);
        await tenants.Factory.DispatchOutboxAsync();

        var inbox = await InboxAsync(tenants, member);
        var item = Assert.Single(inbox.Items);
        Assert.Equal((NotificationType.Assigned, NotificationLink.Job, (Guid?)job, (DateTimeOffset?)null), (item.Type, item.Link, item.LinkId, item.ReadAt));
        Assert.Matches("^.+ assigned JOB-\\d{4} “Deep clean, ground floor” to you\\.$", item.Text);
        Assert.Equal(1, inbox.Unread);
        foreach (var role in new[] { Role.Owner, Role.Admin, Role.Viewer })
        {
            Assert.Empty((await InboxAsync(tenants, await tenants.ClientForAsync(role))).Items);
        }

        var email = Assert.Single(emails.Sent.Skip(before));
        Assert.Equal(tenants.EmailsA[Role.Member], email.To);
        Assert.StartsWith("Alpha Cleaning: ", email.Subject, StringComparison.Ordinal);
        Assert.Contains($"https://app.tenanthub.example/o/alpha-cleaning/jobs/{job}", email.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.tenanthub.example/account/notifications", email.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("#", email.TextBody, StringComparison.Ordinal); // no token, nothing to sign anyone in
        Assert.DoesNotContain("<img", email.HtmlBody, StringComparison.OrdinalIgnoreCase); // no tracking pixel
    }

    [Fact]
    public async Task Nobody_hears_about_their_own_action_and_a_former_member_hears_nothing()
    {
        var (tenants, emails) = await CreateAsync();
        await using var _ = tenants;
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var leaving = await tenants.CreateMemberAsync(tenants.OrgA);
        var before = emails.Sent.Count;

        // The member takes a job themselves; the owner assigns one to someone who is removed before delivery.
        await CreateJobAsync(tenants, member, "My own job", memberId);
        await CreateJobAsync(tenants, owner, "For someone leaving", leaving);
        (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/members/{leaving}", null)).EnsureSuccessStatusCode();
        await tenants.Factory.DispatchOutboxAsync();

        Assert.Empty((await InboxAsync(tenants, member)).Items);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.Equal(0, await db.Notifications.AcrossTenants().CountAsync(n => n.UserId == leaving || n.UserId == memberId, Ct));
        Assert.Empty(emails.Sent.Skip(before));
    }

    [Fact]
    public async Task A_change_that_did_not_happen_notifies_nobody()
    {
        var (tenants, emails) = await CreateAsync();
        await using var _ = tenants;
        var owner = await tenants.ClientForAsync(Role.Owner);
        var job = await CreateJobAsync(tenants, owner, "Unassigned for now", null);
        var details = await tenants.JobDetailsAsync(job);
        var before = emails.Sent.Count;

        // A stale version: the assignment is refused with 409, and its notification goes with it.
        var refused = await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{tenants.OrgA}/jobs/{job}", new
        {
            title = details.Title,
            clientId = details.ClientId,
            assigneeUserId = await tenants.UserIdOfAsync(Role.Member),
            priority = "normal",
            version = await tenants.JobVersionAsync(job) + 1000,
        });
        await tenants.Factory.DispatchOutboxAsync();

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty((await InboxAsync(tenants, await tenants.ClientForAsync(Role.Member))).Items);
        Assert.Empty(emails.Sent.Skip(before));
    }

    [Fact]
    public async Task Preferences_decide_the_email_and_never_the_notification()
    {
        var (tenants, emails) = await CreateAsync();
        await using var _ = tenants;
        var owner = await tenants.ClientForAsync(Role.Owner);
        var member = await tenants.ClientForAsync(Role.Member);
        var memberId = await tenants.UserIdOfAsync(Role.Member);

        var defaults = await member.Http.GetFromJsonAsync<List<NotificationPreferenceView>>("/api/me/notification-preferences", BrowserClient.Json, Ct);
        var saved = await member.SendJsonAsync(HttpMethod.Put, "/api/me/notification-preferences", new
        {
            preferences = new[] { new { type = "assigned", email = false }, new { type = "visitChanged", email = true } },
        });
        var before = emails.Sent.Count;
        var job = await CreateJobAsync(tenants, owner, "Window clean", memberId);
        var details = await tenants.JobDetailsAsync(job);
        (await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{tenants.OrgA}/jobs/{job}", new
        {
            title = details.Title,
            clientId = details.ClientId,
            assigneeUserId = memberId,
            priority = "normal",
            scheduledOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5),
            version = await tenants.JobVersionAsync(job),
        })).EnsureSuccessStatusCode();
        await tenants.Factory.DispatchOutboxAsync();

        // SPEC-V2 5.7: email on by default except for rescheduled or cancelled visits.
        Assert.Equal(
            NotificationTypes.All.Select(t => (t, t != NotificationType.VisitChanged)),
            defaults!.Select(p => (p.Type, p.Email)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var inbox = await InboxAsync(tenants, member);
        Assert.Equal([NotificationType.VisitChanged, NotificationType.Assigned], inbox.Items.Select(i => i.Type));
        Assert.Matches("moved JOB-\\d{4} “Window clean” to \\w{3} \\d{1,2} \\w{3}\\.$", inbox.Items[0].Text);
        var email = Assert.Single(emails.Sent.Skip(before));
        Assert.Contains("moved JOB-", email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_member_reads_and_marks_only_their_own()
    {
        var (tenants, _) = await CreateAsync();
        await using var __ = tenants;
        var member = await tenants.ClientForAsync(Role.Member);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var adminId = await tenants.UserIdOfAsync(Role.Admin);
        var first = await tenants.CreateNotificationAsync(tenants.OrgA, memberId);
        var second = await tenants.CreateNotificationAsync(tenants.OrgA, memberId);
        var admins = await tenants.CreateNotificationAsync(tenants.OrgA, adminId);
        var url = $"/api/orgs/{tenants.OrgA}/notifications";

        var marked = await member.PostJsonAsync($"{url}/read", new { ids = new[] { first, admins } });
        var adminInbox = await InboxAsync(tenants, admin);
        var empty = await member.PostJsonAsync($"{url}/read", new { });
        var all = await member.PostJsonAsync($"{url}/read", new { all = true });
        var memberInbox = await InboxAsync(tenants, member);

        Assert.Equal(1, (await marked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("unread").GetInt32());
        Assert.Equal((admins, (DateTimeOffset?)null), (Assert.Single(adminInbox.Items).Id, adminInbox.Items[0].ReadAt));
        await ProblemAssert.HasFieldErrorsAsync(empty, "ids");
        Assert.Equal(0, (await all.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("unread").GetInt32());
        Assert.Equal([second, first], memberInbox.Items.Select(i => i.Id)); // newest first
        Assert.All(memberInbox.Items, i => Assert.NotNull(i.ReadAt));
    }

    [Fact]
    public async Task Clients_actions_reach_the_managers_and_the_people_concerned()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        using var contact = await tenants.SignInAsync(tenants.A1);
        await ClearNotificationsAsync(tenants.ConnectionString); // the fixture's own requests notified the Owners
        var before = tenants.Emails.Sent.Count;

        (await contact.PortalSendAsync(HttpMethod.Post, $"/api/portal/quotes/{tenants.A1.JobId}/approve", new { }, tenants.A1.Slug)).EnsureSuccessStatusCode();
        (await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/requests", new { title = "Clean the loft" }, tenants.A1.Slug)).EnsureSuccessStatusCode();
        (await contact.PortalSendAsync(HttpMethod.Post, $"/api/portal/visits/{tenants.A1.VisitId}/report-problem", new { text = "Brass still dull" }, tenants.A1.Slug)).EnsureSuccessStatusCode();
        await tenants.Factory.DispatchOutboxAsync();

        // The Owner manages, sent the quote and is the visit's assignee: each change notifies them once.
        var inbox = (await owner.Http.GetFromJsonAsync<NotificationList>($"/api/orgs/{tenants.A1.OrganizationId}/notifications", BrowserClient.Json, Ct))!;
        Assert.Equal(
            [NotificationType.ProblemReported, NotificationType.WorkRequested, NotificationType.QuoteDecided],
            inbox.Items.Select(i => i.Type));
        Assert.Equal($"{tenants.A1.Name} Contact reported a problem with JOB-0002 “{tenants.A1.VisitTitle}”.", inbox.Items[0].Text);
        Assert.Equal($"{tenants.A1.Name} Contact of {tenants.A1.Name} asked for work: “Clean the loft”.", inbox.Items[1].Text);
        Assert.Equal($"{tenants.A1.Name} Contact approved the quote for JOB-0001 “{tenants.A1.JobTitle}”.", inbox.Items[2].Text);
        Assert.Equal(3, tenants.Emails.Sent.Skip(before).Count(m => m.Subject.StartsWith($"{tenants.A1.OrganizationName}: ", StringComparison.Ordinal)));
        // Nothing for another organization's people.
        var other = tenants.OwnerOf(tenants.B1.OrganizationId);
        Assert.Empty((await other.Http.GetFromJsonAsync<NotificationList>($"/api/orgs/{tenants.B1.OrganizationId}/notifications", BrowserClient.Json, Ct))!.Items);
    }

    [Fact]
    public async Task A_message_delivered_twice_notifies_once()
    {
        var (tenants, _) = await CreateAsync();
        await using var __ = tenants;
        var memberId = await tenants.UserIdOfAsync(Role.Member);
        var request = new NotificationRequest(NotificationType.Assigned, NotificationLink.Job, tenants.JobA, null, "Someone", [memberId], Managers: false);
        var source = Guid.NewGuid();

        for (var i = 0; i < 2; i++)
        {
            await using var scope = tenants.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NotificationFanOut>().DeliverAsync(source, tenants.OrgA, request, Ct);
        }

        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.Equal(1, await db.Notifications.AcrossTenants().CountAsync(n => n.SourceId == source, Ct));
    }

    internal static async Task ClearNotificationsAsync(string connectionString)
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresFixture.AdminConnectionStringFor(connectionString));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("DELETE FROM notifications", connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> CreateJobAsync(ApiTenants tenants, BrowserClient client, string title, Guid? assignee)
    {
        var response = await client.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs", new { title, clientId = tenants.ClientA, assigneeUserId = assignee, priority = "normal" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<NotificationList> InboxAsync(ApiTenants tenants, BrowserClient client) =>
        (await client.Http.GetFromJsonAsync<NotificationList>($"/api/orgs/{tenants.OrgA}/notifications", BrowserClient.Json, Ct))!;
}
