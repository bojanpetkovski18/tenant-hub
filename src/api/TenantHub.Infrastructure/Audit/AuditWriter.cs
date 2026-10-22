using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TenantHub.Application.Abstractions;
using TenantHub.Domain;
using TenantHub.Domain.Audit;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Audit;

/// <summary>
/// Turns the auditable events of tracked aggregates into audit entries just before they are saved, so the change
/// and its entry are written in the same transaction: both or neither (D012). Events are cleared once turned into
/// entries, so a second SaveChanges in the same request cannot record them twice.
/// </summary>
internal sealed class AuditWriter(AppDbContext db, ITenantContext tenant, TimeProvider time)
{
    private const string SystemActor = "System";
    private string? _actorName;

    public async Task AddEntriesAsync(CancellationToken cancellationToken)
    {
        var aggregates = Pending();
        if (aggregates.Count == 0)
        {
            return;
        }

        _actorName ??= await ActorNameAsync(cancellationToken);
        Add(aggregates);
    }

    public void AddEntries()
    {
        var aggregates = Pending();
        if (aggregates.Count == 0)
        {
            return;
        }

        _actorName ??= ActorNameQuery().FirstOrDefault() ?? SystemActor;
        Add(aggregates);
    }

    private List<AggregateRoot> Pending() =>
        [.. db.ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).Where(a => a.DomainEvents.Count > 0)];

    private void Add(List<AggregateRoot> aggregates)
    {
        var now = time.GetUtcNow();
        var traceId = Activity.Current?.TraceId.ToString();
        foreach (var aggregate in aggregates)
        {
            foreach (var change in aggregate.DomainEvents.OfType<IAuditableEvent>())
            {
                db.AuditEntries.Add(AuditEntry.From(change, tenant.UserId, _actorName!, now, traceId));
            }

            aggregate.ClearDomainEvents();
        }
    }

    private async Task<string> ActorNameAsync(CancellationToken cancellationToken) =>
        await ActorNameQuery().FirstOrDefaultAsync(cancellationToken) ?? SystemActor;

    private IQueryable<string> ActorNameQuery() =>
        db.Users.AsNoTracking().Where(u => u.Id == tenant.UserId).Select(u => u.DisplayName);
}
