using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>
/// SPEC 3.4, coverage, read from the real route table: every endpoint is classified; the public ones are exactly
/// the reviewed list; tenant-scoped ones sit under <c>/api/orgs/{orgId}</c>, declare a permission and have a line in
/// <see cref="TenantEndpointCase.All"/>, so the matrices cover them without anyone remembering to add a test.
/// </summary>
public sealed class EndpointCoverageTests(OfflineApiFactory factory) : IClassFixture<OfflineApiFactory>
{
    /// <summary>SPEC 7's public endpoints, plus the 404 fallback and the test-only throwing route.</summary>
    private static readonly HashSet<string> ExpectedPublic =
    [
        "GET /api/auth/session",
        "POST /api/auth/sign-up",
        "POST /api/auth/sign-in",
        "* /health", // MapHealthChecks answers any method; read-only
        "* /health/live",
        "* {**path}",
        $"GET {ApiFactory.ThrowingPath}",
    ];

    private List<(string Key, Endpoint Endpoint)> Endpoints()
    {
        using var client = factory.CreateClient(); // builds the app, so the route table exists
        return [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Key: $"{Method(e)} {e.RoutePattern.RawText}", Endpoint: (Endpoint)e))];
    }

    [Fact]
    public void Every_endpoint_is_classified()
    {
        var unclassified = Endpoints().Where(e => e.Endpoint.Metadata.GetMetadata<EndpointAccessMetadata>() is null).Select(e => e.Key).ToList();

        Assert.True(unclassified.Count == 0, $"Mark with .Public(), .UserScoped() or MapTenantGroup(): {string.Join(", ", unclassified)}");
    }

    [Fact]
    public void Public_endpoints_are_exactly_the_reviewed_list_and_allow_anonymous()
    {
        var publicEndpoints = Endpoints().Where(e => Access(e.Endpoint) == EndpointAccess.Public).ToList();

        Assert.Equal(ExpectedPublic.Order(), publicEndpoints.Select(e => e.Key).Order());
        Assert.All(publicEndpoints, e => Assert.NotNull(e.Endpoint.Metadata.GetMetadata<IAllowAnonymous>()));
    }

    [Fact]
    public void Tenant_endpoints_are_under_the_organization_route_and_declare_a_known_permission()
    {
        var tenantEndpoints = Endpoints().Where(e => Access(e.Endpoint) == EndpointAccess.Tenant).ToList();

        Assert.NotEmpty(tenantEndpoints);
        Assert.All(tenantEndpoints, e =>
        {
            Assert.Contains($" {EndpointAccessExtensions.TenantRoutePrefix}", e.Key, StringComparison.Ordinal);
            Assert.Contains(e.Endpoint.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission, Permissions.All);
            Assert.Null(e.Endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        });
    }

    [Fact]
    public void Nothing_else_is_routed_under_an_organization()
    {
        var outside = Endpoints()
            .Where(e => Access(e.Endpoint) != EndpointAccess.Tenant && e.Key.Contains("/api/orgs/{", StringComparison.Ordinal))
            .Select(e => e.Key)
            .ToList();

        Assert.True(outside.Count == 0, $"Organization routes must use MapTenantGroup(): {string.Join(", ", outside)}");
    }

    [Fact]
    public void Every_tenant_endpoint_has_a_matrix_case_and_every_case_an_endpoint()
    {
        var routed = Endpoints().Where(e => Access(e.Endpoint) == EndpointAccess.Tenant).Select(e => e.Key).ToHashSet();
        var cased = TenantEndpointCase.All.Select(c => c.Key).ToHashSet();

        Assert.True(routed.SetEquals(cased),
            $"Missing cases: {string.Join(", ", routed.Except(cased))}; stale cases: {string.Join(", ", cased.Except(routed))}");
    }

    private static EndpointAccess? Access(Endpoint endpoint) => endpoint.Metadata.GetMetadata<EndpointAccessMetadata>()?.Access;

    private static string Method(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? string.Join(",", methods) : "*";
}
