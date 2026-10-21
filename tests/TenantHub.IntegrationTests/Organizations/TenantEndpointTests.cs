using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TenantHub.Api.Features.Organizations;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
using TenantHub.IntegrationTests.Infrastructure;
using TenantHub.IntegrationTests.Isolation;

namespace TenantHub.IntegrationTests.Organizations;

public sealed class TenantEndpointTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Organization_details_come_from_the_route_s_organization()
    {
        var tenants = await SharedApiTenants.GetAsync<TenantEndpointTests>(postgres);
        var viewer = await tenants.ClientForAsync(Role.Viewer);

        var organization = await viewer.Http.GetFromJsonAsync<OrganizationResponse>($"/api/orgs/{tenants.OrgA}", BrowserClient.Json, Ct);

        Assert.Equal((tenants.OrgA, "Alpha Cleaning", "alpha-cleaning", Plan.Free), (organization!.Id, organization.Name, organization.Slug, organization.Plan));
    }

    [Fact]
    public async Task Members_are_the_organization_s_active_members_only()
    {
        var tenants = await SharedApiTenants.GetAsync<TenantEndpointTests>(postgres);
        var member = await tenants.ClientForAsync(Role.Member);

        var members = await member.Http.GetFromJsonAsync<List<MemberResponse>>($"/api/orgs/{tenants.OrgA}/members", BrowserClient.Json, Ct);

        Assert.Equal(
            [(tenants.EmailsA[Role.Owner], Role.Owner), (tenants.EmailsA[Role.Admin], Role.Admin), (tenants.EmailsA[Role.Member], Role.Member), (tenants.EmailsA[Role.Viewer], Role.Viewer)],
            members!.Select(m => (m.Email, m.Role)));
        Assert.DoesNotContain(members!, m => m.Email == tenants.RemovedMemberEmail || m.Email == tenants.OrgBOwnerEmail);
    }

    [Fact]
    public async Task A_malformed_organization_id_is_not_found()
    {
        var tenants = await SharedApiTenants.GetAsync<TenantEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);

        var response = await owner.Http.GetAsync("/api/orgs/not-a-guid/members", Ct);

        await ProblemAssert.IsProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_guard_stops_a_cross_tenant_write_logs_a_security_event_and_reveals_nothing()
    {
        var tenants = await SharedApiTenants.GetAsync<TenantEndpointTests>(postgres);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var before = await tenants.SnapshotOfBAsync();

        var response = await owner.PostJsonAsync($"/api/orgs/{tenants.OrgA}{ApiFactory.IsolationViolationPattern}?targetOrganizationId={tenants.OrgB}", new { });

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.DoesNotContain("organization", problem.GetProperty("title").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await tenants.SnapshotOfBAsync());
        Assert.Contains(tenants.Factory.Logs.Entries, e =>
            e.Level == LogLevel.Critical && e.Message.Contains("SECURITY: tenant isolation violation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_session_lists_each_membership_s_permissions()
    {
        var tenants = await SharedApiTenants.GetAsync<TenantEndpointTests>(postgres);
        var viewer = await tenants.ClientForAsync(Role.Viewer);

        var session = await viewer.SessionAsync();

        var inA = session.User!.Memberships.Single(m => m.OrganizationId == tenants.OrgA);
        Assert.Equal(Role.Viewer, inA.Role);
        Assert.Equal(Permissions.For(Role.Viewer).Order(), inA.Permissions);
    }
}
