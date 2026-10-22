using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Memberships;
using TenantHub.Infrastructure.Data;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// Two organizations behind the real API. A ("Alpha Cleaning") has one signed-up user per role plus a removed
/// member; B ("Bravo Gardens") is the organization the tests try to reach from A. Every user has a password, so
/// tests sign in exactly like a browser.
/// </summary>
public sealed class ApiTenants : IAsyncDisposable
{
    private readonly Dictionary<Role, BrowserClient> _clients = [];

    private ApiTenants(ApiFactory factory, string connectionString) => (Factory, ConnectionString) = (factory, connectionString);

    public ApiFactory Factory { get; }

    public string ConnectionString { get; }

    public Guid OrgA { get; private set; }

    public Guid OrgB { get; private set; }

    public string OrgBName { get; } = "Bravo Gardens";

    /// <summary>A standing client in A and one in B, for endpoints that take a client id.</summary>
    public Guid ClientA { get; private set; }

    public Guid ClientB { get; private set; }

    public string ClientBName { get; } = "Bravo Private Estate";

    public string OrgBOwnerEmail { get; } = $"owner-{Guid.NewGuid():N}@bravo.example";

    public string RemovedMemberEmail { get; } = $"removed-{Guid.NewGuid():N}@alpha.example";

    public Dictionary<Role, string> EmailsA { get; } = Enum.GetValues<Role>()
        .ToDictionary(role => role, role => $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@alpha.example");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ApiTenants> CreateAsync(PostgresFixture postgres)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var tenants = new ApiTenants(new ApiFactory(connectionString), connectionString);
        await tenants.ArrangeAsync();
        return tenants;
    }

    /// <summary>A browser signed in as the member of A with this role (cached per role).</summary>
    public async Task<BrowserClient> ClientForAsync(Role role)
    {
        if (!_clients.TryGetValue(role, out var client))
        {
            client = new BrowserClient(Factory);
            (await client.SignInAsync(EmailsA[role])).EnsureSuccessStatusCode();
            _clients[role] = client;
        }

        return client;
    }

    /// <summary>A hash of everything B owns, to show a request changed nothing there.</summary>
    public async Task<string> SnapshotOfBAsync()
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == OrgB, Ct);
        var memberships = await db.Memberships.AcrossTenants().AsNoTracking()
            .Where(m => m.OrganizationId == OrgB)
            .OrderBy(m => m.UserId)
            .ToListAsync(Ct);
        var clients = await db.Clients.AcrossTenants().AsNoTracking()
            .Where(c => c.OrganizationId == OrgB)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Notes, c.Status, c.Version })
            .ToListAsync(Ct);
        var audit = await db.AuditEntries.AcrossTenants().AsNoTracking()
            .Where(a => a.OrganizationId == OrgB)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Action, a.EntityId })
            .ToListAsync(Ct);
        var json = JsonSerializer.Serialize(new { organization, memberships, clients, audit });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public Guid ClientIn(Guid organizationId) =>
        organizationId == OrgA ? ClientA : organizationId == OrgB ? ClientB : Guid.NewGuid();

    /// <summary>A new client in A or B, written as that organization's Owner (like a request would).</summary>
    public async Task<Guid> CreateClientAsync(Guid organizationId, string? name = null)
    {
        if (organizationId != OrgA && organizationId != OrgB)
        {
            return Guid.NewGuid();
        }

        await using var db = await OwnerContextAsync(organizationId);
        var details = ClientDetails.Validate(name ?? $"Client {Guid.NewGuid():N}", null, null, null, null, null);
        var client = Client.Create(organizationId, details, DateTimeOffset.UtcNow);
        db.Clients.Add(client);
        await db.SaveChangesAsync(Ct);
        return client.Id;
    }

    public async Task<(string Name, uint Version)> ClientStateAsync(Guid clientId)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var client = await db.Clients.AcrossTenants().AsNoTracking().SingleOrDefaultAsync(c => c.Id == clientId, Ct);
        return client is null ? ("Unknown", 0) : (client.Name, client.Version);
    }

    public async Task SetArchivedAsync(Guid clientId, bool archived)
    {
        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        var organizationId = await lookup.Clients.AcrossTenants()
            .Where(c => c.Id == clientId)
            .Select(c => (Guid?)c.OrganizationId)
            .SingleOrDefaultAsync(Ct);
        if (organizationId is null)
        {
            return;
        }

        await using var db = await OwnerContextAsync(organizationId.Value);
        var client = await db.Clients.SingleAsync(c => c.Id == clientId, Ct);
        if (archived)
        {
            client.Archive(DateTimeOffset.UtcNow);
        }
        else
        {
            client.Restore(DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        await Factory.DisposeAsync();
    }

    private async Task ArrangeAsync()
    {
        OrgA = await SignUpAsync(EmailsA[Role.Owner], "Alpha Cleaning");
        OrgB = await SignUpAsync(OrgBOwnerEmail, OrgBName);

        // Everyone else signs up too (so they have passwords; their own organizations are irrelevant), then joins A.
        var joiners = new List<(string Email, Role Role, bool Removed)>
        {
            (EmailsA[Role.Admin], Role.Admin, false),
            (EmailsA[Role.Member], Role.Member, false),
            (EmailsA[Role.Viewer], Role.Viewer, false),
            (RemovedMemberEmail, Role.Member, true),
        };
        foreach (var (email, _, _) in joiners)
        {
            await SignUpAsync(email, $"Own {email[..8]}");
        }

        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        var ids = await lookup.Users.Where(u => joiners.Select(j => j.Email).Contains(u.Email!)).ToDictionaryAsync(u => u.Email!, u => u.Id, Ct);
        var ownerId = await lookup.Users.Where(u => u.Email == EmailsA[Role.Owner]).Select(u => u.Id).SingleAsync(Ct);

        var tenant = new TenantContext();
        tenant.Enter(OrgA, ownerId, Role.Owner);
        await using var db = PostgresFixture.CreateDbContext(ConnectionString, tenant);
        var orgA = await db.Organizations.SingleAsync(o => o.Id == OrgA, Ct);
        foreach (var (email, role, removed) in joiners)
        {
            var membership = orgA.AddMember(ids[email], role, DateTimeOffset.UtcNow, ownerId);
            db.Memberships.Add(membership);
            if (removed)
            {
                // Removing members arrives in M05; set the soft-removal column directly for now.
                db.Entry(membership).Property(m => m.RemovedAt).CurrentValue = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(Ct);

        ClientA = await CreateClientAsync(OrgA, "Alpha Standing Client");
        ClientB = await CreateClientAsync(OrgB, ClientBName);
    }

    private async Task<AppDbContext> OwnerContextAsync(Guid organizationId)
    {
        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        var ownerId = await lookup.Memberships.AcrossTenants()
            .Where(m => m.OrganizationId == organizationId && m.Role == Role.Owner)
            .Select(m => m.UserId)
            .FirstAsync(Ct);
        var tenant = new TenantContext();
        tenant.Enter(organizationId, ownerId, Role.Owner);
        return PostgresFixture.CreateDbContext(ConnectionString, tenant);
    }

    private async Task<Guid> SignUpAsync(string email, string organizationName)
    {
        using var browser = new BrowserClient(Factory);
        // Display name = the email's local part, so audit snapshots show which test user acted.
        var response = await browser.SignUpAsync(email, organizationName, displayName: email.Split('@')[0]);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct);
        return session!.User!.Memberships.Single().OrganizationId;
    }
}
