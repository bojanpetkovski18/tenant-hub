using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Auth;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Auth;
using TenantHub.Application.Organizations;
using TenantHub.Domain.Memberships;
using TenantHub.Domain.Organizations;

namespace TenantHub.Api.Features.Organizations;

public sealed record CreateOrganizationRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string Name,
    [property: Required(AllowEmptyStrings = true), MaxLength(64)] string TimeZone);

public sealed record OrganizationResponse(Guid Id, string Name, string Slug, Plan Plan, string TimeZone, DateTimeOffset CreatedAt);

public sealed record ChangeRoleRequest(Role? Role);

public sealed record MemberResponse(
    Guid UserId, string DisplayName, string Email, Role Role, DateTimeOffset JoinedAt,
    bool IsYou, bool CanChangeRole, bool CanRemove, bool CanLeave)
{
    public static MemberResponse From(MemberSummary m) =>
        new(m.UserId, m.DisplayName, m.Email, m.Role, m.JoinedAt, m.IsYou, m.CanChangeRole, m.CanRemove, m.CanLeave);
}

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        // User-scoped: creating an organization needs no membership.
        app.MapPost("/api/orgs", async (CreateOrganizationRequest request, HttpContext context, CreateOrganizationHandler handler, CancellationToken ct) =>
            {
                var membership = await handler.HandleAsync(
                    new CreateOrganizationCommand(context.User.GetUserId(), request.Name, request.TimeZone), ct);
                return TypedResults.Created($"/api/orgs/{membership.OrganizationId}", MembershipResponse.From(membership));
            })
            .WithTags("Organizations")
            .UserScoped();

        // Tenant-scoped: membership and permission are checked before each handler (TenantResolutionFilter).
        var organization = app.MapTenantGroup().WithTags("Organization");

        organization.MapGet("/", async (GetOrganizationQuery query, CancellationToken ct) =>
            {
                var o = await query.HandleAsync(ct);
                return TypedResults.Ok(new OrganizationResponse(o.Id, o.Name, o.Slug, o.Plan, o.TimeZone, o.CreatedAt));
            })
            .RequirePermission(Permissions.OrgRead);

        organization.MapGet("/members", async (ListMembersQuery query, CancellationToken ct) =>
            {
                var members = await query.HandleAsync(ct);
                return TypedResults.Ok(members.Select(MemberResponse.From).ToList());
            })
            .RequirePermission(Permissions.OrgRead);

        organization.MapPatch("/members/{userId:guid}", async (Guid userId, ChangeRoleRequest request, MemberCommands commands, CancellationToken ct) =>
            {
                // No body: what the caller may do next depends on everyone's roles, so the page reloads the list.
                await commands.ChangeRoleAsync(userId, request.Role, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.MembersManage);

        organization.MapDelete("/members/{userId:guid}", async (Guid userId, MemberCommands commands, CancellationToken ct) =>
            {
                await commands.RemoveAsync(userId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.MembersManage);

        // Leaving needs no management permission: every member may leave (unless the last Owner, SPEC 5.2). A route
        // of its own, so each route declares one permission (D028).
        organization.MapDelete("/members/me", async (MemberCommands commands, CancellationToken ct) =>
            {
                await commands.LeaveAsync(ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.OrgRead);

        return app;
    }
}
