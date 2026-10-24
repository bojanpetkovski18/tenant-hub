using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Dashboard;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Organizations;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // Every member sees it; recent activity is included only for audit.read (US-60).
        app.MapTenantGroup().MapGet("/dashboard", async (DashboardQuery query, CancellationToken ct) =>
                TypedResults.Ok(await query.HandleAsync(ct)))
            .WithTags("Dashboard")
            .RequirePermission(Permissions.OrgRead);

        return app;
    }
}
