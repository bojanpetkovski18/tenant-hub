using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Clients;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>
/// US-80 and US-52 through the API: adding and removing contacts with the SPEC-V2 5.3 rules, the invitation email, and
/// the portal as a plan feature (SPEC-V2 4.8): refused on Free, a downgrade that turns it off needs confirmation, and
/// the contacts come back with an upgrade.
/// </summary>
public sealed class ContactTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_owner_adds_contacts_within_the_rules_and_each_gets_an_invitation()
    {
        await using var org = await OrgAsync("team");
        var client = await org.ClientAsync("Acme Offices");

        var added = await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = " Dana  Reyes ", email = "Dana@Acme.example" });
        var duplicate = await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = "Dana again", email = "dana@acme.EXAMPLE" });
        for (var i = 2; i <= 5; i++)
        {
            (await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = $"Person {i}", email = $"p{i}@acme.example" })).EnsureSuccessStatusCode();
        }

        var sixth = await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = "Person 6", email = "p6@acme.example" });

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var contact = (await added.Content.ReadFromJsonAsync<ContactView>(BrowserClient.Json, Ct))!;
        Assert.Equal(("Dana Reyes", "Dana@Acme.example"), (contact.Name, contact.Email));
        Assert.Equal($"/api/orgs/{org.Id}/contacts/{contact.Id}", added.Headers.Location!.OriginalString);
        await ProblemAssert.HasFieldErrorsAsync(duplicate, "email");
        await ProblemAssert.HasCodeAsync(sixth, HttpStatusCode.Conflict, "contact-limit-reached");

        var invitation = await EmailLinks.LatestAsync(org.Factory, org.Emails, "Dana@Acme.example", "Brightline Cleaning Co. gave you access to its client portal");
        Assert.Contains("added you as a contact for Acme Offices", invitation.TextBody, StringComparison.Ordinal);
        Assert.Contains("/portal/brightline-cleaning-co", invitation.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("#", invitation.TextBody, StringComparison.Ordinal); // no token: it only says where the portal is
    }

    [Fact]
    public async Task A_removed_contact_is_no_longer_listed_and_the_email_can_be_added_again()
    {
        await using var org = await OrgAsync("team");
        var client = await org.ClientAsync("Acme Offices");
        var contact = await org.AddContactAsync(client, "dana@acme.example");

        var removed = await org.Owner.SendJsonAsync(HttpMethod.Delete, org.Url($"/contacts/{contact}"), null);
        var again = await org.Owner.SendJsonAsync(HttpMethod.Delete, org.Url($"/contacts/{contact}"), null);
        var listed = await org.ContactsAsync(client);
        var readded = await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = "Dana Reyes", email = "dana@acme.example" });

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await ProblemAssert.HasCodeAsync(again, HttpStatusCode.NotFound, "contact-not-found");
        Assert.Empty(listed);
        Assert.Equal(HttpStatusCode.Created, readded.StatusCode);
    }

    [Fact]
    public async Task An_archived_client_gets_no_contacts_and_an_unverified_owner_cannot_invite()
    {
        await using var org = await OrgAsync("team");
        var archived = await org.ClientAsync("Old Client");
        (await org.Owner.PostJsonAsync(org.Url($"/clients/{archived}/archive"), new { })).EnsureSuccessStatusCode();
        await using var unverified = await OrgAsync("team", verify: false);
        var client = await unverified.ClientAsync("Acme Offices");

        await ProblemAssert.HasCodeAsync(
            await org.Owner.PostJsonAsync(org.Url($"/clients/{archived}/contacts"), new { name = "Dana", email = "dana@old.example" }),
            HttpStatusCode.Conflict,
            "client-archived");
        await ProblemAssert.HasCodeAsync(
            await unverified.Owner.PostJsonAsync(unverified.Url($"/clients/{client}/contacts"), new { name = "Dana", email = "dana@acme.example" }),
            HttpStatusCode.Forbidden,
            "email-not-verified");
    }

    [Fact]
    public async Task On_free_every_contact_endpoint_is_refused_with_the_plan_that_has_the_portal()
    {
        await using var org = await OrgAsync("free");
        var client = await org.ClientAsync("Acme Offices");

        var responses = new[]
        {
            await org.Owner.Http.GetAsync(org.Url($"/clients/{client}/contacts"), Ct),
            await org.Owner.PostJsonAsync(org.Url($"/clients/{client}/contacts"), new { name = "Dana", email = "dana@acme.example" }),
            await org.Owner.SendJsonAsync(HttpMethod.Delete, org.Url($"/contacts/{Guid.NewGuid()}"), null),
        };

        foreach (var response in responses)
        {
            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, "plan-feature-unavailable");
        }

        var problem = await responses[0].Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("portal", "team"), (problem.GetProperty("feature").GetString(), problem.GetProperty("plan").GetString()));
    }

    [Fact]
    public async Task A_downgrade_that_turns_the_portal_off_needs_confirmation_and_an_upgrade_brings_the_contacts_back()
    {
        await using var org = await OrgAsync("team");
        var client = await org.ClientAsync("Acme Offices");
        await org.AddContactAsync(client, "dana@acme.example");

        var unconfirmed = await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url("/plan"), new { plan = "free" });
        var confirmed = await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url("/plan"), new { plan = "free", confirmFeatureLoss = true });
        var onFree = await org.Owner.Http.GetAsync(org.Url($"/clients/{client}/contacts"), Ct);
        (await org.Owner.SendJsonAsync(HttpMethod.Put, org.Url("/plan"), new { plan = "team" })).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(unconfirmed, HttpStatusCode.Conflict, "plan-feature-loss");
        var loss = await unconfirmed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(["portal"], loss.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        await ProblemAssert.HasCodeAsync(onFree, HttpStatusCode.Forbidden, "plan-feature-unavailable");
        Assert.Equal(["dana@acme.example"], (await org.ContactsAsync(client)).Select(c => c.Email));
    }

    [Fact]
    public async Task The_organization_lists_its_features_and_every_plans()
    {
        await using var org = await OrgAsync("team");

        var overview = await org.Owner.Http.GetFromJsonAsync<JsonElement>(org.Url(), Ct);

        Assert.Equal(["portal"], overview.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        var plans = overview.GetProperty("plans").EnumerateArray().ToDictionary(
            p => p.GetProperty("plan").GetString()!,
            p => p.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToList());
        Assert.Equal([], plans["free"]);
        Assert.Equal(["portal"], plans["team"]);
        Assert.Equal(["portal", "integrations"], plans["business"]);
    }

    private async Task<Org> OrgAsync(string plan, bool verify = true)
    {
        var emails = new CapturedEmails();
        var factory = new ApiFactory(await postgres.CreateDatabaseAsync(), services: s => s.AddSingleton<IEmailSender>(emails));
        var owner = new BrowserClient(factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email)).EnsureSuccessStatusCode();
        if (verify)
        {
            await EmailLinks.VerifyAsync(factory, emails, owner, email);
        }

        var id = (await owner.SessionAsync()).User!.Memberships.Single().OrganizationId;
        if (plan != "free")
        {
            (await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{id}/plan", new { plan })).EnsureSuccessStatusCode();
        }

        return new Org(factory, emails, owner, id);
    }

    private sealed record Org(ApiFactory Factory, CapturedEmails Emails, BrowserClient Owner, Guid Id) : IAsyncDisposable
    {
        public string Url(string path = "") => $"/api/orgs/{Id}{path}";

        public async Task<Guid> ClientAsync(string name)
        {
            var response = await Owner.PostJsonAsync(Url("/clients"), new { name });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        public async Task<Guid> AddContactAsync(Guid client, string email)
        {
            var response = await Owner.PostJsonAsync(Url($"/clients/{client}/contacts"), new { name = "Dana Reyes", email });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ContactView>(BrowserClient.Json, Ct))!.Id;
        }

        public async Task<IReadOnlyList<ContactView>> ContactsAsync(Guid client) =>
            (await Owner.Http.GetFromJsonAsync<List<ContactView>>(Url($"/clients/{client}/contacts"), BrowserClient.Json, Ct))!;

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
