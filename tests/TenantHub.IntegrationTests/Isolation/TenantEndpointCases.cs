using System.Net;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// One line per tenant-scoped endpoint: the cross-tenant matrix and the permission matrix run every line, and
/// <see cref="EndpointCoverageTests"/> fails if an endpoint in the route table has no line here. Later milestones
/// add their endpoints here; the tests themselves stay unchanged.
/// </summary>
public sealed record TenantEndpointCase(
    string Method,
    string Pattern,
    string Permission,
    HttpStatusCode Allowed = HttpStatusCode.OK,
    Func<ApiTenants, object>? Body = null)
{
    /// <summary>The route as the route table shows it, e.g. <c>GET /api/orgs/{orgId:guid}/members</c>.</summary>
    public string Key => $"{Method} {EndpointAccessExtensions.TenantRoutePrefix}{Pattern}";

    public string PathFor(Guid organizationId) => $"/api/orgs/{organizationId}{Pattern}";

    public override string ToString() => Key;

    public static IReadOnlyList<TenantEndpointCase> All { get; } =
    [
        new("GET", "/", Permissions.OrgRead),
        new("GET", "/members", Permissions.OrgRead),

        // Test-only endpoints (ApiFactory): the 403 path, and the guard stopping a deliberate bug (500).
        new("GET", ApiFactory.OwnerOnlyPattern, Permissions.BillingManage),
        new("POST", $"{ApiFactory.IsolationViolationPattern}", Permissions.OrgRead, HttpStatusCode.InternalServerError),
    ];

    public async Task<HttpResponseMessage> SendAsync(BrowserClient client, ApiTenants tenants, Guid organizationId)
    {
        var path = PathFor(organizationId);
        if (Pattern == ApiFactory.IsolationViolationPattern)
        {
            path += $"?targetOrganizationId={tenants.OrgB}";
        }

        return Method switch
        {
            "GET" => await client.Http.GetAsync(path, TestContext.Current.CancellationToken),
            "POST" => await client.PostJsonAsync(path, Body?.Invoke(tenants) ?? new { }),
            _ => throw new NotSupportedException(Method),
        };
    }
}
