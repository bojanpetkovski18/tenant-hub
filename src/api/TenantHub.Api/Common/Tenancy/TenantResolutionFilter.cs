using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Common.Auth;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Integrations;
using TenantHub.Application.Tenancy;
using TenantHub.Domain.Integrations;

namespace TenantHub.Api.Common.Tenancy;

/// <summary>
/// Runs before every tenant-scoped handler (SPEC 3.2, D006):
/// <list type="number">
/// <item>the organization comes from the route, never from the body, query string, headers or cookies;</item>
/// <item>no active membership → 404 <c>organization-not-found</c>, the same answer as for an organization that does
/// not exist, so other tenants' existence is not revealed;</item>
/// <item>missing permission → 403 <c>permission-denied</c>.</item>
/// </list>
/// A request authenticated with an API key (<see cref="ApiKeyMiddleware"/>) was already matched to this organization; it
/// enters as the key, with the key's fixed permissions instead of a role's (SPEC-V2 3.3).
/// Logs from the handler carry the organization and user ids.
/// <para>
/// The request keeps one database connection from resolution to the end (EF Core closes an explicitly opened
/// connection only when the context is disposed). Otherwise every query takes a connection from the pool, which
/// resets it, and the row-level security settings go out again before each one (D038): measured at about 6 ms
/// more per jobs list request, against about one extra round trip this way.
/// </para>
/// </summary>
public sealed partial class TenantResolutionFilter(
    ResolveTenantQuery resolve,
    ITenantContext tenant,
    TenantContext tenantContext,
    IAppDbContext db,
    ILogger<TenantResolutionFilter> logger) : IEndpointFilter
{
    public const string OrganizationNotFoundCode = "organization-not-found";
    public const string PermissionDeniedCode = "permission-denied";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var permission = http.GetEndpoint()?.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission
            ?? throw new InvalidOperationException($"Tenant-scoped endpoint {http.Request.Path} declares no permission.");

        await db.Database.OpenConnectionAsync(http.RequestAborted);
        var key = http.GetApiKey();
        if (!Guid.TryParse(http.Request.RouteValues["orgId"] as string, out var organizationId)
            || (key is not null ? !EnterAsKey(key, organizationId) : !await resolve.TryEnterAsync(organizationId, http.User.GetUserId(), http.RequestAborted)))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Organization not found.",
                extensions: new Dictionary<string, object?> { ["code"] = OrganizationNotFoundCode });
        }

        if (!tenant.HasPermission(permission))
        {
            LogPermissionDenied(logger, permission, organizationId);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "You do not have permission to do this in this organization.",
                extensions: new Dictionary<string, object?> { ["code"] = PermissionDeniedCode });
        }

        using (logger.BeginScope(new Dictionary<string, object> { ["OrganizationId"] = organizationId, ["UserId"] = tenant.UserId! }))
        {
            return await next(context);
        }
    }

    private bool EnterAsKey(ApiKeyIdentity key, Guid organizationId)
    {
        if (key.OrganizationId != organizationId)
        {
            return false;
        }

        tenantContext.EnterAsApiKey(organizationId, key.KeyId, key.Name, ApiKeyPermissions.For(key.Access), key.CreatorUserId);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {Permission} denied in organization {OrganizationId}.")]
    private static partial void LogPermissionDenied(ILogger logger, string permission, Guid organizationId);
}
