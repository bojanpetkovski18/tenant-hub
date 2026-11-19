using Microsoft.EntityFrameworkCore;
using TenantHub.Api.Common.Auth;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Portal;

namespace TenantHub.Api.Common.Tenancy;

/// <summary>
/// Runs before every portal-scoped handler (SPEC-V2 3.2), the portal's <see cref="TenantResolutionFilter"/>:
/// <list type="number">
/// <item>the organization, client and contact come from the signed session cookie only;</item>
/// <item>a removed contact, an archived or deleted client, or a plan without the portal ends the session: the cookie
/// is cleared and the answer is 401 <c>portal-session-ended</c>, on the very next request;</item>
/// <item>then the scope is the contact's: no staff permissions, and the database shows only their client's rows.</item>
/// </list>
/// </summary>
public sealed partial class PortalResolutionFilter(
    PortalSessions sessions,
    IAppDbContext db,
    ILogger<PortalResolutionFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.GetPortalIdentity() is not { } identity)
        {
            // The portal policy already refused anything else; this keeps the filter safe on its own.
            return Ended();
        }

        // One connection for the request, as for tenant resolution (row-level security settings go out once).
        await db.Database.OpenConnectionAsync(http.RequestAborted);
        if (!await sessions.TryEnterAsync(identity, http.RequestAborted))
        {
            LogEnded(logger, identity.ContactId, identity.OrganizationId);
            await PortalAuth.SignOutAsync(http);
            return Ended();
        }

        using (logger.BeginScope(new Dictionary<string, object> { ["OrganizationId"] = identity.OrganizationId, ["ContactId"] = identity.ContactId }))
        {
            return await next(context);
        }
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Ended() => TypedResults.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Your portal session has ended. Sign in again.",
        extensions: new Dictionary<string, object?> { ["code"] = PortalSessions.SessionEndedCode });

    [LoggerMessage(Level = LogLevel.Information, Message = "Portal session of contact {ContactId} in organization {OrganizationId} ended: the contact, client or portal is no longer active.")]
    private static partial void LogEnded(ILogger logger, Guid contactId, Guid organizationId);
}
