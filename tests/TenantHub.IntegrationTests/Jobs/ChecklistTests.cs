using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Jobs;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Jobs;

/// <summary>
/// A job's checklist through the API (US-90, SPEC-V2 5.6): the Completed precondition with the open items, who may
/// change it, reordering, and a completion that can never race an untick.
/// </summary>
public sealed class ChecklistTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_completes_only_once_every_required_item_is_ticked()
    {
        var tenants = await SharedApiTenants.GetAsync<ChecklistTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        var url = JobUrl(tenants, job);
        await AddAsync(member, url, "Vacuum the offices", required: true);
        await AddAsync(member, url, "Water the plants", required: false);
        var list = await AddAsync(member, url, "Empty the bins", required: true);
        (await member.PostJsonAsync($"{url}/transitions", new { to = "inProgress" })).EnsureSuccessStatusCode();

        var refused = await member.PostJsonAsync($"{url}/transitions", new { to = "completed" });
        var page = await member.Http.GetFromJsonAsync<JobView>(url, BrowserClient.Json, Ct);
        foreach (var item in list.Items.Where(i => i.Required))
        {
            (await member.SendJsonAsync(HttpMethod.Put, $"{url}/checklist/{item.Id}", new { done = true })).EnsureSuccessStatusCode();
        }

        var completed = await member.PostJsonAsync($"{url}/transitions", new { to = "completed" });

        var problem = await ProblemAssert.IsProblemAsync(refused, HttpStatusCode.Conflict);
        Assert.Equal("checklist-incomplete", problem.GetProperty("code").GetString());
        Assert.Equal(
            ["Vacuum the offices", "Empty the bins"],
            problem.GetProperty("openItems").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(
            "Tick the required checklist items first: Vacuum the offices; Empty the bins.",
            page!.Transitions.Single(t => t.To == JobStatus.Completed).BlockedReason);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var after = await member.Http.GetFromJsonAsync<ChecklistView>($"{url}/checklist", BrowserClient.Json, Ct);
        Assert.False(after!.CanEdit);
        Assert.Equal("JOB-" + page.Number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + " is Completed. Reopen it to change its checklist.", after.ReadOnlyReason);
        Assert.All(after.Items.Where(i => i.Required), i => Assert.True(i.Done && i.DoneAt is not null && i.DoneByName is not null));
    }

    [Fact]
    public async Task Items_are_edited_and_reordered_by_whoever_works_on_the_job_and_every_change_is_in_its_history()
    {
        var tenants = await SharedApiTenants.GetAsync<ChecklistTests>(postgres);
        var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
        var member = await tenants.ClientForAsync(Role.Member);
        var admin = await tenants.ClientForAsync(Role.Admin);
        var viewer = await tenants.ClientForAsync(Role.Viewer);
        var url = JobUrl(tenants, job);
        await AddAsync(member, url, "First");
        await AddAsync(member, url, "Second");
        var three = (await AddAsync(member, url, "Third")).Items[2];

        var moved = await member.SendJsonAsync(HttpMethod.Put, $"{url}/checklist/{three.Id}", new { position = 0, text = "Third, now first" });
        var otherJob = await tenants.CreateJobAsync(tenants.OrgA, Role.Admin);
        var notMine = await member.PostJsonAsync($"{JobUrl(tenants, otherJob)}/checklist", new { text = "Sneaky" });
        var viewing = await viewer.PostJsonAsync($"{url}/checklist", new { text = "Sneaky" });
        var invalid = await admin.PostJsonAsync($"{url}/checklist", new { text = "   " });
        var history = await member.Http.GetFromJsonAsync<List<JobHistoryEntry>>($"{url}/history", BrowserClient.Json, Ct);

        var view = (await moved.Content.ReadFromJsonAsync<ChecklistView>(BrowserClient.Json, Ct))!;
        Assert.Equal(["Third, now first", "First", "Second"], view.Items.Select(i => i.Text));
        Assert.Equal([0, 1, 2], view.Items.Select(i => i.Position));
        await ProblemAssert.HasCodeAsync(notMine, HttpStatusCode.Forbidden, "job-not-yours");
        Assert.Equal(HttpStatusCode.Forbidden, viewing.StatusCode);
        await ProblemAssert.HasFieldErrorsAsync(invalid, "text");
        Assert.Equal(
            ["job.checklist_item_moved", "job.checklist_item_changed", "job.checklist_item_added", "job.checklist_item_added", "job.checklist_item_added"],
            history!.Where(h => h.Action.StartsWith("job.checklist", StringComparison.Ordinal)).Select(h => h.Action));
    }

    [Fact]
    public async Task Unticking_and_completing_at_once_never_leave_a_completed_job_with_an_open_required_item()
    {
        var tenants = await SharedApiTenants.GetAsync<ChecklistTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);
        var admin = await tenants.ClientForAsync(Role.Admin);
        for (var round = 0; round < 8; round++)
        {
            var job = await tenants.CreateJobAsync(tenants.OrgA, Role.Member);
            var url = JobUrl(tenants, job);
            var item = (await AddAsync(member, url, "Lock the door", required: true)).Items[0];
            (await member.SendJsonAsync(HttpMethod.Put, $"{url}/checklist/{item.Id}", new { done = true })).EnsureSuccessStatusCode();
            (await member.PostJsonAsync($"{url}/transitions", new { to = "inProgress" })).EnsureSuccessStatusCode();

            await Task.WhenAll(
                member.SendJsonAsync(HttpMethod.Put, $"{url}/checklist/{item.Id}", new { done = false }),
                admin.PostJsonAsync($"{url}/transitions", new { to = "completed" }));

            await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
            var saved = await db.Jobs.AcrossTenants().AsNoTracking().Include(j => j.Checklist).SingleAsync(j => j.Id == job, Ct);
            Assert.False(
                saved.Status == JobStatus.Completed && saved.Checklist.Any(i => i.Required && !i.IsDone),
                $"round {round}: completed with the required item unticked");
        }
    }

    [Fact]
    public async Task An_agreements_checklist_is_copied_into_its_visits()
    {
        var tenants = await SharedApiTenants.GetAsync<ChecklistTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var org = $"/api/orgs/{tenants.OrgA}";

        var created = await owner.PostJsonAsync($"{org}/agreements", new
        {
            clientId = tenants.ClientA,
            title = $"Weekly clean {Guid.NewGuid():N}",
            recurrence = new { kind = "weekly", interval = 1, weekdays = new[] { "tuesday" } },
            startsOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            checklist = new[] { new { text = "Vacuum", required = true }, new { text = "Water the plants", required = false } },
        });
        var invalid = await owner.PostJsonAsync($"{org}/agreements", new
        {
            clientId = tenants.ClientA,
            title = "Too long a list",
            recurrence = new { kind = "weekly", interval = 1, weekdays = new[] { "tuesday" } },
            startsOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            checklist = Enumerable.Range(1, 31).Select(i => new { text = $"Item {i}", required = false }),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var agreement = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(
            [("Vacuum", true), ("Water the plants", false)],
            agreement.GetProperty("checklist").EnumerateArray().Select(e => (e.GetProperty("text").GetString(), e.GetProperty("required").GetBoolean())));
        var visit = agreement.GetProperty("upcomingVisits")[0].GetProperty("id").GetGuid();
        var checklist = await owner.Http.GetFromJsonAsync<ChecklistView>($"{org}/jobs/{visit}/checklist", BrowserClient.Json, Ct);
        Assert.Equal([("Vacuum", true), ("Water the plants", false)], checklist!.Items.Select(i => (i.Text, i.Required)));
        await ProblemAssert.HasFieldErrorsAsync(invalid, "checklist");
    }

    private static string JobUrl(ApiTenants tenants, Guid job) => $"/api/orgs/{tenants.OrgA}/jobs/{job}";

    private static async Task<ChecklistView> AddAsync(BrowserClient client, string url, string text, bool required = false)
    {
        var response = await client.PostJsonAsync($"{url}/checklist", new { text, required });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChecklistView>(BrowserClient.Json, Ct))!;
    }
}
