using System.Net;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC 3.4, cross-tenant matrix: for every tenant-scoped endpoint, the Owner of A (who holds every permission)
/// aims at B. Expected: 404 <c>organization-not-found</c>, identical to an organization that does not exist; B's
/// data unchanged; nothing of B's in the response.
/// </summary>
public sealed class CrossTenantMatrixTests(PostgresFixture postgres)
{
    public static TheoryData<string> Cases() => new(TenantEndpointCase.All.Select(c => c.Key));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Another_organization_s_route_is_not_found_and_changes_nothing(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<CrossTenantMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var input = await endpoint.PrepareAsync(tenants, tenants.OrgB);
        var before = await tenants.SnapshotOfBAsync();

        var response = await endpoint.SendAsync(owner, input);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal(TenantResolutionFilter.OrganizationNotFoundCode, problem.GetProperty("code").GetString());
        var body = problem.GetRawText();
        Assert.DoesNotContain(tenants.OrgB.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenants.OrgBName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenants.OrgBOwnerEmail, body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await tenants.SnapshotOfBAsync());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_foreign_organization_looks_exactly_like_a_missing_one(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<CrossTenantMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var owner = await tenants.ClientForAsync(Role.Owner);

        var foreign = await ProblemAssert.IsProblemAsync(await endpoint.SendAsync(owner, tenants, tenants.OrgB), HttpStatusCode.NotFound);
        var missing = await ProblemAssert.IsProblemAsync(await endpoint.SendAsync(owner, tenants, Guid.NewGuid()), HttpStatusCode.NotFound);

        // Same status, title and code: a caller cannot probe which organization ids exist.
        Assert.Equal(foreign.GetProperty("title").GetString(), missing.GetProperty("title").GetString());
        Assert.Equal(foreign.GetProperty("code").GetString(), missing.GetProperty("code").GetString());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_removed_member_is_treated_as_a_stranger(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<CrossTenantMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var removed = new BrowserClient(tenants.Factory);
        (await removed.SignInAsync(tenants.RemovedMemberEmail)).EnsureSuccessStatusCode();

        var response = await endpoint.SendAsync(removed, tenants, tenants.OrgA);

        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.NotFound, TenantResolutionFilter.OrganizationNotFoundCode);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Signed_out_callers_get_401_not_a_tenant_answer(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<CrossTenantMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        using var anonymous = new BrowserClient(tenants.Factory);

        var response = await endpoint.SendAsync(anonymous, tenants, tenants.OrgA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> ResourceCases() =>
        new(TenantEndpointCase.All.Where(c => c.TargetsResource).Select(c => c.Key));

    [Theory]
    [MemberData(nameof(ResourceCases))]
    public async Task Another_organization_s_resource_under_our_own_route_is_not_found(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<CrossTenantMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var owner = await tenants.ClientForAsync(Role.Owner);
        // A's route, A's membership, B's client id: the tenant filter makes it simply not exist.
        var input = await endpoint.PrepareAsync(tenants, tenants.OrgA, tenants.ClientB);
        var before = await tenants.SnapshotOfBAsync();

        var response = await endpoint.SendAsync(owner, input);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("client-not-found", problem.GetProperty("code").GetString());
        Assert.DoesNotContain(tenants.ClientBName, problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await tenants.SnapshotOfBAsync());
    }
}
