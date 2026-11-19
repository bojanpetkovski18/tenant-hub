using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TenantHub.Application.Jobs;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// US-84 through the API (SPEC-V2 5.5): a contact asks for work, staff see it in the inbox with a count, and convert it
/// into a job for that client or decline it with a reason, which the contact then reads. One decision per request.
/// </summary>
public sealed class WorkRequestFlowTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_becomes_a_job_for_the_contacts_client_and_the_contact_sees_it()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        var org = $"/api/orgs/{tenants.A1.OrganizationId}";
        using var contact = await tenants.SignInAsync(tenants.A1);

        var submitted = await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/requests", new { title = "Clean the stairwell", description = "All four floors", preferredOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5) }, tenants.A1.Slug);
        var request = (await submitted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var count = await owner.Http.GetFromJsonAsync<JsonElement>($"{org}/requests/open-count", Ct);
        var inbox = await owner.Http.GetFromJsonAsync<JsonElement>($"{org}/requests/?status=open", Ct);
        // The form may name another client; the job is still for the client who asked.
        var converted = await owner.PostJsonAsync($"{org}/requests/{request}/convert", new { title = "Stairwell clean", clientId = tenants.A2.ClientId, priority = "normal", quotedAmount = 95m });
        var job = await converted.Content.ReadFromJsonAsync<JobView>(BrowserClient.Json, Ct);
        var again = await owner.PostJsonAsync($"{org}/requests/{request}/decline", new { reason = "Changed my mind" });
        var followed = await contact.Http.GetFromJsonAsync<JsonElement>("/api/portal/requests", Ct);

        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        Assert.Equal(3, count.GetProperty("open").GetInt32()); // the organization's: both clients' standing requests and this one
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), r => r.GetProperty("title").GetString() == "Clean the stairwell"
            && r.GetProperty("contactName").GetString() == $"{tenants.A1.Name} Contact");
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        Assert.Equal((tenants.A1.ClientId, 95m), (job!.ClientId, job.QuotedAmount!.Value));
        await ProblemAssert.HasCodeAsync(again, HttpStatusCode.Conflict, "request-not-open");
        var mine = followed.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request);
        Assert.Equal(("converted", job.Label), (mine.GetProperty("status").GetString(), mine.GetProperty("jobLabel").GetString()));
    }

    [Fact]
    public async Task A_declined_request_shows_the_contact_why()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        using var contact = await tenants.SignInAsync(tenants.A1);

        var declined = await owner.PostJsonAsync(
            $"/api/orgs/{tenants.A1.OrganizationId}/requests/{tenants.A1.RequestId}/decline", new { reason = "We do not clean carpets any more." });
        var followed = await contact.Http.GetFromJsonAsync<JsonElement>("/api/portal/requests", Ct);

        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        var mine = followed.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == tenants.A1.RequestId);
        Assert.Equal(("declined", "We do not clean carpets any more."), (mine.GetProperty("status").GetString(), mine.GetProperty("declineReason").GetString()));
    }

    [Fact]
    public async Task A_contact_may_have_ten_open_requests_and_none_for_a_past_day()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);

        var past = await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/requests", new { title = "Yesterday please", preferredOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2) }, tenants.A1.Slug);
        for (var i = 2; i <= 10; i++)
        {
            (await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/requests", new { title = $"Request number {i}" }, tenants.A1.Slug)).EnsureSuccessStatusCode();
        }

        var eleventh = await contact.PortalSendAsync(HttpMethod.Post, "/api/portal/requests", new { title = "One too many" }, tenants.A1.Slug);

        await ProblemAssert.HasFieldErrorsAsync(past, "preferredOn");
        await ProblemAssert.HasCodeAsync(eleventh, HttpStatusCode.Conflict, "request-limit-reached");
    }

    [Fact]
    public async Task Deleting_the_job_keeps_the_request_converted()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        var org = $"/api/orgs/{tenants.A1.OrganizationId}";
        var job = await (await owner.PostJsonAsync($"{org}/requests/{tenants.A1.RequestId}/convert", new { title = "Carpet clean", priority = "normal" }))
            .Content.ReadFromJsonAsync<JobView>(BrowserClient.Json, Ct);

        var deleted = await owner.SendJsonAsync(HttpMethod.Delete, $"{org}/jobs/{job!.Id}", null);
        var inbox = await owner.Http.GetFromJsonAsync<JsonElement>($"{org}/requests/", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var request = inbox.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == tenants.A1.RequestId);
        Assert.Equal(("converted", JsonValueKind.Null), (request.GetProperty("status").GetString(), request.GetProperty("jobId").ValueKind));
    }

    [Fact]
    public async Task On_free_the_inbox_is_refused_and_counts_nothing()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        var owner = tenants.OwnerOf(tenants.B1.OrganizationId);
        var org = $"/api/orgs/{tenants.B1.OrganizationId}";
        (await owner.SendJsonAsync(HttpMethod.Put, $"{org}/plan", new { plan = "free", confirmFeatureLoss = true })).EnsureSuccessStatusCode();

        var inbox = await owner.Http.GetAsync($"{org}/requests/", Ct);
        var count = await owner.Http.GetFromJsonAsync<JsonElement>($"{org}/requests/open-count", Ct);

        await ProblemAssert.HasCodeAsync(inbox, HttpStatusCode.Forbidden, "plan-feature-unavailable");
        Assert.Equal(0, count.GetProperty("open").GetInt32());
    }
}
