using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantHub.Infrastructure.Data;

namespace TenantHub.Infrastructure.Background;

/// <summary>
/// One kind of recurring work. <see cref="RunAsync"/> does one run and answers when the next is due, counted from
/// now: a host that slept through several intervals runs the task once on waking, not once per missed interval.
/// </summary>
public interface IScheduledTask
{
    Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken);
}

/// <summary>The run's organization (null for application-wide work), when it was due and the clock at its start.</summary>
public sealed record ScheduledRun(Guid? OrganizationId, DateTimeOffset DueAt, DateTimeOffset Now);

public sealed record ScheduledTaskRoute(string Name, Type TaskType);

public static class SchedulerRegistration
{
    /// <summary>An application-wide task: the scheduler creates its row, first due at once.</summary>
    public static IServiceCollection AddScheduledTask<TTask>(this IServiceCollection services, string name)
        where TTask : class, IScheduledTask
    {
        services.AddScoped<TTask>();
        services.AddSingleton(new ScheduledTaskRoute(name, typeof(TTask)));
        return services;
    }
}

/// <summary>
/// Runs due rows of <c>scheduled_tasks</c> (SPEC-V2 4.9, D039), one at a time, on any number of instances. A row is
/// claimed with <c>FOR UPDATE SKIP LOCKED</c> and a lease written in the same short transaction; it is run outside
/// any transaction; then its next due time is written only if the lease is still this run's. A run that crashes
/// leaves the lease to expire, and another instance takes the task over. Rows of tasks this build does not know
/// (a task removed, or demo mode off) are left alone.
/// </summary>
public sealed partial class Scheduler(
    IServiceScopeFactory scopes,
    IEnumerable<ScheduledTaskRoute> routes,
    TimeProvider time,
    ILogger<Scheduler> logger)
{
    /// <summary>How long a run holds its task. A run that takes longer may be repeated by another instance.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, ScheduledTaskRoute> _routes = routes.ToDictionary(r => r.Name, StringComparer.Ordinal);

    /// <summary>Creates the rows of application-wide tasks that do not have one yet, due at once.</summary>
    public async Task EnsureTasksAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        foreach (var name in _routes.Keys)
        {
            // The unique index treats a null organization as a value, so a second instance's insert is ignored.
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO scheduled_tasks (id, name, organization_id, due_at)
                VALUES ({Guid.CreateVersion7(now)}, {name}, NULL, {now})
                ON CONFLICT (name, organization_id) DO NOTHING
                """,
                cancellationToken);
        }
    }

    /// <summary>Runs every task that is due, one after another. Returns how many runs it made.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var runs = 0;
        while (!cancellationToken.IsCancellationRequested && await RunNextAsync(cancellationToken))
        {
            runs++;
        }

        return runs;
    }

    /// <summary>When the earliest known task is next due (or its lease ends), for the host to sleep until then.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = _routes.Keys.ToArray();
        return await db.ScheduledTasks
            .Where(t => names.Contains(t.Name))
            .Select(t => (DateTimeOffset?)(t.LeaseUntil > t.DueAt ? t.LeaseUntil : t.DueAt))
            .MinAsync(cancellationToken);
    }

    private async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await ClaimAsync(db, cancellationToken) is not { } task)
        {
            return false;
        }

        var route = _routes[task.Name];
        var lease = task.LeaseUntil!.Value;
        var now = time.GetUtcNow();
        DateTimeOffset next;
        string? error = null;
        try
        {
            await using var runScope = scopes.CreateAsyncScope();
            var runner = (IScheduledTask)runScope.ServiceProvider.GetRequiredService(route.TaskType);
            next = await runner.RunAsync(new ScheduledRun(task.OrganizationId, task.DueAt, now), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false; // shutting down: the lease runs out and another start takes the task
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name;
            next = time.GetUtcNow() + RetryAfterFailure;
            LogFailed(logger, exception, task.Name, task.OrganizationId);
        }

        var finished = time.GetUtcNow();
        var written = await db.ScheduledTasks
            .Where(t => t.Id == task.Id && t.LeaseUntil == lease)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.DueAt, next)
                    .SetProperty(t => t.LeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(t => t.LastRunAt, error is null ? finished : task.LastRunAt)
                    .SetProperty(t => t.LastError, error),
                CancellationToken.None);
        if (written == 0)
        {
            LogLeaseLost(logger, task.Name, task.OrganizationId);
        }

        return true;
    }

    private async Task<ScheduledTask?> ClaimAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var names = _routes.Keys.ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Not composed further: EF Core would wrap the locking query in a subquery.
        var claimed = await db.ScheduledTasks
            .FromSql($"""
                SELECT * FROM scheduled_tasks
                WHERE name = ANY({names}) AND due_at <= {now} AND (lease_until IS NULL OR lease_until <= {now})
                ORDER BY due_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        var task = claimed.SingleOrDefault();
        if (task is null)
        {
            return null;
        }

        // PostgreSQL keeps microseconds; the lease is compared again when the run finishes, so it must round-trip.
        task.LeaseUntil = TruncateToMicroseconds(now + Lease);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled task {Task} (organization {OrganizationId}) failed; retrying in five minutes.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string task, Guid? organizationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled task {Task} (organization {OrganizationId}) ran past its lease; another instance may have run it too.")]
    private static partial void LogLeaseLost(ILogger logger, string task, Guid? organizationId);
}

/// <summary>
/// Clears delivered outbox messages after 7 days and dead ones after 30 (D039): long enough to see what happened,
/// short enough that addresses in payloads do not pile up.
/// </summary>
internal sealed class OutboxRetentionTask(AppDbContext db) : IScheduledTask
{
    public const string Name = "outbox.retention";

    public static readonly TimeSpan KeepProcessed = TimeSpan.FromDays(7);

    public static readonly TimeSpan KeepDead = TimeSpan.FromDays(30);

    public async Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken)
    {
        var processedBefore = run.Now - KeepProcessed;
        var deadBefore = run.Now - KeepDead;
        await db.OutboxMessages.Where(m => m.ProcessedAt < processedBefore).ExecuteDeleteAsync(cancellationToken);
        await db.OutboxMessages.Where(m => m.DeadAt < deadBefore).ExecuteDeleteAsync(cancellationToken);
        return run.Now + TimeSpan.FromHours(1);
    }
}
