using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain.Clients;
using TenantHub.Domain.Jobs;

namespace TenantHub.Application.Jobs;

/// <summary>The editable fields as they arrive; <see cref="JobDetails.Validate"/> normalizes and checks them.</summary>
public sealed record JobInput(
    string? Title,
    string? Description,
    Guid? ClientId,
    Guid? AssigneeUserId,
    JobPriority? Priority,
    DateOnly? ScheduledOn,
    DateOnly? DueOn,
    decimal? QuotedAmount)
{
    public JobDetails ToDetails() =>
        JobDetails.Validate(Title, Description, ClientId, AssigneeUserId, Priority, ScheduledOn, DueOn, QuotedAmount);
}

/// <summary>
/// Job commands (US-30 to US-32). Each loads the job through the tenant filter (another organization's id is simply
/// "not found"), applies one domain method, which decides who may do what, and saves once; the audit entry is
/// written in the same save (D012). The number comes from the organization's counter under the organization-row
/// lock, so parallel creates leave no gaps and no duplicates (SPEC 5.4).
/// </summary>
public sealed class JobCommands(
    IAppDbContext db,
    ITenantContext tenant,
    IDatabaseLocks locks,
    JobViews views,
    TimeProvider time)
{
    public async Task<JobView> CreateAsync(JobInput input, CancellationToken cancellationToken)
    {
        var details = input.ToDetails();
        await EnsureReferencesAsync(details, current: null, cancellationToken);
        var names = await views.NamesAsync([details.ClientId], [details.AssigneeUserId], cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockOrganizationAsync(tenant.OrganizationId!.Value, cancellationToken);
        var organization = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, cancellationToken);

        // Open-job plan limit (SPEC 5.5): checked here in M08, under the same organization lock.
        var job = Job.Create(organization.Id, organization.TakeJobNumber(), details, views.Actor(), names, time.GetUtcNow());
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await views.ForAsync(job, cancellationToken);
    }

    public async Task<JobView> UpdateAsync(Guid id, uint version, JobInput input, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        var details = input.ToDetails();
        await EnsureReferencesAsync(details, job.Details, cancellationToken);
        var names = await views.NamesAsync(
            [job.ClientId, details.ClientId],
            [job.AssigneeUserId, details.AssigneeUserId],
            cancellationToken);

        // The version the user saw: if the row changed since, the update matches nothing and EF reports a conflict.
        db.Entry(job).Property(j => j.Version).OriginalValue = version;
        if (job.Update(details, views.Actor(), names, time.GetUtcNow()))
        {
            await SaveAsync(cancellationToken);
        }
        else if (job.Version != version)
        {
            throw JobErrors.ConcurrencyConflict();
        }

        return await views.ForAsync(job, cancellationToken);
    }

    public async Task<JobView> TransitionAsync(Guid id, JobStatus to, string? reason, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        job.TransitionTo(to, views.Actor(), reason, time.GetUtcNow());
        await SaveAsync(cancellationToken);
        return await views.ForAsync(job, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await LoadAsync(id, cancellationToken);
        job.MarkDeleted(views.Actor());
        db.Jobs.Remove(job);
        await SaveAsync(cancellationToken);
    }

    /// <summary>
    /// A new or changed client must be active, and a new or changed assignee an active member (SPEC 5.2, 5.4).
    /// Unchanged references are left alone: a job keeps a client that was archived since, and a former assignee.
    /// The composite foreign keys are the backstop that nothing points outside the organization.
    /// </summary>
    private async Task EnsureReferencesAsync(JobDetails details, JobDetails? current, CancellationToken cancellationToken)
    {
        var errors = new List<(string Field, string Message)>();

        if (current is null || details.ClientId != current.ClientId)
        {
            var status = await db.Clients
                .Where(c => c.Id == details.ClientId)
                .Select(c => (ClientStatus?)c.Status)
                .SingleOrDefaultAsync(cancellationToken);
            if (status is null)
            {
                errors.Add(("clientId", "Choose a client from the list."));
            }
            else if (status != ClientStatus.Active)
            {
                errors.Add(("clientId", "This client is archived. Restore it first, or choose another."));
            }
        }

        if (details.AssigneeUserId is { } assignee && (current is null || assignee != current.AssigneeUserId)
            && !await db.Memberships.AnyAsync(m => m.UserId == assignee && m.RemovedAt == null, cancellationToken))
        {
            errors.Add(("assigneeUserId", "Choose an active member of this organization."));
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(FieldErrors.From(errors));
        }
    }

    private async Task<Job> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Jobs.SingleOrDefaultAsync(j => j.Id == id, cancellationToken) ?? throw JobErrors.NotFound();

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw JobErrors.ConcurrencyConflict();
        }
    }
}
