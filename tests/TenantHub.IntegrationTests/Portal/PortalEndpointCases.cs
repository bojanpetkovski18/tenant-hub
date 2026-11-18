using System.Net;
using System.Text.RegularExpressions;
using TenantHub.Api.Common.Auth;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Portal;

/// <summary>What a portal endpoint's <c>{id}</c> refers to.</summary>
public enum PortalResource
{
    None,

    /// <summary>The client's standing job, seen as a visit.</summary>
    Visit,

    /// <summary>The client's standing job, whose quote waits for a decision.</summary>
    Quote,
}

/// <summary>
/// One line per portal-scoped endpoint (SPEC-V2 3.2): the portal matrix runs every line, and
/// <c>EndpointCoverageTests</c> fails if a portal endpoint in the route table has none. Later milestones add theirs.
/// </summary>
public sealed partial record PortalEndpointCase(string Method, string Pattern)
{
    public HttpStatusCode Allowed { get; init; } = HttpStatusCode.OK;

    public PortalResource Resource { get; init; }

    public Func<PortalClient, object>? Body { get; init; }

    /// <summary>The answer includes the caller's own standing job: proves the endpoint shows data at all.</summary>
    public bool ShowsOwnJob { get; init; }

    /// <summary>A successful call changes data and must write an audit entry naming the contact (SPEC-V2 5.9).</summary>
    public bool Audited { get; init; }

    /// <summary>The route as the route table shows it, e.g. <c>GET /api/portal/visits/{id:guid}</c>.</summary>
    public string Key => $"{Method} {PortalAuth.ApiPath}{Pattern}";

    public string NotFoundCode => Resource switch
    {
        PortalResource.Visit => "visit-not-found",
        PortalResource.Quote => "quote-not-found",
        _ => throw new InvalidOperationException($"{Key} takes no id."),
    };

    public override string ToString() => Key;

    public static IReadOnlyList<PortalEndpointCase> All { get; } =
    [
        new("GET", "/visits") { ShowsOwnJob = true },
        new("GET", "/visits/{id:guid}") { Resource = PortalResource.Visit, ShowsOwnJob = true },
        new("GET", "/quotes") { ShowsOwnJob = true },
        new("POST", "/quotes/{id:guid}/approve") { Resource = PortalResource.Quote, ShowsOwnJob = true, Audited = true, Body = _ => new { comment = "Go ahead" } },
        new("POST", "/quotes/{id:guid}/decline") { Resource = PortalResource.Quote, ShowsOwnJob = true, Audited = true, Body = _ => new { comment = "Too dear" } },
    ];

    /// <summary>This client's resource of the case's kind.</summary>
    public static Guid ResourceOf(PortalResource resource, PortalClient client) => resource switch
    {
        PortalResource.Visit or PortalResource.Quote => client.JobId,
        _ => throw new InvalidOperationException("No resource."),
    };

    /// <summary>Sends the case as <paramref name="browser"/>, with <paramref name="owner"/>'s resource in the route.</summary>
    public async Task<HttpResponseMessage> SendAsync(BrowserClient browser, PortalClient caller, PortalClient owner)
    {
        var path = PortalAuth.ApiPath + IdSegment().Replace(Pattern, Resource == PortalResource.None ? "" : ResourceOf(Resource, owner).ToString());
        if (Method == "GET")
        {
            return await browser.Http.GetAsync(path, TestContext.Current.CancellationToken);
        }

        return await browser.PortalSendAsync(new HttpMethod(Method), path, Body?.Invoke(caller) ?? new { }, caller.Slug);
    }

    [GeneratedRegex(@"\{id(:guid)?\}")]
    private static partial Regex IdSegment();
}
