using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Features.Demo;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Infrastructure.Seed;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Demo;

/// <summary>
/// The portal in the seed and the public demo (SPEC 8.3, M18, D040): Brightline's contacts, quotes and requests, and
/// "Try as a client", which signs in only a contact that existed when the demo was seeded.
/// </summary>
public sealed class DemoPortalTests(PostgresFixture postgres)
{
    private const string Password = "Demo-Pass-2026!";
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_seed_tells_the_portals_story_with_contacts_acting_as_themselves()
    {
        await using var factory = await StartAsync(demoMode: false);
        await using var db = PostgresFixture.CreateDbContext(ConnectionString(factory));
        var brightline = await db.Organizations.SingleAsync(o => o.Slug == DemoSeeder.BrightlineSlug, Ct);

        var contacts = await db.ClientContacts.AcrossTenants().Where(c => c.OrganizationId == brightline.Id).ToListAsync(Ct);
        var quotes = await db.Jobs.AcrossTenants().Where(j => j.OrganizationId == brightline.Id && j.ApprovalStatus != JobApprovalStatus.NotRequested)
            .Select(j => j.ApprovalStatus).ToListAsync(Ct);
        var requests = await db.WorkRequests.AcrossTenants().Where(r => r.OrganizationId == brightline.Id).Select(r => r.Status).ToListAsync(Ct);
        var approval = await db.AuditEntries.AcrossTenants().SingleAsync(a => a.OrganizationId == brightline.Id && a.Action == "job.quote_approved", Ct);

        Assert.Equal(3, contacts.Count);
        Assert.All(contacts, c => Assert.True(c.CreatedAt < brightline.CreatedAt));
        Assert.Equal([JobApprovalStatus.Pending, JobApprovalStatus.Approved], quotes.Order());
        Assert.Equal([WorkRequestStatus.Open, WorkRequestStatus.Open, WorkRequestStatus.Declined], requests.Order());
        Assert.Equal(ActorKind.Contact, approval.ActorKind);
        Assert.Equal(150, await db.Jobs.AcrossTenants().CountAsync(j => j.OrganizationId == brightline.Id && j.AgreementId == null, Ct));
    }

    [Fact]
    public async Task Try_as_a_client_signs_in_a_seeded_contact_only()
    {
        await using var factory = await StartAsync(demoMode: true);
        using var anonymous = new BrowserClient(factory);
        var info = await anonymous.Http.GetFromJsonAsync<DemoInfoResponse>("/api/demo/accounts", BrowserClient.Json, Ct);

        // A visitor signed in as the Owner adds a contact: it must never become a "Try as" choice.
        using var owner = new BrowserClient(factory);
        await owner.SessionAsync();
        (await owner.PostJsonAsync("/api/demo/sign-in", new { role = "owner" })).EnsureSuccessStatusCode();
        var orgId = (await owner.SessionAsync()).User!.Memberships.Single(m => m.Slug == DemoSeeder.BrightlineSlug).OrganizationId;
        var someClient = (await owner.Http.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/clients?pageSize=1", Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        (await owner.PostJsonAsync($"/api/orgs/{orgId}/clients/{someClient}/contacts", new { name = "A Visitor", email = "visitor@elsewhere.example" })).EnsureSuccessStatusCode();
        var after = await anonymous.Http.GetFromJsonAsync<DemoInfoResponse>("/api/demo/accounts", BrowserClient.Json, Ct);

        using var client = new BrowserClient(factory);
        var signedIn = await client.PortalSendAsync(HttpMethod.Post, "/api/portal/demo/sign-in", new { email = info!.PortalContacts[0].Email }, DemoSeeder.BrightlineSlug);
        var visits = await client.Http.GetAsync("/api/portal/visits", Ct);
        using var visitor = new BrowserClient(factory);
        var refused = await visitor.PortalSendAsync(HttpMethod.Post, "/api/portal/demo/sign-in", new { email = "visitor@elsewhere.example" }, DemoSeeder.BrightlineSlug);

        Assert.Equal((DemoSeeder.BrightlineSlug, 3), (info.PortalSlug, info.PortalContacts.Count));
        Assert.Equal(info.PortalContacts.Select(c => c.Email), after!.PortalContacts.Select(c => c.Email));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, visits.StatusCode);
        await ProblemAssert.IsProblemAsync(refused, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Outside_demo_mode_try_as_a_client_does_not_exist()
    {
        await using var factory = await StartAsync(demoMode: false);
        using var browser = new BrowserClient(factory);

        var response = await browser.PortalSendAsync(HttpMethod.Post, "/api/portal/demo/sign-in", new { email = "dana@northgate-offices.example" }, DemoSeeder.BrightlineSlug);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound);
    }

    private async Task<ApiFactory> StartAsync(bool demoMode)
    {
        var clock = new MovingClock(Start);
        var settings = new Dictionary<string, string?>
        {
            ["Seed:Enabled"] = "true",
            [DemoSeeder.PasswordSetting] = Password,
            ["DEMO_MODE"] = demoMode ? "true" : "false",
            ["DEMO_RESET_INTERVAL_HOURS"] = "6",
        };
        var factory = new TrackedFactory(await postgres.CreateDatabaseAsync(), settings, s => s.AddSingleton<TimeProvider>(clock));
        using var warmUp = factory.CreateClient(); // starts the app, which seeds
        return factory;
    }

    private static string ConnectionString(ApiFactory factory) => ((TrackedFactory)factory).ConnectionString;

    private sealed class TrackedFactory(string connectionString, IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection> services)
        : ApiFactory(connectionString, settings, services)
    {
        public string ConnectionString { get; } = connectionString;
    }
}
