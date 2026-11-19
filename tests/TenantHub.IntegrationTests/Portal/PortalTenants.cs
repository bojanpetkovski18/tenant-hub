using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>One client of an organization, with its contact and its standing job (a visit the contact may see).</summary>
public sealed record PortalClient(
    string Name,
    Guid OrganizationId,
    string OrganizationName,
    string Slug,
    Guid ClientId,
    Guid ContactId,
    string ContactEmail,
    Guid JobId,
    string JobTitle,
    Guid RequestId,
    string RequestTitle);

/// <summary>
/// The portal matrix's world (SPEC-V2 3.2): two organizations on Team, two clients in each, one contact per client,
/// one job per client. Contacts sign in the way people do: they ask for a link, and the test follows it from the email.
/// </summary>
public sealed class PortalTenants : IAsyncDisposable
{
    private readonly Dictionary<Guid, BrowserClient> _owners = [];
    private readonly Dictionary<Guid, Guid> _ownerIds = [];

    private PortalTenants(ApiFactory factory, CapturedEmails emails, string connectionString)
    {
        Factory = factory;
        Emails = emails;
        ConnectionString = connectionString;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ApiFactory Factory { get; }

    public CapturedEmails Emails { get; }

    public string ConnectionString { get; }

    /// <summary>Alpha's first client: the one whose contact makes the requests in the matrix.</summary>
    public PortalClient A1 { get; private set; } = null!;

    /// <summary>Another client of the same organization: its ids must be "not found" to A1.</summary>
    public PortalClient A2 { get; private set; } = null!;

    public PortalClient B1 { get; private set; } = null!;

    public PortalClient B2 { get; private set; } = null!;

    public IEnumerable<PortalClient> All => [A1, A2, B1, B2];

    public static async Task<PortalTenants> CreateAsync(
        PostgresFixture postgres, IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var emails = new CapturedEmails();
        var factory = new ApiFactory(connectionString, settings, s =>
        {
            s.AddSingleton<IEmailSender>(emails);
            services?.Invoke(s);
        });
        var tenants = new PortalTenants(factory, emails, connectionString);
        var (alpha, alphaSlug, alphaName) = await tenants.OrganizationAsync("Alpha Cleaning");
        var (bravo, bravoSlug, bravoName) = await tenants.OrganizationAsync("Bravo Gardens");
        tenants.A1 = await tenants.ClientAsync(alpha, alphaSlug, alphaName, "Alpha One Offices", "Alpha One window clean");
        tenants.A2 = await tenants.ClientAsync(alpha, alphaSlug, alphaName, "Alpha Two Clinic", "Alpha Two gutter check");
        tenants.B1 = await tenants.ClientAsync(bravo, bravoSlug, bravoName, "Bravo One Estate", "Bravo One lawn care");
        tenants.B2 = await tenants.ClientAsync(bravo, bravoSlug, bravoName, "Bravo Two School", "Bravo Two hedge trim");
        await factory.DispatchOutboxAsync(); // the invitations, so tests see only what they cause
        return tenants;
    }

    /// <summary>The organization's Owner, signed in (verified, so they can add contacts).</summary>
    public BrowserClient OwnerOf(Guid organizationId) => _owners[organizationId];

    /// <summary>A browser signed in to the portal as the client's contact, through the emailed link.</summary>
    public async Task<BrowserClient> SignInAsync(PortalClient who)
    {
        var browser = new BrowserClient(Factory);
        var token = await RequestLinkAsync(browser, who);
        var confirmed = await browser.PortalSendAsync(HttpMethod.Post, "/api/portal/sign-in/confirm", new { orgSlug = who.Slug, token }, who.Slug);
        confirmed.EnsureSuccessStatusCode();
        return browser;
    }

    /// <summary>Asks for a sign-in link as <paramref name="browser"/> and returns the token from the email.</summary>
    public async Task<string> RequestLinkAsync(BrowserClient browser, PortalClient who)
    {
        var before = Emails.Sent.Count(m => m.To == who.ContactEmail);
        (await browser.PortalSendAsync(HttpMethod.Post, "/api/portal/sign-in/request", new { orgSlug = who.Slug, email = who.ContactEmail }, who.Slug)).EnsureSuccessStatusCode();
        await Factory.DispatchOutboxAsync();
        var sent = Emails.Sent.Where(m => m.To == who.ContactEmail && m.Subject == $"Your sign-in link for {who.OrganizationName}").ToList();
        Assert.True(sent.Count > 0 && Emails.Sent.Count(m => m.To == who.ContactEmail) > before, $"No new sign-in email for {who.ContactEmail}.");
        return EmailLinks.TokenFrom(sent[^1], "sign-in");
    }

    /// <summary>
    /// A hash of everything that is not <paramref name="except"/>'s: other clients' jobs, contacts and client rows, and
    /// every audit entry not about them. A portal call that leaks or writes across the client boundary changes it.
    /// </summary>
    public async Task<string> SnapshotExceptAsync(PortalClient except)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking()
            .Where(j => j.ClientId != except.ClientId)
            .OrderBy(j => j.Id)
            .Select(j => new { j.Id, j.Title, j.Status, j.Version })
            .ToListAsync(Ct);
        var contacts = await db.ClientContacts.AcrossTenants().AsNoTracking()
            .Where(c => c.ClientId != except.ClientId)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.RemovedAt, c.LastSignInAt })
            .ToListAsync(Ct);
        var clients = await db.Clients.AcrossTenants().AsNoTracking()
            .Where(c => c.Id != except.ClientId)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Status, c.Version })
            .ToListAsync(Ct);
        var audit = await db.AuditEntries.AcrossTenants().AsNoTracking()
            .Where(a => a.OrganizationId != except.OrganizationId)
            .CountAsync(Ct);
        var requests = await db.WorkRequests.AcrossTenants().AsNoTracking()
            .Where(r => r.ClientId != except.ClientId)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.Title, r.Status, r.Version })
            .ToListAsync(Ct);
        var json = JsonSerializer.Serialize(new { jobs, contacts, clients, audit, requests });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owner in _owners.Values)
        {
            owner.Dispose();
        }

        await Factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Slug, string Name)> OrganizationAsync(string name)
    {
        var owner = new BrowserClient(Factory);
        var email = BrowserClient.NewEmail();
        (await owner.SignUpAsync(email, organizationName: name)).EnsureSuccessStatusCode();
        await EmailLinks.VerifyAsync(Factory, Emails, owner, email);
        var user = (await owner.SessionAsync()).User!;
        var membership = user.Memberships.Single();
        _ownerIds[membership.OrganizationId] = user.Id;
        (await owner.SendJsonAsync(HttpMethod.Put, $"/api/orgs/{membership.OrganizationId}/plan", new { plan = "team" })).EnsureSuccessStatusCode();
        _owners[membership.OrganizationId] = owner;
        return (membership.OrganizationId, membership.Slug, membership.Name);
    }

    private async Task<PortalClient> ClientAsync(Guid organizationId, string slug, string organizationName, string name, string jobTitle)
    {
        var owner = _owners[organizationId];
        var url = $"/api/orgs/{organizationId}";
        var client = await IdOfAsync(await owner.PostJsonAsync($"{url}/clients", new { name }));
        var email = $"contact-{Guid.NewGuid():N}@client.example";
        var contact = await IdOfAsync(await owner.PostJsonAsync($"{url}/clients/{client}/contacts", new { name = $"{name} Contact", email }));
        var job = await IdOfAsync(await owner.PostJsonAsync($"{url}/jobs", new
        {
            title = jobTitle,
            clientId = client,
            assigneeUserId = _ownerIds[organizationId],
            scheduledOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3),
            quotedAmount = 180m,
        }));
        // The standing job's quote waits for the contact's decision.
        (await owner.PostJsonAsync($"{url}/jobs/{job}/quote", new { })).EnsureSuccessStatusCode();
        // And the contact has asked for something, as they would in the portal.
        var requestTitle = $"{name} asks for a carpet clean";
        var tenant = new TenantContext();
        tenant.EnterAsContact(organizationId, client, contact);
        await using var db = PostgresFixture.CreateDbContext(ConnectionString, tenant);
        var request = Domain.Clients.WorkRequest.Submit(
            await db.ClientContacts.SingleAsync(c => c.Id == contact, Ct), requestTitle, null, null, 0, DateOnly.FromDateTime(DateTime.UtcNow), DateTimeOffset.UtcNow);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync(Ct);
        return new PortalClient(name, organizationId, organizationName, slug, client, contact, email, job, jobTitle, request.Id, requestTitle);
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }
}
