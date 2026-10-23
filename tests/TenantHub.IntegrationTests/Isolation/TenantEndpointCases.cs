using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;
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

    /// <summary>A member created just for this request (role changes and removals use them up).</summary>
    FreshMember,

    /// <summary>The organization's standing pending invitation.</summary>
    Invitation,

    /// <summary>An invitation created just for this request (for revokes).</summary>
    FreshInvitation,

    /// <summary>The organization's standing job (A's is assigned to its Member).</summary>
    Job,

    /// <summary>A New job created just for this request, assigned to the caller's role when known, else the Owner.</summary>
    FreshJob,

    /// <summary>
    /// A new organization standing in for the requested one (for deletes, which use it up): like A (A's role users,
    /// same roles) when A is requested, owned by B's Owner when B is, and a random id otherwise.
    /// </summary>
    FreshOrganization,
}

/// <summary>The organization and resource a case is sent to.</summary>
public sealed record CaseInput(ApiTenants Tenants, Guid OrganizationId, Guid? ResourceId);

/// <summary>
/// One line per tenant-scoped endpoint: the cross-tenant matrix, the foreign-id matrix, the permission matrix and
/// the audit test run every line, and <see cref="EndpointCoverageTests"/> fails if an endpoint in the route table
/// has no line here. Later milestones add their endpoints here; the tests themselves stay unchanged.
/// </summary>
public sealed partial record TenantEndpointCase(string Method, string Pattern, string Permission)
{
    public HttpStatusCode Allowed { get; init; } = HttpStatusCode.OK;

    /// <summary>A successful call changes data and must write at least one audit entry.</summary>
    public bool Audited { get; init; }

    public CaseResource Resource { get; init; }

    /// <summary>Puts the resource in the state the call needs (for example archived, before a restore).</summary>
    public Func<CaseInput, Task>? Arrange { get; init; }

    public Func<CaseInput, Task<object>>? Body { get; init; }

    /// <summary>Undoes what the call (or its Arrange) did to the shared fixture; runs after every call, whatever the answer.</summary>
    public Func<CaseInput, Task>? Restore { get; init; }

    /// <summary>
    /// Why a state-changing endpoint writes no audit entry, reviewed case by case (the audit rule test accepts only
    /// these). Deleting an organization deletes its audit entries with it; the API logs the deletion instead.
    /// </summary>
    public string? NotAuditedBecause { get; init; }

    /// <summary>The code when the id belongs to another organization: the tenant filter makes it not exist.</summary>
    public string NotFoundCode => Resource switch
    {
        CaseResource.Client or CaseResource.FreshClient => "client-not-found",
        CaseResource.FreshMember => "member-not-found",
        CaseResource.Invitation or CaseResource.FreshInvitation => "invitation-not-found",
        CaseResource.Job or CaseResource.FreshJob => "job-not-found",
        _ => throw new InvalidOperationException($"{Key} takes no id."),
    };

    /// <summary>B's standing resource of this case's kind, for the foreign-id test.</summary>
    public Guid ForeignResource(ApiTenants tenants) => Resource switch
    {
        CaseResource.Client or CaseResource.FreshClient => tenants.ClientB,
        CaseResource.FreshMember => tenants.MemberB,
        CaseResource.Invitation or CaseResource.FreshInvitation => tenants.InvitationB,
        CaseResource.Job or CaseResource.FreshJob => tenants.JobB,
        _ => throw new InvalidOperationException($"{Key} takes no id."),
    };

    /// <summary>The route as the route table shows it, e.g. <c>GET /api/orgs/{orgId:guid}/members</c>.</summary>
    public string Key => $"{Method} {EndpointAccessExtensions.TenantRoutePrefix}{Pattern}";

    public bool TargetsResource => IdSegment().IsMatch(Pattern);

    public override string ToString() => Key;

    public static IReadOnlyList<TenantEndpointCase> All { get; } =
    [
        new("GET", "/", Permissions.OrgRead),
        new("PATCH", "/", Permissions.OrgManage)
        {
            Audited = true,
            Body = _ => Task.FromResult<object>(new { name = $"Alpha Cleaning {Guid.NewGuid():N}", timeZone = "Europe/Amsterdam" }),
        },
        new("PUT", "/plan", Permissions.BillingManage)
        {
            // A is on Team for the call, so moving it to Business is a change; it is put back on Business afterwards.
            Audited = true,
            Arrange = input => input.OrganizationId == input.Tenants.OrgA ? input.Tenants.SetPlanAsync(input.OrganizationId, Plan.Team) : Task.CompletedTask,
            Body = _ => Task.FromResult<object>(new { plan = "business" }),
            Restore = input => input.OrganizationId == input.Tenants.OrgA ? input.Tenants.SetPlanAsync(input.OrganizationId, Plan.Business) : Task.CompletedTask,
        },
        new("DELETE", "/", Permissions.OrgDelete)
        {
            Resource = CaseResource.FreshOrganization,
            Allowed = HttpStatusCode.NoContent,
            NotAuditedBecause = "the organization's audit entries are deleted with it; the API logs the deletion instead",
            Body = async input => new { confirmName = await input.Tenants.OrganizationNameAsync(input.OrganizationId) },
        },
        new("GET", "/members", Permissions.OrgRead),
        new("PATCH", "/members/{userId:guid}", Permissions.MembersManage)
        {
            Resource = CaseResource.FreshMember,
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
            Body = _ => Task.FromResult<object>(new { role = "viewer" }),
        },
        new("DELETE", "/members/{userId:guid}", Permissions.MembersManage)
        {
            Resource = CaseResource.FreshMember,
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
        },
        new("DELETE", "/members/me", Permissions.OrgRead)
        {
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
            Restore = input => input.Tenants.RestoreMembersOfAAsync(),
        },

        new("GET", "/invitations/", Permissions.MembersManage),
        new("POST", "/invitations/", Permissions.MembersManage)
        {
            Audited = true,
            Body = _ => Task.FromResult<object>(new { email = $"invitee-{Guid.NewGuid():N}@test.example", role = "member" }),
        },
        new("POST", "/invitations/{id:guid}/resend", Permissions.MembersManage) { Resource = CaseResource.Invitation, Audited = true },
        new("DELETE", "/invitations/{id:guid}", Permissions.MembersManage)
        {
            Resource = CaseResource.FreshInvitation,
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
        },

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

        new("GET", "/jobs/", Permissions.JobsRead),
        new("POST", "/jobs/", Permissions.JobsCreate)
        {
            Allowed = HttpStatusCode.Created,
            Audited = true,
            Body = input => Task.FromResult<object>(new { title = $"Matrix job {Guid.NewGuid():N}", clientId = input.Tenants.ClientIn(input.OrganizationId) }),
        },
        new("GET", "/jobs/{id:guid}", Permissions.JobsRead) { Resource = CaseResource.Job },
        new("GET", "/jobs/{id:guid}/history", Permissions.JobsRead) { Resource = CaseResource.Job },
        new("PUT", "/jobs/{id:guid}", Permissions.JobsWork)
        {
            // Assigned to the caller, so the resource rule (jobs.work only on your own jobs) passes for every role that has jobs.work.
            Resource = CaseResource.FreshJob,
            Audited = true,
            Body = async input =>
            {
                var details = await input.Tenants.JobDetailsAsync(input.ResourceId!.Value);
                return new
                {
                    title = details.Title,
                    description = $"Note {Guid.NewGuid():N}",
                    clientId = details.ClientId,
                    assigneeUserId = details.AssigneeUserId,
                    priority = "high",
                    version = await input.Tenants.JobVersionAsync(input.ResourceId!.Value),
                };
            },
        },
        new("POST", "/jobs/{id:guid}/transitions", Permissions.JobsWork)
        {
            Resource = CaseResource.FreshJob,
            Audited = true,
            Body = _ => Task.FromResult<object>(new { to = "inProgress" }),
        },
        new("DELETE", "/jobs/{id:guid}", Permissions.JobsManage)
        {
            Resource = CaseResource.FreshJob,
            Allowed = HttpStatusCode.NoContent,
            Audited = true,
        },

        new("GET", "/audit/", Permissions.AuditRead),

        // Test-only endpoints (ApiFactory): the 403 path, and the guard stopping a deliberate bug (500).
        new("GET", ApiFactory.OwnerOnlyPattern, Permissions.BillingManage),
        new("POST", ApiFactory.IsolationViolationPattern, Permissions.OrgRead) { Allowed = HttpStatusCode.InternalServerError },
    ];

    /// <summary>
    /// Resolves the <c>{id}</c> (<paramref name="resourceId"/> when given, else this organization's resource) and runs
    /// <see cref="Arrange"/>. Tests that compare snapshots take them after this, so setup is not mistaken for a leak.
    /// </summary>
    public async Task<CaseInput> PrepareAsync(ApiTenants tenants, Guid organizationId, Guid? resourceId = null, Role? caller = null)
    {
        if (Resource == CaseResource.FreshOrganization)
        {
            organizationId = await tenants.CreateOrganizationLikeAsync(organizationId);
        }

        resourceId ??= Resource switch
        {
            CaseResource.Client => tenants.ClientIn(organizationId),
            CaseResource.FreshClient => await tenants.CreateClientAsync(organizationId),
            CaseResource.FreshMember => await tenants.CreateMemberAsync(organizationId),
            CaseResource.Invitation => tenants.InvitationIn(organizationId),
            CaseResource.FreshInvitation => await tenants.CreateInvitationAsync(organizationId),
            CaseResource.Job => tenants.JobIn(organizationId),
            CaseResource.FreshJob => await tenants.CreateJobAsync(organizationId, caller),
            _ => null,
        };
        var input = new CaseInput(tenants, organizationId, resourceId);
        if (Arrange is not null)
        {
            await Arrange(input);
        }

        return input;
    }

    /// <summary><paramref name="caller"/> is the role of A's member sending, for resources that must belong to them.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        BrowserClient client, ApiTenants tenants, Guid organizationId, Guid? resourceId = null, Role? caller = null) =>
        await SendAsync(client, await PrepareAsync(tenants, organizationId, resourceId, caller));

    public async Task<HttpResponseMessage> SendAsync(BrowserClient client, CaseInput input)
    {
        var path = $"/api/orgs/{input.OrganizationId}{IdSegment().Replace(Pattern, input.ResourceId?.ToString() ?? "")}";
        if (Pattern == ApiFactory.IsolationViolationPattern)
        {
            path += $"?targetOrganizationId={input.Tenants.OrgB}";
        }

        var body = Body is null ? new { } : await Body(input);
        var ct = TestContext.Current.CancellationToken;
        var response = Method switch
        {
            "GET" => await client.Http.GetAsync(path, ct),
            "POST" => await client.PostJsonAsync(path, body),
            "PUT" => await client.SendJsonAsync(HttpMethod.Put, path, body),
            "PATCH" => await client.PatchJsonAsync(path, body),
            "DELETE" => await client.SendJsonAsync(HttpMethod.Delete, path, Body is null ? null : body),
            _ => throw new NotSupportedException(Method),
        };

        if (Restore is not null)
        {
            await Restore(input);
        }

        return response;
    }

    /// <summary>The route's id parameter, whatever it is called (<c>{id:guid}</c>, <c>{userId:guid}</c>).</summary>
    [GeneratedRegex(@"\{\w+:guid\}")]
    private static partial Regex IdSegment();
}
