using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Tenancy;
using TenantHub.Domain;
using TenantHub.Domain.Integrations;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC-V2 3.3, the key rows of the matrix: every key-enabled endpoint with a key of the other organization (404, as
/// for a stranger), a read-only key on writes (403), a revoked or expired key (401), a session cookie and a key together
/// (400); every other tenant endpoint refuses keys (403 <c>api-key-not-allowed</c>); and a plan below Business (403).
/// </summary>
public sealed class ApiKeyMatrixTests(PostgresFixture postgres)
{
    /// <summary>SPEC-V2 7: the endpoints that accept keys. The route table must match this list exactly.</summary>
    public static readonly IReadOnlySet<string> KeyEnabled = new HashSet<string>(
    [
        "GET /api/orgs/{orgId:guid}/clients/",
        "POST /api/orgs/{orgId:guid}/clients/",
        "GET /api/orgs/{orgId:guid}/clients/{id:guid}",
        "PUT /api/orgs/{orgId:guid}/clients/{id:guid}",
        "GET /api/orgs/{orgId:guid}/jobs/",
        "POST /api/orgs/{orgId:guid}/jobs/",
        "GET /api/orgs/{orgId:guid}/jobs/{id:guid}",
        "PUT /api/orgs/{orgId:guid}/jobs/{id:guid}",
        "POST /api/orgs/{orgId:guid}/jobs/{id:guid}/transitions",
        "GET /api/orgs/{orgId:guid}/jobs/export",
        "GET /api/orgs/{orgId:guid}/agreements/",
        "GET /api/orgs/{orgId:guid}/agreements/{id:guid}",
    ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> KeyCases() => new(TenantEndpointCase.All.Where(c => KeyEnabled.Contains(c.Key)).Select(c => c.Key));

    public static TheoryData<string> OtherCases() => new(TenantEndpointCase.All.Where(c => !KeyEnabled.Contains(c.Key)).Select(c => c.Key));

    [Fact]
    public void Every_key_enabled_endpoint_has_a_matrix_case() =>
        Assert.Equal(KeyEnabled.Order(), TenantEndpointCase.All.Where(c => KeyEnabled.Contains(c.Key)).Select(c => c.Key).Order());

    [Theory]
    [MemberData(nameof(KeyCases))]
    public async Task A_read_write_key_does_what_the_endpoint_allows(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var client = KeyClient(tenants, tenants.KeyA);
        client.SendXsrfHeader = false; // no cookie, so nothing to forge: a key skips antiforgery

        var response = await endpoint.SendAsync(client, tenants, tenants.OrgA);

        Assert.Equal(endpoint.Allowed, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public async Task A_key_of_another_organization_is_not_found_and_changes_nothing(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var client = KeyClient(tenants, tenants.KeyA);
        var input = await endpoint.PrepareAsync(tenants, tenants.OrgB);
        var before = await tenants.SnapshotOfBAsync();

        var response = await endpoint.SendAsync(client, input);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(TenantResolutionFilter.OrganizationNotFoundCode, problem.GetProperty("code").GetString());
        Assert.DoesNotContain(tenants.OrgBName, problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await tenants.SnapshotOfBAsync());

        // The same answer as an organization that does not exist.
        var missing = await ProblemAssert.IsProblemAsync(await endpoint.SendAsync(client, tenants, Guid.NewGuid()), HttpStatusCode.NotFound);
        Assert.Equal(problem.GetProperty("title").GetString(), missing.GetProperty("title").GetString());
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public async Task A_read_only_key_reads_and_is_refused_every_write(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var (_, readOnly) = await tenants.CreateApiKeyAsync(tenants.OrgA, ApiKeyAccess.ReadOnly);
        using var client = KeyClient(tenants, readOnly);

        var response = await endpoint.SendAsync(client, tenants, tenants.OrgA);

        if (endpoint.Method == "GET")
        {
            Assert.Equal(endpoint.Allowed, response.StatusCode);
        }
        else
        {
            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, TenantResolutionFilter.PermissionDeniedCode);
        }
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public async Task Revoked_and_expired_keys_are_refused(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var (_, revoked) = await tenants.CreateApiKeyAsync(tenants.OrgA, revoked: true);
        var (_, expired) = await tenants.CreateApiKeyAsync(tenants.OrgA, expired: true);

        foreach (var presented in new[] { revoked, expired })
        {
            using var client = KeyClient(tenants, presented);
            var response = await endpoint.SendAsync(client, tenants, tenants.OrgA);

            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Unauthorized, ApiKeyAuth.InvalidCode);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        }
    }

    [Theory]
    [MemberData(nameof(KeyCases))]
    public async Task A_session_cookie_and_a_key_together_are_refused(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var browser = new BrowserClient(tenants.Factory);
        (await browser.SignInAsync(tenants.EmailsA[Role.Owner])).EnsureSuccessStatusCode();
        browser.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenants.KeyA);

        var response = await endpoint.SendAsync(browser, tenants, tenants.OrgA);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, ApiKeyAuth.ConflictCode);
    }

    [Theory]
    [MemberData(nameof(OtherCases))]
    public async Task Every_other_tenant_endpoint_refuses_a_key(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var client = KeyClient(tenants, tenants.KeyA);
        var input = await endpoint.PrepareAsync(tenants, tenants.OrgA);

        var response = await endpoint.SendAsync(client, input);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, ApiKeyAuth.NotAllowedCode);
    }

    [Theory]
    [InlineData("GET", "/api/auth/session")]
    [InlineData("GET", "/api/me/notification-preferences")]
    [InlineData("POST", "/api/orgs")]
    [InlineData("GET", "/api/portal/session")]
    [InlineData("GET", "/api/portal/visits")]
    public async Task Endpoints_outside_an_organization_refuse_a_key(string method, string path)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        using var client = KeyClient(tenants, tenants.KeyA);

        var response = await client.Http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) }, Ct);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, ApiKeyAuth.NotAllowedCode);
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("thk_tooShort")]
    [InlineData("thk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task An_unknown_key_looks_like_a_stranger(string presented)
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        using var client = KeyClient(tenants, presented);

        var response = await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);
    }

    [Fact]
    public async Task Below_the_Business_plan_a_key_is_refused_and_works_again_after_an_upgrade()
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        using var client = KeyClient(tenants, tenants.KeyA);

        foreach (var plan in new[] { Plan.Free, Plan.Team })
        {
            await tenants.SetPlanAsync(tenants.OrgA, plan);
            try
            {
                await ProblemAssert.HasCodeAsync(await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct), HttpStatusCode.Forbidden, "plan-feature-unavailable");
            }
            finally
            {
                await tenants.SetPlanAsync(tenants.OrgA, Plan.Business);
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct)).StatusCode);
    }

    [Fact]
    public async Task What_a_key_changes_is_audited_under_the_key_and_created_by_its_creator()
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var (keyId, presented) = await tenants.CreateApiKeyAsync(tenants.OrgA);
        using var client = KeyClient(tenants, presented);

        var response = await client.PostJsonAsync($"/api/orgs/{tenants.OrgA}/jobs", new { title = "Booked through the API", clientId = tenants.ClientA });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<IdOnly>(BrowserClient.Json, Ct))!.Id;
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var keyName = await db.ApiKeys.AcrossTenants().Where(k => k.Id == keyId).Select(k => k.Name).SingleAsync(Ct);
        var entry = await db.AuditEntries.AcrossTenants().SingleAsync(a => a.EntityId == jobId && a.Action == "job.created", Ct);
        Assert.Equal($"API key {keyName}", entry.ActorName);
        Assert.Equal(ActorKind.ApiKey, entry.ActorKind);
        Assert.Equal(keyId, entry.ActorRef);
        var job = await db.Jobs.AcrossTenants().SingleAsync(j => j.Id == jobId, Ct);
        Assert.Equal(await tenants.UserIdOfAsync(Role.Owner), job.CreatedByUserId);
    }

    [Fact]
    public async Task A_body_that_is_not_utf8_json_is_a_400()
    {
        // Seen in the acceptance from Git Bash, whose curl sent "é" in a Windows code page. Development throws (so the
        // developer sees why); every other environment, this one included, answers 400.
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        using var client = KeyClient(tenants, tenants.KeyA);
        using var body = new ByteArrayContent([.. "{\"title\":\"Caf"u8, 0xE9, .. "\"}"u8]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.Http.PostAsync($"/api/orgs/{tenants.OrgA}/jobs", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Use_is_recorded_at_most_once_a_minute()
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var (keyId, presented) = await tenants.CreateApiKeyAsync(tenants.OrgA, ApiKeyAccess.ReadOnly);
        using var client = KeyClient(tenants, presented);

        (await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/clients", Ct)).EnsureSuccessStatusCode();
        var first = await LastUsedAsync(tenants, keyId);
        (await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/clients", Ct)).EnsureSuccessStatusCode();

        Assert.NotNull(first);
        Assert.Equal(first, await LastUsedAsync(tenants, keyId));
    }

    [Fact]
    public async Task A_key_is_never_shown_again_after_it_is_created()
    {
        var tenants = await SharedApiTenants.GetAsync<ApiKeyMatrixTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);

        var created = await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}/api-keys", new { name = "Accounting sync", access = "readWrite" });
        var body = await created.Content.ReadFromJsonAsync<CreatedKey>(BrowserClient.Json, Ct);
        var list = await owner.Http.GetStringAsync($"/api/orgs/{tenants.OrgA}/api-keys", Ct);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Matches("^thk_[A-Za-z0-9_-]{43}$", body!.Key);
        Assert.StartsWith(body.ApiKey.Prefix, body.Key, StringComparison.Ordinal);
        Assert.DoesNotContain(body.Key, list, StringComparison.Ordinal);
        Assert.Contains(body.ApiKey.Prefix, list, StringComparison.Ordinal);
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        var stored = await db.ApiKeys.AcrossTenants().SingleAsync(k => k.Id == body.ApiKey.Id, Ct);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body.Key)), stored.KeyHash);

        // Revocation is immediate.
        using var client = KeyClient(tenants, body.Key);
        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendJsonAsync(HttpMethod.Delete, $"/api/orgs/{tenants.OrgA}/api-keys/{body.ApiKey.Id}", null)).StatusCode);
        await ProblemAssert.HasCodeAsync(await client.Http.GetAsync($"/api/orgs/{tenants.OrgA}/jobs", Ct), HttpStatusCode.Unauthorized, ApiKeyAuth.InvalidCode);
    }

    private static BrowserClient KeyClient(ApiTenants tenants, string key)
    {
        var client = new BrowserClient(tenants.Factory);
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<DateTimeOffset?> LastUsedAsync(ApiTenants tenants, Guid keyId)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.ApiKeys.AcrossTenants().Where(k => k.Id == keyId).Select(k => k.LastUsedAt).SingleAsync(Ct);
    }

    private sealed record IdOnly(Guid Id);

    private sealed record CreatedKeyView(Guid Id, string Prefix);

    private sealed record CreatedKey(CreatedKeyView ApiKey, string Key);
}
