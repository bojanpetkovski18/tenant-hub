using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TenantHub.Application.Jobs;
using TenantHub.Domain.Jobs;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// US-91 through the API (SPEC-V2 4.5, 5.6): the contact sees a completed visit's checklist and photos, confirms it or
/// reports a problem once, staff see the answer, and reopening the job asks again. Before completion there is no proof.
/// </summary>
public sealed class VisitSignOffTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_completed_visit_shows_its_proof_and_the_contact_confirms_it_once()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);
        var visit = $"/api/portal/visits/{tenants.A1.VisitId}";

        var before = await contact.Http.GetFromJsonAsync<JsonElement>(visit, Ct);
        var thumbnail = await contact.Http.GetAsync($"{visit}/photos/{tenants.A1.PhotoId}/thumbnail", Ct);
        var confirmed = await contact.PortalSendAsync(HttpMethod.Post, $"{visit}/confirm", null, tenants.A1.Slug);
        var again = await contact.PortalSendAsync(HttpMethod.Post, $"{visit}/report-problem", new { text = "Changed my mind" }, tenants.A1.Slug);

        Assert.Equal(
            [("Polish the brass", true, true)],
            before.GetProperty("checklist").EnumerateArray().Select(i => (i.GetProperty("text").GetString(), i.GetProperty("required").GetBoolean(), i.GetProperty("done").GetBoolean())));
        Assert.Equal(tenants.A1.PhotoId, Assert.Single(before.GetProperty("photos").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.True(before.GetProperty("canGiveFeedback").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var after = await confirmed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("confirmed", $"{tenants.A1.Name} Contact"), (after.GetProperty("feedback").GetProperty("kind").GetString(), after.GetProperty("feedback").GetProperty("byName").GetString()));
        Assert.False(after.GetProperty("canGiveFeedback").GetBoolean());
        await ProblemAssert.HasCodeAsync(again, HttpStatusCode.Conflict, "feedback-already-given");

        var staff = await tenants.OwnerOf(tenants.A1.OrganizationId).Http.GetFromJsonAsync<JobView>($"/api/orgs/{tenants.A1.OrganizationId}/jobs/{tenants.A1.VisitId}", BrowserClient.Json, Ct);
        Assert.Equal((ClientFeedback.Confirmed, $"{tenants.A1.Name} Contact"), (staff!.ClientFeedback!.Kind, staff.ClientFeedback.ByName));
    }

    [Fact]
    public async Task A_problem_is_shown_to_staff_and_reopening_the_job_clears_it()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);
        var owner = tenants.OwnerOf(tenants.A1.OrganizationId);
        var jobUrl = $"/api/orgs/{tenants.A1.OrganizationId}/jobs/{tenants.A1.VisitId}";
        var visit = $"/api/portal/visits/{tenants.A1.VisitId}";

        var empty = await contact.PortalSendAsync(HttpMethod.Post, $"{visit}/report-problem", new { text = "  " }, tenants.A1.Slug);
        var reported = await contact.PortalSendAsync(HttpMethod.Post, $"{visit}/report-problem", new { text = "The hallway was not mopped" }, tenants.A1.Slug);
        var shown = await owner.Http.GetFromJsonAsync<JobView>(jobUrl, BrowserClient.Json, Ct);
        var history = await owner.Http.GetFromJsonAsync<List<JobHistoryEntry>>($"{jobUrl}/history", BrowserClient.Json, Ct);
        (await owner.PostJsonAsync($"{jobUrl}/transitions", new { to = "inProgress", reason = "Client reported a problem" })).EnsureSuccessStatusCode();
        var reopened = await owner.Http.GetFromJsonAsync<JobView>(jobUrl, BrowserClient.Json, Ct);
        var hidden = await contact.Http.GetAsync($"{visit}/photos/{tenants.A1.PhotoId}", Ct);
        var early = await contact.PortalSendAsync(HttpMethod.Post, $"{visit}/confirm", null, tenants.A1.Slug);

        await ProblemAssert.HasFieldErrorsAsync(empty, "text");
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal((ClientFeedback.ProblemReported, "The hallway was not mopped"), (shown!.ClientFeedback!.Kind, shown.ClientFeedback.Text));
        Assert.Equal(JobStatus.Completed, shown.Status); // a problem does not reopen the job by itself
        Assert.Contains(history!, h => h.Action == "job.client_reported_problem" && h.ActorName == $"{tenants.A1.Name} Contact");
        Assert.Null(reopened!.ClientFeedback);
        // Reopened, the visit is work in progress again: its photos are not shown and it cannot be signed off.
        await ProblemAssert.HasCodeAsync(hidden, HttpStatusCode.NotFound, "visit-not-found");
        await ProblemAssert.HasCodeAsync(early, HttpStatusCode.Conflict, "visit-not-completed");
    }

    [Fact]
    public async Task Before_completion_a_visit_shows_no_proof()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);

        var open = await contact.Http.GetFromJsonAsync<JsonElement>($"/api/portal/visits/{tenants.A1.JobId}", Ct);

        Assert.Empty(open.GetProperty("checklist").EnumerateArray());
        Assert.Empty(open.GetProperty("photos").EnumerateArray());
        Assert.False(open.GetProperty("canGiveFeedback").GetBoolean());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("feedback").ValueKind);
    }

    [Fact]
    public async Task Another_clients_visit_and_photo_are_not_found()
    {
        await using var tenants = await PortalTenants.CreateAsync(postgres);
        using var contact = await tenants.SignInAsync(tenants.A1);

        // Their visit and photo, and their photo under our own visit.
        var theirs = await contact.Http.GetAsync($"/api/portal/visits/{tenants.A2.VisitId}/photos/{tenants.A2.PhotoId}", Ct);
        var underOurs = await contact.Http.GetAsync($"/api/portal/visits/{tenants.A1.VisitId}/photos/{tenants.A2.PhotoId}", Ct);
        var otherOrganization = await contact.Http.GetAsync($"/api/portal/visits/{tenants.A1.VisitId}/photos/{tenants.B1.PhotoId}/thumbnail", Ct);

        await ProblemAssert.HasCodeAsync(theirs, HttpStatusCode.NotFound, "visit-not-found");
        await ProblemAssert.HasCodeAsync(underOurs, HttpStatusCode.NotFound, "photo-not-found");
        await ProblemAssert.HasCodeAsync(otherOrganization, HttpStatusCode.NotFound, "photo-not-found");
    }
}
