using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Jobs;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// US-83 through the API (SPEC-V2 5.4): staff send a quote, work cannot start until the contact approves, the decision
/// is made once (even by two contacts at the same moment), and changing the amount asks again.
/// </summary>
public sealed class QuoteTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Work_starts_only_after_the_contact_approves_and_staff_see_who_decided()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var (owner, jobUrl) = Staff(tenants, tenants.A1);
        using var contact = await tenants.SignInAsync(tenants.A1);

        var pending = await JobAsync(owner, jobUrl);
        var early = await owner.PostJsonAsync($"{jobUrl}/transitions", new { to = "inProgress" });
        var quotes = await contact.Http.GetFromJsonAsync<JsonElement>("/api/portal/quotes", Ct);
        var approved = await contact.PortalSendAsync(HttpMethod.Post, $"/api/portal/quotes/{tenants.A1.JobId}/approve", new { comment = "Please use the side entrance" }, tenants.A1.Slug);
        var decided = await JobAsync(owner, jobUrl);
        var started = await owner.PostJsonAsync($"{jobUrl}/transitions", new { to = "inProgress" });

        Assert.Equal(JobApprovalStatus.Pending, pending.Quote.Status);
        Assert.Equal("The client has not approved the quote yet.", pending.Transitions.Single(t => t.To == JobStatus.InProgress).BlockedReason);
        await ProblemAssert.HasCodeAsync(early, HttpStatusCode.Conflict, "quote-not-approved");
        var listed = Assert.Single(quotes.EnumerateArray());
        Assert.Equal((180m, "pending"), (listed.GetProperty("amount").GetDecimal(), listed.GetProperty("status").GetString()));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(
            (JobApprovalStatus.Approved, (decimal?)180m, $"{tenants.A1.Name} Contact", "Please use the side entrance"),
            (decided.Quote.Status, decided.Quote.ApprovedAmount, decided.Quote.DecidedByName, decided.Quote.Comment));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
    }

    [Fact]
    public async Task Changing_the_amount_after_approval_asks_again_and_the_log_shows_both()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var (owner, jobUrl) = Staff(tenants, tenants.A1);
        using var contact = await tenants.SignInAsync(tenants.A1);
        (await contact.PortalSendAsync(HttpMethod.Post, $"/api/portal/quotes/{tenants.A1.JobId}/approve", new { }, tenants.A1.Slug)).EnsureSuccessStatusCode();
        var job = await JobAsync(owner, jobUrl);

        var updated = await owner.SendJsonAsync(HttpMethod.Put, jobUrl, new
        {
            title = job.Title,
            clientId = job.ClientId,
            assigneeUserId = job.AssigneeUserId,
            priority = "normal",
            scheduledOn = job.ScheduledOn,
            dueOn = job.DueOn,
            quotedAmount = 210m,
            estimatedMinutes = job.EstimatedMinutes,
            version = job.Version,
        });
        var after = await JobAsync(owner, jobUrl);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(JobApprovalStatus.NotRequested, after.Quote.Status);
        Assert.True(after.Quote.CanSend);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var log = await db.AuditEntries.AcrossTenants()
            .Where(a => a.EntityId == tenants.A1.JobId && a.Action.StartsWith("job.quote_"))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.ActorKind })
            .ToListAsync(Ct);
        Assert.Equal(
            [("job.quote_sent", ActorKind.User), ("job.quote_approved", ActorKind.Contact), ("job.quote_reset", ActorKind.User)],
            log.Select(l => (l.Action, l.ActorKind)));
    }

    [Fact]
    public async Task Two_contacts_deciding_at_once_make_one_decision()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var (owner, _) = Staff(tenants, tenants.A1);
        var second = tenants.A1 with { ContactEmail = $"second-{Guid.NewGuid():N}@client.example" };
        (await owner.PostJsonAsync($"/api/orgs/{second.OrganizationId}/clients/{second.ClientId}/contacts", new { name = "Second Contact", email = second.ContactEmail }))
            .EnsureSuccessStatusCode();
        using var first = await tenants.SignInAsync(tenants.A1);
        using var other = await tenants.SignInAsync(second);

        var answers = await Task.WhenAll(
            first.PortalSendAsync(HttpMethod.Post, $"/api/portal/quotes/{tenants.A1.JobId}/approve", new { }, tenants.A1.Slug),
            other.PortalSendAsync(HttpMethod.Post, $"/api/portal/quotes/{tenants.A1.JobId}/decline", new { comment = "Not now" }, tenants.A1.Slug));

        var statuses = answers.Select(a => a.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Single(statuses, s => s == HttpStatusCode.OK);
        await ProblemAssert.HasCodeAsync(answers.Single(a => a.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "quote-already-decided");
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        Assert.Equal(1, await db.AuditEntries.AcrossTenants().CountAsync(
            a => a.EntityId == tenants.A1.JobId && (a.Action == "job.quote_approved" || a.Action == "job.quote_declined"), Ct));
    }

    [Fact]
    public async Task On_free_a_quote_cannot_be_sent()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var (owner, jobUrl) = Staff(tenants, tenants.B1);
        (await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{tenants.B1.OrganizationId}/plan", new { plan = "free", confirmFeatureLoss = true }))
            .EnsureSuccessStatusCode();

        var refused = await owner.PostJsonAsync($"{jobUrl}/quote", new { });
        var job = await JobAsync(owner, jobUrl);

        await ProblemAssert.HasCodeAsync(refused, HttpStatusCode.Forbidden, "plan-feature-unavailable");
        Assert.Equal((false, "Quotes for approval come with the client portal, part of the Team plan."), (job.Quote.CanSend, job.Quote.CannotSend));
    }

    private static (BrowserClient Owner, string JobUrl) Staff(PortalTenants tenants, PortalClient client) =>
        (tenants.OwnerOf(client.OrganizationId), $"/api/orgs/{client.OrganizationId}/jobs/{client.JobId}");

    private static async Task<JobView> JobAsync(BrowserClient owner, string url) =>
        (await owner.Http.GetFromJsonAsync<JobView>(url, BrowserClient.Json, Ct))!;
}
