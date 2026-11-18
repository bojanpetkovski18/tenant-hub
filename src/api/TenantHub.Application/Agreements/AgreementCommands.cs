using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Application.Jobs;
using TenantHub.Application.Organizations;
using TenantHub.Domain;
using TenantHub.Domain.Agreements;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;
using TenantHub.Domain.Organizations;

namespace TenantHub.Application.Agreements;

public sealed record RecurrenceInput(
    RecurrenceKind? Kind,
    int? Interval,
    IReadOnlyList<DayOfWeek>? Weekdays,
    int? Day,
    bool? LastDay,
    WeekOfMonth? Week,
    DayOfWeek? Weekday)
{
    public Recurrence ToRecurrence() => Recurrence.Validate(Kind, Interval, Weekdays, Day, LastDay ?? false, Week, Weekday);
}

/// <summary>The fields as they arrive; <see cref="Validate"/> reports every invalid one at once.</summary>
public sealed record AgreementInput(
    Guid? ClientId,
    string? Title,
    string? Description,
    Guid? AssigneeUserId,
    JobPriority? Priority,
    decimal? QuotedAmount,
    int? EstimatedMinutes,
    RecurrenceInput? Recurrence,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    int? MaxVisits)
{
    public (AgreementTemplate Template, Schedule Schedule) Validate()
    {
        var errors = new Dictionary<string, string[]>();
        T? Collect<T>(Func<T> validate)
            where T : class
        {
            try
            {
                return validate();
            }
            catch (DomainValidationException exception)
            {
                foreach (var (field, messages) in exception.Errors)
                {
                    errors[field] = messages;
                }

                return null;
            }
        }

        var template = Collect(() => AgreementTemplate.Validate(Title, Description, AssigneeUserId, Priority, QuotedAmount, EstimatedMinutes));
        var recurrence = Collect(() => (Recurrence ?? new RecurrenceInput(null, null, null, null, null, null, null)).ToRecurrence());
        var schedule = Collect(() => Schedule.Validate(recurrence ?? Domain.Agreements.Recurrence.MonthlyOnDay(1), StartsOn, EndsOn, MaxVisits));
        return errors.Count == 0 ? (template!, schedule!) : throw new DomainValidationException(errors);
    }
}

public static class AgreementErrors
{
    public static DomainException NotFound() => new("agreement-not-found", "Agreement not found.", DomainErrorKind.NotFound);

    public static DomainException ConcurrencyConflict() =>
        new("concurrency-conflict", "Someone else changed this agreement after you opened it. Reload to see their changes.", DomainErrorKind.Conflict);
}

/// <summary>
/// Agreement commands (US-70, US-71). Everything that changes which visits should exist runs, in one transaction under
/// the organization lock, the change, the visits it implies and the audit entries: so a visit is generated once even
/// when the daily run, another instance and a user act at the same moment (D041), and the active-agreements and
/// open-jobs limits are counted by one writer at a time (D013).
/// </summary>
public sealed class AgreementCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IDatabaseLocks locks,
    PlanLimitsCheck limits,
    VisitGenerator generator,
    IRecurringWork recurring,
    JobViews views,
    TimeProvider time)
{
    public async Task<Guid> CreateAsync(AgreementInput input, CancellationToken cancellationToken)
    {
        var (template, schedule) = input.Validate();
        if (input.ClientId is not { } clientId || clientId == Guid.Empty)
        {
            throw new RequestValidationException(FieldErrors.From([("clientId", "Choose a client.")]));
        }

        await EnsureReferencesAsync(clientId, template.AssigneeUserId, cancellationToken);
        var names = await views.NamesAsync([clientId], [template.AssigneeUserId], cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var organization = await LockAsync(cancellationToken);
        var now = time.GetUtcNow();
        var agreement = Agreement.Create(organization.Id, clientId, template, schedule, organization.TodayAt(now), tenant.UserId!.Value, names, now);
        await limits.EnsureRoomForAsync(LimitKind.ActiveAgreements, cancellationToken);
        db.Agreements.Add(agreement);

        await generator.GenerateAsync(organization, [agreement], cancellationToken);
        await recurring.EnsureDailyGenerationAsync(organization.Id, VisitGenerator.NextDailyRun(organization, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return agreement.Id;
    }

    /// <summary>
    /// Applies the edit to the agreement and to its following visits: those on a date the schedule still has are
    /// updated, those on a dropped date withdrawn, and new dates generated. Visits changed by hand, past visits and
    /// visits in progress or closed are left as they are (SPEC-V2 5.1).
    /// </summary>
    public async Task UpdateAsync(Guid id, uint version, AgreementInput input, CancellationToken cancellationToken)
    {
        var (template, schedule) = input.Validate();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var organization = await LockAsync(cancellationToken);
        var agreement = await LoadAsync(id, cancellationToken);
        db.Entry(agreement).Property(a => a.Version).OriginalValue = version;
        if (template.AssigneeUserId != agreement.AssigneeUserId)
        {
            await EnsureReferencesAsync(clientId: null, template.AssigneeUserId, cancellationToken);
        }

        var now = time.GetUtcNow();
        var today = organization.TodayAt(now);
        var names = await views.NamesAsync([agreement.ClientId], [agreement.AssigneeUserId, template.AssigneeUserId], cancellationToken);
        if (!agreement.Update(template, schedule, names, now))
        {
            if (agreement.Version != version)
            {
                throw AgreementErrors.ConcurrencyConflict();
            }

            return;
        }

        var visits = await FutureVisitsAsync(agreement.Id, today, cancellationToken);
        var plan = VisitGeneration.AfterEdit(agreement, visits, today);
        foreach (var visit in plan.Update)
        {
            visit.FollowAgreement(agreement, names, now);
        }

        foreach (var visit in plan.Withdraw)
        {
            visit.WithdrawFromAgreement(agreement);
            db.Jobs.Remove(visit);
        }

        // Withdrawn rows go first, so their numbers' visits are gone before new dates are counted against the limit.
        await SaveAsync(cancellationToken);
        await generator.GenerateAsync(organization, [agreement], cancellationToken);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        var agreement = await LoadAsync(id, cancellationToken);
        agreement.Pause(time.GetUtcNow());
        await SaveAsync(cancellationToken);
    }

    /// <summary>Generation starts again from today; the past is never back-filled (SPEC-V2 5.1).</summary>
    public async Task ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var organization = await LockAsync(cancellationToken);
        var agreement = await LoadAsync(id, cancellationToken);
        if (await db.Clients.Where(c => c.Id == agreement.ClientId).Select(c => c.Status).SingleAsync(cancellationToken) != ClientStatus.Active)
        {
            throw new DomainException("client-archived", "The client of this agreement is archived. Restore the client first.");
        }

        if (agreement.Status == AgreementStatus.Paused)
        {
            await limits.EnsureRoomForAsync(LimitKind.ActiveAgreements, cancellationToken);
        }

        var now = time.GetUtcNow();
        agreement.Resume(now);
        await generator.GenerateAsync(organization, [agreement], cancellationToken);
        await recurring.EnsureDailyGenerationAsync(organization.Id, VisitGenerator.NextDailyRun(organization, now), cancellationToken);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Cancels the visits from today on that are New or Scheduled ("Agreement ended") and keeps the rest.</summary>
    public async Task EndAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var organization = await LockAsync(cancellationToken);
        var agreement = await LoadAsync(id, cancellationToken);
        var now = time.GetUtcNow();
        var today = organization.TodayAt(now);
        agreement.End(now);

        var actor = views.Actor();
        foreach (var visit in VisitGeneration.CancelledByEnd(agreement, await FutureVisitsAsync(agreement.Id, today, cancellationToken), today))
        {
            visit.TransitionTo(JobStatus.Cancelled, actor, "Agreement ended", now);
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Organization> LockAsync(CancellationToken cancellationToken)
    {
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        return await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, cancellationToken);
    }

    private Task<List<Job>> FutureVisitsAsync(Guid agreementId, DateOnly today, CancellationToken cancellationToken) =>
        db.Jobs.Where(j => j.AgreementId == agreementId && j.OccurrenceOn >= today).ToListAsync(cancellationToken);

    private async Task<Agreement> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Agreements.SingleOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw AgreementErrors.NotFound();

    /// <summary>The client must be active (SPEC-V2 5.1) and the template's assignee an active member (SPEC-V2 5.1).</summary>
    private async Task EnsureReferencesAsync(Guid? clientId, Guid? assigneeUserId, CancellationToken cancellationToken)
    {
        var errors = new List<(string Field, string Message)>();
        if (clientId is { } id)
        {
            var status = await db.Clients.Where(c => c.Id == id).Select(c => (ClientStatus?)c.Status).SingleOrDefaultAsync(cancellationToken);
            if (status is null)
            {
                errors.Add(("clientId", "Choose a client from the list."));
            }
            else if (status != ClientStatus.Active)
            {
                errors.Add(("clientId", "This client is archived. Restore it first, or choose another."));
            }
        }

        if (assigneeUserId is { } assignee && !await db.Memberships.AnyAsync(m => m.UserId == assignee && m.RemovedAt == null, cancellationToken))
        {
            errors.Add(("assigneeUserId", "Choose an active member of this organization."));
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(FieldErrors.From(errors));
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw AgreementErrors.ConcurrencyConflict();
        }
    }
}

/// <summary>The scheduler's daily run for one organization (SPEC-V2 4.9): generates up to its horizon, as the system.</summary>
public sealed class DailyVisitGeneration(
    IAppDbContext db,
    Tenancy.TenantContext tenant,
    IDatabaseLocks locks,
    VisitGenerator generator,
    TimeProvider time)
{
    /// <summary>Returns when the next run is due. A run after downtime catches up at once: today's horizon covers every missed day.</summary>
    public async Task<DateTimeOffset> RunAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        tenant.EnterAsSystem(organizationId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(organizationId, cancellationToken);
        var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, cancellationToken);
        var active = await db.Agreements.Where(a => a.Status == AgreementStatus.Active).ToListAsync(cancellationToken);
        await generator.GenerateAsync(organization, active, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return VisitGenerator.NextDailyRun(organization, time.GetUtcNow());
    }
}
