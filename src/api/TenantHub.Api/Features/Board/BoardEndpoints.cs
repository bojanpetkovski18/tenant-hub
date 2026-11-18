using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Board;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Board;

/// <summary>
/// The dispatch board (SPEC-V2 4.3, 7): everyone who reads jobs sees it; moving cards uses
/// <c>POST /jobs/{id}/schedule</c>, which needs <c>jobs.manage</c>.
/// </summary>
public static class BoardEndpoints
{
    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapTenantGroup().MapGet("/board", async (DateOnly? week, Guid? clientId, string[]? status, BoardQuery query, CancellationToken ct) =>
                TypedResults.Ok(await query.HandleAsync(
                    new BoardRequest(
                        week,
                        clientId,
                        [.. (status ?? []).Select(s => Enum.TryParse<JobStatus>(s, ignoreCase: true, out var v) ? (JobStatus?)v : null).OfType<JobStatus>().Distinct()]),
                    ct)))
            .WithTags("Board")
            .RequirePermission(Permissions.JobsRead);

        return app;
    }
}
