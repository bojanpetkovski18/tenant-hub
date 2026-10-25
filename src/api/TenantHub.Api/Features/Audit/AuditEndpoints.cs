using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Audit;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Audit;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapTenantGroup().MapGroup("/audit").WithTags("Audit");

        // Admins and Owners only (audit.read); a job's own history is GET /jobs/{id}/history for everyone who reads jobs.
        audit.MapGet("/", async (
                Guid? actor,
                string? action,
                string? entityType,
                Guid? entityId,
                DateOnly? from,
                DateOnly? to,
                string? before,
                int? pageSize,
                ListAuditEntriesQuery query,
                CancellationToken ct) =>
            TypedResults.Ok(await query.HandleAsync(new AuditListRequest(actor, action, entityType, entityId, from, to, before, pageSize), ct)))
            .RequirePermission(Permissions.AuditRead);

        return app;
    }
}
