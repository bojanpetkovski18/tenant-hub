using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Organizations;

namespace TenantHub.Api.Features.Organizations;

public sealed record CreateOrganizationRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string Name,
    [property: Required(AllowEmptyStrings = true), MaxLength(64)] string TimeZone);

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var orgs = app.MapGroup("/api/orgs").WithTags("Organizations");

        // User-scoped: creating an organization needs no membership. Tenant-scoped routes under
        // /api/orgs/{orgId} arrive with the tenant context in M03.
        orgs.MapPost("/", async (CreateOrganizationRequest request, HttpContext context, CreateOrganizationHandler handler, CancellationToken ct) =>
        {
            var membership = await handler.HandleAsync(
                new CreateOrganizationCommand(context.User.GetUserId(), request.Name, request.TimeZone), ct);
            return TypedResults.Created($"/api/orgs/{membership.OrganizationId}", MembershipResponse.From(membership));
        });

        return app;
    }
}
