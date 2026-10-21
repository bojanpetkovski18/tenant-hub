using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
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
        var json = JsonSerializer.Serialize(new { organization, memberships });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
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
    }

    private async Task<Guid> SignUpAsync(string email, string organizationName)
    {
        using var browser = new BrowserClient(Factory);
        var response = await browser.SignUpAsync(email, organizationName);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(BrowserClient.Json, Ct);
        return session!.User!.Memberships.Single().OrganizationId;
    }
}
