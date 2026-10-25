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

// Shape only; the domain checks the name and the IANA time zone and reports both at once.
public sealed record UpdateOrganizationRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(200)] string Name,
    [property: Required(AllowEmptyStrings = true), MaxLength(64)] string TimeZone);

public sealed record ChangePlanRequest(Plan? Plan);

public sealed record DeleteOrganizationRequest(string? ConfirmName);

public sealed record ChangeRoleRequest(Role? Role);

public sealed record MemberResponse(
    Guid UserId, string DisplayName, string Email, Role Role, DateTimeOffset JoinedAt,
    bool IsYou, bool CanChangeRole, bool CanRemove, bool CanLeave)
{
    public static MemberResponse From(MemberSummary m) =>
        new(m.UserId, m.DisplayName, m.Email, m.Role, m.JoinedAt, m.IsYou, m.CanChangeRole, m.CanRemove, m.CanLeave);
}

public static partial class OrganizationEndpoints
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
                TypedResults.Ok(await query.HandleAsync(ct)))
            .RequirePermission(Permissions.OrgRead);

        organization.MapPatch("/", async (UpdateOrganizationRequest request, OrganizationCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.UpdateSettingsAsync(request.Name, request.TimeZone, ct)))
            .RequirePermission(Permissions.OrgManage);

        // Demo billing (SPEC 4.1): no payment; a downgrade that does not fit is 409 with the blocking limits.
        organization.MapPut("/plan", async (ChangePlanRequest request, OrganizationCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ChangePlanAsync(request.Plan, ct)))
            .RequirePermission(Permissions.BillingManage);

        organization.MapDelete("/", async (
                HttpContext context,
                OrganizationCommands commands,
                ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                // The body is read here rather than bound: a bound JSON body makes routing skip the endpoint when a
                // request has none, and the caller would get the not-found fallback instead of a 400 on confirmName.
                var deleted = await commands.DeleteAsync((await ReadConfirmationAsync(context, ct))?.ConfirmName, ct);
                // Its audit entries are deleted with it (cascade), so the deletion is recorded here instead.
                LogDeleted(loggers.CreateLogger("TenantHub.Organizations"), deleted.Id, deleted.Slug, context.User.GetUserId(), deleted.Members);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.OrgDelete);

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

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Organization {OrganizationId} ({Slug}) was deleted by user {UserId} (active members: {Members}).")]
    private static partial void LogDeleted(ILogger logger, Guid organizationId, string slug, Guid userId, int members);

    /// <summary>The optional confirmation body; malformed or missing JSON counts as no confirmation (400 on confirmName).</summary>
    private static async Task<DeleteOrganizationRequest?> ReadConfirmationAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<DeleteOrganizationRequest>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
