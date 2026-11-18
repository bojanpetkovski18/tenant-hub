using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Api.Features.Jobs;
using TenantHub.Application.Clients;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Requests;

public sealed record DeclineRequestRequest([property: MaxLength(5000)] string? Reason);

/// <summary>
/// The staff Requests inbox (SPEC-V2 5.5, 7): Owners and Admins (<c>portal.manage</c>) see the requests clients sent
/// through the portal, convert one into a job, or decline it with a reason.
/// </summary>
public static class RequestEndpoints
{
    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapTenantGroup().MapGroup("/requests").WithTags("Requests");

        requests.MapGet("/", async (string? status, Guid? clientId, int? page, int? pageSize, WorkRequestCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ListAsync(status, clientId, page, pageSize, ct)))
            .RequirePermission(Permissions.PortalManage);

        // The sidebar's count of open requests.
        requests.MapGet("/open-count", async (WorkRequestCommands commands, CancellationToken ct) =>
                TypedResults.Ok(new { open = await commands.OpenCountAsync(ct) }))
            .RequirePermission(Permissions.PortalManage);

        // The job form, prefilled from the request; the job is created and the request converted together. Creating a
        // job also needs jobs.create, which the handler checks (one declared permission per route, D028).
        requests.MapPost("/{id:guid}/convert", async (Guid id, JobRequest request, WorkRequestCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ConvertAsync(id, request.ToInput(), ct)))
            .RequirePermission(Permissions.PortalManage);

        requests.MapPost("/{id:guid}/decline", async (Guid id, DeclineRequestRequest request, WorkRequestCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.DeclineAsync(id, request.Reason, ct)))
            .RequirePermission(Permissions.PortalManage);

        return app;
    }
}
