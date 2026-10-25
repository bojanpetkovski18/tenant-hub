using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Invitations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Invitations;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.Infrastructure.Data;
using TenantHub.Infrastructure.Identity;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// Two organizations behind the real API. A ("Alpha Cleaning") has one signed-up user per role, a second Owner (so
/// the Owner may leave) and a removed member; B ("Bravo Gardens") is the organization the tests try to reach from
/// A. Every signed-up user has a password, so tests sign in exactly like a browser.
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

    /// <summary>A standing Viewer of B, for endpoints that take a member's user id.</summary>
    public Guid MemberB { get; private set; }

    /// <summary>A standing pending invitation in A and one in B, for endpoints that take an invitation id.</summary>
    public Guid InvitationA { get; private set; }

    public Guid InvitationB { get; private set; }

    /// <summary>A standing job in A (assigned to A's Member) and one in B, for endpoints that take a job id.</summary>
    public Guid JobA { get; private set; }

    public Guid JobB { get; private set; }

    public string JobBTitle { get; } = "Bravo Hedge Trimming";

    public string OrgBOwnerEmail { get; } = $"owner-{Guid.NewGuid():N}@bravo.example";

    public string RemovedMemberEmail { get; } = $"removed-{Guid.NewGuid():N}@alpha.example";

    public string CoOwnerEmail { get; } = $"co-owner-{Guid.NewGuid():N}@alpha.example";

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
        var invitations = await db.Invitations.AcrossTenants().AsNoTracking()
            .Where(i => i.OrganizationId == OrgB)
            .OrderBy(i => i.Id)
            .Select(i => new { i.Id, i.Role, i.TokenHash, i.ExpiresAt, i.AcceptedAt, i.RevokedAt })
            .ToListAsync(Ct);
        var jobs = await db.Jobs.AcrossTenants().AsNoTracking()
            .Where(j => j.OrganizationId == OrgB)
            .OrderBy(j => j.Id)
            .Select(j => new { j.Id, j.Number, j.Title, j.Status, j.AssigneeUserId, j.Version })
            .ToListAsync(Ct);
        var json = JsonSerializer.Serialize(new { organization, memberships, clients, audit, invitations, jobs, next = organization.NextJobNumber });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public Guid ClientIn(Guid organizationId) =>
        organizationId == OrgA ? ClientA : organizationId == OrgB ? ClientB : Guid.NewGuid();

    public Guid JobIn(Guid organizationId) =>
        organizationId == OrgA ? JobA : organizationId == OrgB ? JobB : Guid.NewGuid();

    public Guid InvitationIn(Guid organizationId) =>
        organizationId == OrgA ? InvitationA : organizationId == OrgB ? InvitationB : Guid.NewGuid();

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

    /// <summary>The user id of A's member with this role.</summary>
    public async Task<Guid> UserIdOfAsync(Role role)
    {
        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        return await lookup.Users.Where(u => u.Email == EmailsA[role]).Select(u => u.Id).SingleAsync(Ct);
    }

    /// <summary>
    /// A new New job in A or B, created like a manager would (numbered from the organization's counter). Assigned to
    /// A's member with <paramref name="assignTo"/> when given, otherwise to the organization's Owner.
    /// </summary>
    public async Task<Guid> CreateJobAsync(Guid organizationId, Role? assignTo = null, string? title = null)
    {
        if (organizationId != OrgA && organizationId != OrgB)
        {
            return Guid.NewGuid();
        }

        var ownerId = await OwnerIdAsync(organizationId);
        var assignee = organizationId == OrgA && assignTo is { } role ? await UserIdOfAsync(role) : ownerId;
        await using var db = ContextAs(organizationId, ownerId);
        var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, Ct);
        var clientId = ClientIn(organizationId);
        var details = JobDetails.Validate(title ?? $"Job {Guid.NewGuid():N}", null, clientId, assignee, JobPriority.Normal, null, null, null);
        var job = Job.Create(
            organizationId,
            organization.TakeJobNumber(),
            details,
            JobActor.From(ownerId, Role.Owner),
            new Dictionary<Guid, string>(),
            DateTimeOffset.UtcNow);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return job.Id;
    }

    public async Task<JobDetails> JobDetailsAsync(Guid jobId)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var job = await db.Jobs.AcrossTenants().AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, Ct);
        return job?.Details ?? JobDetails.Validate("Unknown job", null, Guid.NewGuid(), null, null, null, null, null);
    }

    public async Task<uint> JobVersionAsync(Guid jobId)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var job = await db.Jobs.AcrossTenants().AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, Ct);
        return job?.Version ?? 0;
    }

    /// <summary>A new member of A or B without a password (nobody signs in as them); returns the user id.</summary>
    public async Task<Guid> CreateMemberAsync(Guid organizationId, Role role = Role.Member)
    {
        if (organizationId != OrgA && organizationId != OrgB)
        {
            return Guid.NewGuid();
        }

        var email = $"member-{Guid.NewGuid():N}@test.example";
        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = email.Split('@')[0],
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var users = PostgresFixture.CreateDbContext(ConnectionString))
        {
            users.Users.Add(user);
            await users.SaveChangesAsync(Ct);
        }

        await using var db = await OwnerContextAsync(organizationId);
        var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, Ct);
        db.Memberships.Add(organization.AddMember(user.Id, role, DateTimeOffset.UtcNow, null));
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }

    /// <summary>A new pending invitation (as Member) in A or B; nobody holds its link.</summary>
    public async Task<Guid> CreateInvitationAsync(Guid organizationId)
    {
        if (organizationId != OrgA && organizationId != OrgB)
        {
            return Guid.NewGuid();
        }

        var ownerId = await OwnerIdAsync(organizationId);
        await using var db = ContextAs(organizationId, ownerId);
        var invitation = Invitation.Create(
            organizationId, $"invitee-{Guid.NewGuid():N}@test.example", Role.Member, InvitationTokens.Create().Hash,
            new Actor(ownerId, Role.Owner), DateTimeOffset.UtcNow);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(Ct);
        return invitation.Id;
    }

    /// <summary>Brings back A's role users who left, so a case that makes the caller leave can run for every role.</summary>
    public async Task RestoreMembersOfAAsync()
    {
        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        var users = await lookup.Users
            .Where(u => EmailsA.Values.Contains(u.Email!) || u.Email == CoOwnerEmail)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(Ct);
        var roleOf = users.ToDictionary(
            u => u.Id,
            u => u.Email == CoOwnerEmail ? Role.Owner : EmailsA.Single(e => e.Value == u.Email).Key);

        await using var db = await OwnerContextAsync(OrgA);
        var left = await db.Memberships.Where(m => roleOf.Keys.Contains(m.UserId) && m.RemovedAt != null).ToListAsync(Ct);
        foreach (var membership in left)
        {
            membership.Rejoin(roleOf[membership.UserId], null, DateTimeOffset.UtcNow);
        }

        await db.SaveChangesAsync(Ct);
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
        await SetPlanAsync(OrgA, Plan.Business);
        await SetPlanAsync(OrgB, Plan.Business);

        // Everyone else signs up too (so they have passwords; their own organizations are irrelevant), then joins A.
        var joiners = new List<(string Email, Role Role, bool Removed)>
        {
            (EmailsA[Role.Admin], Role.Admin, false),
            (EmailsA[Role.Member], Role.Member, false),
            (EmailsA[Role.Viewer], Role.Viewer, false),
            (RemovedMemberEmail, Role.Member, true),
            (CoOwnerEmail, Role.Owner, false),
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
                membership.Remove(DateTimeOffset.UtcNow, email);
            }
        }

        await db.SaveChangesAsync(Ct);

        ClientA = await CreateClientAsync(OrgA, "Alpha Standing Client");
        ClientB = await CreateClientAsync(OrgB, ClientBName);
        MemberB = await CreateMemberAsync(OrgB, Role.Viewer);
        InvitationA = await CreateInvitationAsync(OrgA);
        InvitationB = await CreateInvitationAsync(OrgB);
        JobA = await CreateJobAsync(OrgA, Role.Member, "Alpha Standing Job");
        JobB = await CreateJobAsync(OrgB, title: JobBTitle);
    }

    /// <summary>
    /// Sets an organization's plan directly. The fixture's organizations are on Business, so the matrices and flows are
    /// not limited; tests about limits use their own Free organizations.
    /// </summary>
    public async Task SetPlanAsync(Guid organizationId, Plan plan)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == organizationId, Ct);
        if (organization is not null)
        {
            db.Entry(organization).Property(o => o.Plan).CurrentValue = plan;
            await db.SaveChangesAsync(Ct);
        }
    }

    /// <summary>
    /// A new organization standing in for <paramref name="like"/>: A's role users with their roles when it is A, B's
    /// Owner alone when it is B, and a random id (no organization) otherwise. Business plan, like the fixture.
    /// </summary>
    public async Task<Guid> CreateOrganizationLikeAsync(Guid like)
    {
        if (like != OrgA && like != OrgB)
        {
            return Guid.NewGuid();
        }

        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        var members = await lookup.Memberships.AcrossTenants()
            .Where(m => m.OrganizationId == like && m.RemovedAt == null && (like == OrgB || EmailsA.Values.Contains(lookup.Users.Single(u => u.Id == m.UserId).Email!)))
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(Ct);
        var ownerId = members.First(m => m.Role == Role.Owner).UserId;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var organization = Organization.Create($"Disposable {suffix}", $"disposable-{suffix}", "UTC", ownerId, DateTimeOffset.UtcNow, Plan.Business);
        await using (var db = PostgresFixture.CreateDbContext(ConnectionString))
        {
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(Ct);
        }

        await using var tenantDb = ContextAs(organization.Id, ownerId);
        foreach (var member in members)
        {
            tenantDb.Memberships.Add(member.Role == Role.Owner
                ? organization.AddOwner(member.UserId, DateTimeOffset.UtcNow)
                : organization.AddMember(member.UserId, member.Role, DateTimeOffset.UtcNow, ownerId));
        }

        await tenantDb.SaveChangesAsync(Ct);
        return organization.Id;
    }

    public async Task<string> OrganizationNameAsync(Guid organizationId)
    {
        await using var db = PostgresFixture.CreateDbContext(ConnectionString);
        return await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Name).SingleOrDefaultAsync(Ct) ?? "Unknown organization";
    }

    /// <summary>A context working in the organization as its first active Owner.</summary>
    private async Task<AppDbContext> OwnerContextAsync(Guid organizationId) =>
        ContextAs(organizationId, await OwnerIdAsync(organizationId));

    private async Task<Guid> OwnerIdAsync(Guid organizationId)
    {
        await using var lookup = PostgresFixture.CreateDbContext(ConnectionString);
        return await lookup.Memberships.AcrossTenants()
            .Where(m => m.OrganizationId == organizationId && m.Role == Role.Owner && m.RemovedAt == null)
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .FirstAsync(Ct);
    }

    private AppDbContext ContextAs(Guid organizationId, Guid ownerId)
    {
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
