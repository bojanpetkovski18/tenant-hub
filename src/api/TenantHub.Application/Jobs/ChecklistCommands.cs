using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Jobs;

namespace TenantHub.Application.Jobs;

/// <summary>An item as the job page shows it: who ticked it and when.</summary>
public sealed record ChecklistItemView(Guid Id, int Position, string Text, bool Required, bool Done, DateTimeOffset? DoneAt, string? DoneByName);

/// <summary>
/// A job's checklist with what the caller may do: <see cref="CanEdit"/> covers ticking, adding, editing and reordering,
/// and <see cref="ReadOnlyReason"/> says why not to someone who could otherwise (a completed job's checklist).
/// </summary>
public sealed record ChecklistView(IReadOnlyList<ChecklistItemView> Items, bool CanEdit, string? ReadOnlyReason, int MaxItems);

public sealed record ChecklistItemInput(string? Text, bool? Required);

public sealed record ChecklistChangeInput(string? Text, bool? Required, bool? Done, int? Position);

/// <summary>
/// A job's checklist (US-90, SPEC-V2 5.6). Each change runs under the job's row lock, which status changes take too, so
/// a job is never completed while someone unticks a required item; then one domain method decides and audits it.
/// </summary>
public sealed class ChecklistCommands(IAppDbContext db, IDatabaseLocks locks, JobViews views, IIdentityService identity, TimeProvider time)
{
    public async Task<ChecklistView> ListAsync(Guid jobId, CancellationToken cancellationToken) =>
        await ViewAsync(await LoadAsync(jobId, tracked: false, cancellationToken), cancellationToken);

    public Task<ChecklistView> AddAsync(Guid jobId, ChecklistItemInput input, CancellationToken cancellationToken) =>
        ChangeAsync(jobId, job => job.AddChecklistItem(ChecklistEntry.Validate(input.Text, input.Required), views.Actor(), time.GetUtcNow()), cancellationToken);

    public Task<ChecklistView> ChangeItemAsync(Guid jobId, Guid itemId, ChecklistChangeInput input, CancellationToken cancellationToken) =>
        ChangeAsync(
            jobId,
            job => job.ChangeChecklistItem(itemId, new ChecklistChange(input.Text, input.Required, input.Done, input.Position), views.Actor(), time.GetUtcNow()),
            cancellationToken);

    public Task<ChecklistView> RemoveAsync(Guid jobId, Guid itemId, CancellationToken cancellationToken) =>
        ChangeAsync(jobId, job => job.RemoveChecklistItem(itemId, views.Actor(), time.GetUtcNow()), cancellationToken);

    /// <summary>The items with the names of who ticked them; ordered, as every reader shows them.</summary>
    public static async Task<IReadOnlyList<ChecklistItemView>> ItemsAsync(
        IEnumerable<ChecklistItem> items, IIdentityService identity, CancellationToken cancellationToken)
    {
        var ordered = items.OrderBy(i => i.Position).ToList();
        var names = await identity.FindProfilesAsync([.. ordered.Select(i => i.DoneByUserId).OfType<Guid>().Distinct()], cancellationToken);
        return
        [
            .. ordered.Select(i => new ChecklistItemView(
                i.Id,
                i.Position,
                i.Text,
                i.Required,
                i.IsDone,
                i.DoneAt,
                i.DoneByUserId is { } user ? names.TryGetValue(user, out var profile) ? profile.DisplayName : "A former member" : null)),
        ];
    }

    private async Task<ChecklistView> ChangeAsync(Guid jobId, Action<Job> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await locks.LockJobAsync(jobId, cancellationToken);
        var job = await LoadAsync(jobId, tracked: true, cancellationToken);
        change(job);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ViewAsync(job, cancellationToken);
    }

    private async Task<ChecklistView> ViewAsync(Job job, CancellationToken cancellationToken)
    {
        var actor = views.Actor();
        var canEdit = job.CanEdit(actor) && job.Status.IsOpen();
        var readOnly = job.CanEdit(actor) && !job.Status.IsOpen()
            ? $"{job.Label} is {JobLabels.Of(job.Status)}. Reopen it to change its checklist."
            : null;
        return new ChecklistView(await ItemsAsync(job.Checklist, identity, cancellationToken), canEdit, readOnly, Checklists.MaxItems);
    }

    private async Task<Job> LoadAsync(Guid jobId, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.Jobs.Include(j => j.Checklist).Where(j => j.Id == jobId);
        return await (tracked ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken)
            ?? throw JobErrors.NotFound();
    }
}
