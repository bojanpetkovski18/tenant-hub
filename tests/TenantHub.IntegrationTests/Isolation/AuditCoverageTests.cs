using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC 10, criterion 8: every state-changing endpoint writes at least one audit entry for its organization. Runs
/// every <see cref="TenantEndpointCase"/> marked <c>Audited</c>; the rule test makes sure none is forgotten.
/// </summary>
public sealed class AuditCoverageTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> AuditedCases() => new(TenantEndpointCase.All.Where(c => c.Audited).Select(c => c.Key));

    [Fact]
    public void Every_state_changing_endpoint_is_marked_audited()
    {
        var unmarked = TenantEndpointCase.All
            .Where(c => c.Method != "GET" && !c.Audited && !c.Pattern.StartsWith("/test/", StringComparison.Ordinal))
            .Select(c => c.Key)
            .ToList();

        Assert.True(unmarked.Count == 0, $"State-changing endpoints must be Audited: {string.Join(", ", unmarked)}");
    }

    [Theory]
    [MemberData(nameof(AuditedCases))]
    public async Task A_successful_change_writes_an_audit_entry(string key)
    {
        var tenants = await SharedApiTenants.GetAsync<AuditCoverageTests>(postgres);
        var endpoint = TenantEndpointCase.All.Single(c => c.Key == key);
        var owner = await tenants.ClientForAsync(Role.Owner);
        var before = await CountAsync(tenants);

        var response = await endpoint.SendAsync(owner, tenants, tenants.OrgA);

        Assert.Equal(endpoint.Allowed, response.StatusCode);
        Assert.True(await CountAsync(tenants) > before, $"{key} succeeded without writing an audit entry.");
    }

    private static async Task<int> CountAsync(ApiTenants tenants)
    {
        await using var db = PostgresFixture.CreateDbContext(tenants.ConnectionString);
        return await db.AuditEntries.AcrossTenants().CountAsync(a => a.OrganizationId == tenants.OrgA, Ct);
    }
}
