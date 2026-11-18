using System.ComponentModel.DataAnnotations;
using TenantHub.Api.Common.Tenancy;
using TenantHub.Application.Agreements;
using TenantHub.Application.Paging;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Api.Features.Agreements;

// Shape only; the rules (SPEC-V2 5.1) are the domain's, which reports every field at once.
public sealed record RecurrenceRequest(
    RecurrenceKind? Kind,
    int? Interval,
    [property: MaxLength(7)] IReadOnlyList<DayOfWeek>? Weekdays,
    int? Day,
    bool? LastDay,
    WeekOfMonth? Week,
    DayOfWeek? Weekday)
{
    public RecurrenceInput ToInput() => new(Kind, Interval, Weekdays, Day, LastDay, Week, Weekday);
}

public sealed record AgreementRequest(
    Guid? ClientId,
    [property: Required(AllowEmptyStrings = true), MaxLength(500)] string Title,
    [property: MaxLength(10000)] string? Description,
    Guid? AssigneeUserId,
    JobPriority? Priority,
    decimal? QuotedAmount,
    int? EstimatedMinutes,
    RecurrenceRequest? Recurrence,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    int? MaxVisits,
    uint? Version)
{
    public AgreementInput ToInput() =>
        new(ClientId, Title, Description, AssigneeUserId, Priority, QuotedAmount, EstimatedMinutes, Recurrence?.ToInput(), StartsOn, EndsOn, MaxVisits);
}

public sealed record PreviewRequestBody(RecurrenceRequest? Recurrence, DateOnly? StartsOn, DateOnly? EndsOn, int? MaxVisits);

public sealed record PreviewResponse(IReadOnlyList<DateOnly> Dates);

/// <summary>Recurring service agreements (SPEC-V2 4.2, 7). The client of an agreement is fixed at creation.</summary>
public static class AgreementEndpoints
{
    public static IEndpointRouteBuilder MapAgreementEndpoints(this IEndpointRouteBuilder app)
    {
        var agreements = app.MapTenantGroup().MapGroup("/agreements").WithTags("Agreements");

        agreements.MapGet("/", async (Guid? clientId, string[]? status, string? q, int? page, int? pageSize, AgreementQueries query, CancellationToken ct) =>
                TypedResults.Ok(await query.ListAsync(
                    new ListAgreementsRequest(
                        clientId,
                        [.. (status ?? []).Select(s => Enum.TryParse<AgreementStatus>(s, ignoreCase: true, out var v) ? (AgreementStatus?)v : null).OfType<AgreementStatus>().Distinct()],
                        q,
                        PageRequest.From(page, pageSize)),
                    ct)))
            .RequirePermission(Permissions.AgreementsRead);

        agreements.MapPost("/", async (AgreementRequest request, HttpContext context, AgreementCommands commands, AgreementQueries query, CancellationToken ct) =>
            {
                var id = await commands.CreateAsync(request.ToInput(), ct);
                return TypedResults.Created($"{context.Request.Path.Value!.TrimEnd('/')}/{id}", await query.GetAsync(id, ct));
            })
            .RequirePermission(Permissions.AgreementsManage);

        // Read: it saves nothing, and the form shows the dates to anyone who can see agreements.
        agreements.MapPost("/preview", async (PreviewRequestBody request, AgreementQueries query, CancellationToken ct) =>
                TypedResults.Ok(new PreviewResponse(await query.PreviewAsync(
                    new PreviewRequest(request.Recurrence?.ToInput(), request.StartsOn, request.EndsOn, request.MaxVisits), ct))))
            .RequirePermission(Permissions.AgreementsRead);

        agreements.MapGet("/{id:guid}", async (Guid id, AgreementQueries query, CancellationToken ct) =>
                TypedResults.Ok(await query.GetAsync(id, ct)))
            .RequirePermission(Permissions.AgreementsRead);

        agreements.MapPut("/{id:guid}", async (Guid id, AgreementRequest request, AgreementCommands commands, AgreementQueries query, CancellationToken ct) =>
            {
                await commands.UpdateAsync(id, request.Version ?? 0, request.ToInput(), ct);
                return TypedResults.Ok(await query.GetAsync(id, ct));
            })
            .RequirePermission(Permissions.AgreementsManage);

        agreements.MapPost("/{id:guid}/pause", async (Guid id, AgreementCommands commands, AgreementQueries query, CancellationToken ct) =>
            {
                await commands.PauseAsync(id, ct);
                return TypedResults.Ok(await query.GetAsync(id, ct));
            })
            .RequirePermission(Permissions.AgreementsManage);

        agreements.MapPost("/{id:guid}/resume", async (Guid id, AgreementCommands commands, AgreementQueries query, CancellationToken ct) =>
            {
                await commands.ResumeAsync(id, ct);
                return TypedResults.Ok(await query.GetAsync(id, ct));
            })
            .RequirePermission(Permissions.AgreementsManage);

        agreements.MapPost("/{id:guid}/end", async (Guid id, AgreementCommands commands, AgreementQueries query, CancellationToken ct) =>
            {
                await commands.EndAsync(id, ct);
                return TypedResults.Ok(await query.GetAsync(id, ct));
            })
            .RequirePermission(Permissions.AgreementsManage);

        return app;
    }
}
