using TenantHub.Api.Common.Auth;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Tenancy;

namespace TenantHub.Api.Common.Tenancy;

/// <summary>
/// Runs before every tenant-scoped handler (SPEC 3.2, D006):
/// <list type="number">
/// <item>the organization comes from the route, never from the body, query string, headers or cookies;</item>
/// <item>no active membership → 404 <c>organization-not-found</c>, the same answer as for an organization that does
/// not exist, so other tenants' existence is not revealed;</item>
/// <item>missing permission → 403 <c>permission-denied</c>.</item>
/// </list>
/// Logs from the handler carry the organization and user ids.
/// </summary>
public sealed partial class TenantResolutionFilter(
    ResolveTenantQuery resolve,
    ITenantContext tenant,
    ILogger<TenantResolutionFilter> logger) : IEndpointFilter
{
    public const string OrganizationNotFoundCode = "organization-not-found";
    public const string PermissionDeniedCode = "permission-denied";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var permission = http.GetEndpoint()?.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission
            ?? throw new InvalidOperationException($"Tenant-scoped endpoint {http.Request.Path} declares no permission.");

        if (!Guid.TryParse(http.Request.RouteValues["orgId"] as string, out var organizationId)
            || !await resolve.TryEnterAsync(organizationId, http.User.GetUserId(), http.RequestAborted))
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {Permission} denied in organization {OrganizationId}.")]
    private static partial void LogPermissionDenied(ILogger logger, string permission, Guid organizationId);
}
