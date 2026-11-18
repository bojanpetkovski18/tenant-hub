using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Application.Paging;
using TenantHub.Domain;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Memberships;

namespace TenantHub.Application.Agreements;

public sealed record RecurrenceView(RecurrenceKind Kind, int Interval, IReadOnlyList<DayOfWeek> Weekdays, int? Day, bool LastDay, WeekOfMonth? Week, DayOfWeek? Weekday)
{
    public static RecurrenceView From(Recurrence r) => new(r.Kind, r.Interval, r.Weekdays, r.Day, r.LastDay, r.Week, r.Weekday);
}

public sealed record AgreementListItem(
    Guid Id,
    string Title,
    Guid ClientId,
    string ClientName,
    string Schedule,
    AgreementStatus Status,
    GenerationHold? PausedReason,
    string? AssigneeName,
    DateOnly? NextVisitOn);

public sealed record VisitItem(Guid Id, string Label, DateOnly OccurrenceOn, DateOnly? ScheduledOn, JobStatus Status, string? AssigneeName, bool ChangedByHand);

/// <summary>
/// One agreement as its page needs it (SPEC-V2 6): the template, the schedule, the generation state, the next visits,
/// and the visits changed by hand, which edits no longer reach. <see cref="CanManage"/> drives the page's buttons.
/// </summary>
public sealed record AgreementView(
    Guid Id,
    Guid ClientId,
    string ClientName,
    string Title,
    string? Description,
    Guid? AssigneeUserId,
    string? AssigneeName,
    JobPriority Priority,
    decimal? QuotedAmount,
    int? EstimatedMinutes,
    RecurrenceView Recurrence,
    string Schedule,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    int? MaxVisits,
    AgreementStatus Status,
    GenerationHold? PausedReason,
    DateOnly? GeneratedThrough,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    IReadOnlyList<VisitItem> UpcomingVisits,
    IReadOnlyList<VisitItem> ChangedByHand,
    bool CanManage);

public sealed record ListAgreementsRequest(Guid? ClientId, IReadOnlyList<AgreementStatus> Statuses, string? Search, PageRequest Paging);

public sealed record PreviewRequest(RecurrenceInput? Recurrence, DateOnly? StartsOn, DateOnly? EndsOn, int? MaxVisits);

/// <summary>Agreement reads (US-70, US-71), in SQL with projections, inside the current organization.</summary>
public sealed class AgreementQueries(IAppDbContext db, ITenantContext tenant, IIdentityService identity, JobViews views)
{
    /// <summary>How many dates the form's live preview shows.</summary>
    public const int PreviewCount = 8;

    private const int UpcomingCount = 8;

    public async Task<PagedList<AgreementListItem>> ListAsync(ListAgreementsRequest request, CancellationToken cancellationToken)
    {
        var today = await views.TodayAsync(cancellationToken);
        var agreements = db.Agreements.AsNoTracking();
        if (request.ClientId is { } clientId)
        {
            agreements = agreements.Where(a => a.ClientId == clientId);
        }

        if (request.Statuses.Count > 0)
        {
            agreements = agreements.Where(a => request.Statuses.Contains(a.Status));
        }

        if (request.Search?.Trim() is { Length: > 0 } search)
        {
            // Parameterized; lower() on both sides, as the clients list searches.
            var term = search.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(...) and strpos; never runs as .NET string code.
            agreements = agreements.Where(a => a.Title.ToLower().Contains(term)
                || db.Clients.Any(c => c.Id == a.ClientId && c.Name.ToLower().Contains(term)));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await agreements.CountAsync(cancellationToken);
        var rows = await agreements
            .OrderBy(a => a.Status)
            .ThenBy(a => a.Title)
            .ThenBy(a => a.Id)
            .Skip(request.Paging.Skip)
            .Take(request.Paging.PageSize)
            .Select(a => new
            {
                Agreement = a,
                ClientName = db.Clients.Where(c => c.Id == a.ClientId).Select(c => c.Name).FirstOrDefault(),
                NextVisitOn = db.Jobs
                    .Where(j => j.AgreementId == a.Id && j.OccurrenceOn >= today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
                    .Min(j => j.OccurrenceOn),
            })
            .ToListAsync(cancellationToken);

        var people = await identity.FindProfilesAsync([.. rows.Select(r => r.Agreement.AssigneeUserId).OfType<Guid>().Distinct()], cancellationToken);
        return new PagedList<AgreementListItem>(
            [.. rows.Select(r => new AgreementListItem(
                r.Agreement.Id,
                r.Agreement.Title,
                r.Agreement.ClientId,
                r.ClientName ?? "Unknown client",
                r.Agreement.Recurrence.Describe(),
                r.Agreement.Status,
                r.Agreement.PausedReason,
                r.Agreement.AssigneeUserId is { } id ? people.GetValueOrDefault(id)?.DisplayName ?? "A former member" : null,
                r.NextVisitOn))],
            request.Paging.Page,
            request.Paging.PageSize,
            total);
    }

    public async Task<AgreementView> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var agreement = await db.Agreements.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw AgreementErrors.NotFound();
        var today = await views.TodayAsync(cancellationToken);
        var visits = await db.Jobs.AsNoTracking()
            .Where(j => j.AgreementId == id && j.OccurrenceOn >= today && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled)
            .OrderBy(j => j.OccurrenceOn)
            .Select(j => new { j.Id, j.Number, OccurrenceOn = j.OccurrenceOn!.Value, j.ScheduledOn, j.Status, j.AssigneeUserId, j.ChangedByHand })
            .ToListAsync(cancellationToken);
        var names = await views.NamesAsync([agreement.ClientId], [agreement.AssigneeUserId, .. visits.Select(v => v.AssigneeUserId)], cancellationToken);
        string? Name(Guid? userId) => userId is { } value ? names.GetValueOrDefault(value, "A former member") : null;
        var items = visits.Select(v => new VisitItem(v.Id, Job.FormatNumber(v.Number), v.OccurrenceOn, v.ScheduledOn, v.Status, Name(v.AssigneeUserId), v.ChangedByHand)).ToList();

        return new AgreementView(
            agreement.Id,
            agreement.ClientId,
            names.GetValueOrDefault(agreement.ClientId, "Unknown client"),
            agreement.Title,
            agreement.Description,
            agreement.AssigneeUserId,
            Name(agreement.AssigneeUserId),
            agreement.Priority,
            agreement.QuotedAmount,
            agreement.EstimatedMinutes,
            RecurrenceView.From(agreement.Recurrence),
            agreement.Recurrence.Describe(),
            agreement.StartsOn,
            agreement.EndsOn,
            agreement.MaxVisits,
            agreement.Status,
            agreement.PausedReason,
            agreement.GeneratedThrough,
            agreement.CreatedAt,
            agreement.UpdatedAt,
            agreement.Version,
            [.. items.Take(UpcomingCount)],
            [.. items.Where(v => v.ChangedByHand)],
            tenant.HasPermission(Permissions.AgreementsManage));
    }

    /// <summary>The next visit dates of a schedule, without saving (the form's live preview): from today or the start.</summary>
    public async Task<IReadOnlyList<DateOnly>> PreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        var today = await views.TodayAsync(cancellationToken);
        var input = new AgreementInput(null, "Preview", null, null, null, null, null, request.Recurrence, request.StartsOn ?? today, request.EndsOn, request.MaxVisits);
        var (_, schedule) = input.Validate();
        return [.. schedule.Occurrences(today, today.AddYears(5)).Take(PreviewCount)];
    }
}
