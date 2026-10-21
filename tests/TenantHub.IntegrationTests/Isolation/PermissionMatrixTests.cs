using System.Net;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC 10, criterion 5: every tenant-scoped endpoint called by every role of its own organization. The expected
/// answer comes from the role map, which PermissionsTests pins to the SPEC table.
/// </summary>
public sealed class PermissionMatrixTests(PostgresFixture postgres)
{
    public static TheoryData<string, Role> Cases()
    {
        var data = new TheoryData<string, Role>();
        foreach (var endpoint in TenantEndpointCase.All)
        {
            foreach (var role in Enum.GetValues<Role>())
            {
                data.Add(endpoint.Key, role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_role_gets_exactly_what_its_permissions_allow(string key, Role role)
    {
        var tenants = await SharedApiTenants.GetAsync<PermissionMatrixTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var client = await tenants.ClientForAsync(role);

        var response = await endpoint.SendAsync(client, tenants, tenants.OrgA);

        if (Permissions.For(role).Contains(endpoint.Permission))
        {
            Assert.Equal(endpoint.Allowed, response.StatusCode);
        }
        else
        {
            await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Forbidden, TenantResolutionFilter.PermissionDeniedCode);
        }
    }
}
