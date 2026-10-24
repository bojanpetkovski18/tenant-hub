using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Jobs;
using TenantHub.Application.Paging;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Jobs;

// Shape only; the job rules (SPEC 5.4) are checked by the domain, which reports every field at once.
public sealed record JobRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(500)] string Title,
    [property: MaxLength(10000)] string? Description,
    Guid? ClientId,
    Guid? AssigneeUserId,
    JobPriority? Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount)
{
    public JobInput ToInput() => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount);
}

/// <summary><see cref="Version"/> is the value the client last read; a newer one in the database means 409.</summary>
public sealed record UpdateJobRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(500)] string Title,
    [property: MaxLength(10000)] string? Description,
    Guid? ClientId,
    Guid? AssigneeUserId,
    JobPriority? Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount,
    uint Version)
{
    public JobInput ToInput() => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount);
}

public sealed record TransitionRequest(JobStatus To, [property: MaxLength(2000)] string? Reason);

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapTenantGroup().MapGroup("/jobs").WithTags("Jobs");

        jobs.MapGet("/", async (
                string? q,
                string[]? status,
                string[]? priority,
                string? assignee,
                Guid? clientId,
                bool? overdue,
                DateOnly? dueFrom,
                DateOnly? dueTo,
                DateOnly? completedFrom,
                string? sort,
                int? page,
                int? pageSize,
                ListJobsQuery query,
                CancellationToken ct) =>
            TypedResults.Ok(await query.HandleAsync(
                new ListJobsRequest(
                    q,
                    Parse<JobStatus>(status),
                    Parse<JobPriority>(priority),
                    assignee,
                    clientId,
                    overdue ?? false,
                    dueFrom,
                    dueTo,
                    completedFrom,
                    sort,
                    PageRequest.From(page, pageSize)),
                ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapPost("/", async (JobRequest request, HttpContext context, JobCommands commands, CancellationToken ct) =>
            {
                var job = await commands.CreateAsync(request.ToInput(), ct);
                return TypedResults.Created($"{context.Request.Path.Value!.TrimEnd('/')}/{job.Id}", job);
            })
            .RequirePermission(Permissions.JobsCreate);

        jobs.MapGet("/{id:guid}", async (Guid id, GetJobQuery query, CancellationToken ct) =>
                TypedResults.Ok(await query.HandleAsync(id, ct)))
            .RequirePermission(Permissions.JobsRead);

        jobs.MapGet("/{id:guid}/history", async (Guid id, JobHistoryQuery query, CancellationToken ct) =>
                TypedResults.Ok(await query.HandleAsync(id, ct)))
            .RequirePermission(Permissions.JobsRead);

        // jobs.work is the route's permission; whether this job is the caller's is a rule of the domain (resource-level).
        jobs.MapPut("/{id:guid}", async (Guid id, UpdateJobRequest request, JobCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.UpdateAsync(id, request.Version, request.ToInput(), ct)))
            .RequirePermission(Permissions.JobsWork);

        jobs.MapPost("/{id:guid}/transitions", async (Guid id, TransitionRequest request, JobCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.TransitionAsync(id, request.To, request.Reason, ct)))
            .RequirePermission(Permissions.JobsWork);

        jobs.MapDelete("/{id:guid}", async (Guid id, JobCommands commands, CancellationToken ct) =>
            {
                await commands.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(Permissions.JobsManage);

        return app;
    }

    /// <summary>Repeated query values (<c>status=new&amp;status=scheduled</c>); unknown ones are ignored, like an unknown sort.</summary>
    private static List<T> Parse<T>(string[]? values)
        where T : struct, Enum =>
        [.. (values ?? []).Select(v => Enum.TryParse<T>(v, ignoreCase: true, out var parsed) ? (T?)parsed : null).OfType<T>().Distinct()];
}
