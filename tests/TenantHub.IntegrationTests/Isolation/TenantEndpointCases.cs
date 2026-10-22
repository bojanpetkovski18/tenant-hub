using System.Net;
using System.Net.Http.Json;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Isolation;

/// <summary>What an endpoint's <c>{id}</c> refers to in a test run.</summary>
public enum CaseResource
{
    None,

    /// <summary>The organization's standing client (A's or B's).</summary>
    Client,

    /// <summary>A client created just for this request (for deletes, which use it up).</summary>
    FreshClient,
}

/// <summary>The organization and resource a case is sent to.</summary>
public sealed record CaseInput(ApiTenants Tenants, Guid OrganizationId, Guid? ResourceId);

/// <summary>
/// One line per tenant-scoped endpoint: the cross-tenant matrix, the foreign-id matrix, the permission matrix and
/// the audit test run every line, and <see cref="EndpointCoverageTests"/> fails if an endpoint in the route table
/// has no line here. Later milestones add their endpoints here; the tests themselves stay unchanged.
/// </summary>
public sealed record TenantEndpointCase(string Method, string Pattern, string Permission)
{
    private const string IdSegment = "{id:guid}";

    public HttpStatusCode Allowed { get; init; } = HttpStatusCode.OK;

    /// <summary>A successful call changes data and must write at least one audit entry.</summary>
    public bool Audited { get; init; }

    public CaseResource Resource { get; init; }

    /// <summary>Puts the resource in the state the call needs (for example archived, before a restore).</summary>
    public Func<CaseInput, Task>? Arrange { get; init; }

    public Func<CaseInput, Task<object>>? Body { get; init; }

    /// <summary>The route as the route table shows it, e.g. <c>GET /api/orgs/{orgId:guid}/members</c>.</summary>
    public string Key => $"{Method} {EndpointAccessExtensions.TenantRoutePrefix}{Pattern}";

    public bool TargetsResource => Pattern.Contains(IdSegment, StringComparison.Ordinal);

    public override string ToString() => Key;

    public static IReadOnlyList<TenantEndpointCase> All { get; } =
    [
        new("GET", "/", Permissions.OrgRead),
        new("GET", "/members", Permissions.OrgRead),

        new("GET", "/clients/", Permissions.ClientsRead),
        new("POST", "/clients/", Permissions.ClientsWrite)
        {
            Allowed = HttpStatusCode.Created,
            Audited = true,
            Body = _ => Task.FromResult<object>(new { name = $"Matrix client {Guid.NewGuid():N}" }),
        },
        new("GET", "/clients/{id:guid}", Permissions.ClientsRead) { Resource = CaseResource.Client },
        new("PUT", "/clients/{id:guid}", Permissions.ClientsWrite)
        {
            Resource = CaseResource.Client,
            Audited = true,
            Body = async input =>
            {
                var (name, version) = await input.Tenants.ClientStateAsync(input.ResourceId!.Value);
                return new { name, notes = $"Note {Guid.NewGuid():N}", version };
            },
        },
        new("POST", "/clients/{id:guid}/archive", Permissions.ClientsWrite)
        {
            Resource = CaseResource.Client,
            Audited = true,
            Arrange = input => input.Tenants.SetArchivedAsync(input.ResourceId!.Value, archived: false),
        },
        new("POST", "/clients/{id:guid}/restore", Permissions.ClientsWrite)
        {
            Resource = CaseResource.Client,
            Audited = true,
            Arrange = input => input.Tenants.SetArchivedAsync(input.ResourceId!.Value, archived: true),
        },
        new("DELETE", "/clients/{id:guid}", Permissions.ClientsDelete)
        {
            Resource = CaseResource.FreshClient,
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
        },

        // Test-only endpoints (ApiFactory): the 403 path, and the guard stopping a deliberate bug (500).
        new("GET", ApiFactory.OwnerOnlyPattern, Permissions.BillingManage),
        new("POST", ApiFactory.IsolationViolationPattern, Permissions.OrgRead) { Allowed = HttpStatusCode.InternalServerError },
    ];

    /// <summary>
    /// Resolves the <c>{id}</c> (<paramref name="resourceId"/> when given, else this organization's resource) and runs
    /// <see cref="Arrange"/>. Tests that compare snapshots take them after this, so setup is not mistaken for a leak.
    /// </summary>
    public async Task<CaseInput> PrepareAsync(ApiTenants tenants, Guid organizationId, Guid? resourceId = null)
    {
        resourceId ??= Resource switch
        {
            CaseResource.Client => tenants.ClientIn(organizationId),
            CaseResource.FreshClient => await tenants.CreateClientAsync(organizationId),
            _ => null,
        };
        var input = new CaseInput(tenants, organizationId, resourceId);
        if (Arrange is not null && resourceId is not null)
        {
            await Arrange(input);
        }

        return input;
    }

    public async Task<HttpResponseMessage> SendAsync(BrowserClient client, ApiTenants tenants, Guid organizationId, Guid? resourceId = null) =>
        await SendAsync(client, await PrepareAsync(tenants, organizationId, resourceId));

    public async Task<HttpResponseMessage> SendAsync(BrowserClient client, CaseInput input)
    {
        var path = $"/api/orgs/{input.OrganizationId}{Pattern.Replace(IdSegment, input.ResourceId?.ToString(), StringComparison.Ordinal)}";
        if (Pattern == ApiFactory.IsolationViolationPattern)
        {
            path += $"?targetOrganizationId={input.Tenants.OrgB}";
        }

        var body = Body is null ? new { } : await Body(input);
        var ct = TestContext.Current.CancellationToken;
        return Method switch
        {
            "GET" => await client.Http.GetAsync(path, ct),
            "POST" => await client.PostJsonAsync(path, body),
            "PUT" => await client.SendJsonAsync(HttpMethod.Put, path, body),
            "DELETE" => await client.SendJsonAsync(HttpMethod.Delete, path, null),
            _ => throw new NotSupportedException(Method),
        };
    }
}
