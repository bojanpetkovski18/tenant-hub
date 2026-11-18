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
    decimal? QuotedAmount,
    int? EstimatedMinutes = null)
{
    public JobInput ToInput() => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount, EstimatedMinutes);
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
    uint Version,
    int? EstimatedMinutes = null)
{
    public JobInput ToInput() => new(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount, EstimatedMinutes);
}

public sealed record TransitionRequest(JobStatus To, [property: MaxLength(2000)] string? Reason);

/// <summary>A board move: <c>Version</c> is what the board showed; <c>MoveDueDate</c> answers "move the due date too?".</summary>
public sealed record ScheduleRequest(Guid? AssigneeUserId, DateOnly? ScheduledOn, uint Version, bool MoveDueDate = false);

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
                Guid? agreementId,
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
                    PageRequest.From(page, pageSize),
                    agreementId),
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

        // A board move (SPEC-V2 5.2): assignee and day in one transaction; managers only, like assigning anywhere.
        jobs.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleRequest request, JobCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.ScheduleAsync(id, new ScheduleInput(request.AssigneeUserId, request.ScheduledOn, request.Version, request.MoveDueDate), ct)))
            .RequirePermission(Permissions.JobsManage);

        // Skipping one visit of an agreement is an agreement decision (SPEC-V2 2.2, 7).
        jobs.MapPost("/{id:guid}/skip", async (Guid id, JobCommands commands, CancellationToken ct) =>
                TypedResults.Ok(await commands.SkipAsync(id, ct)))
            .RequirePermission(Permissions.AgreementsManage);

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
