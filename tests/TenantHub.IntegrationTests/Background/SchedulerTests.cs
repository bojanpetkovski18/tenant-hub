using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantHub.Infrastructure.Background;
using TenantHub.IntegrationTests.Infrastructure;

namespace TenantHub.IntegrationTests.Background;

/// <summary>
/// SPEC-V2 4.9 (D039): a due task runs once however many instances look, a run that dies is taken over when its
/// lease ends, and a host that slept through several intervals runs the task once, not once per missed interval.
/// </summary>
public sealed class SchedulerTests(PostgresFixture postgres)
{
    private const string TaskName = "test.counting";
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_schedulers_run_a_due_task_once()
    {
        const int rounds = 5;
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var counter = new Counter { RunTime = TimeSpan.FromMilliseconds(300) };
        await using var first = Factory(connectionString, clock, counter);
        await using var second = Factory(connectionString, clock, counter);
        await Task.WhenAll(EnsureAsync(first), EnsureAsync(second));

        var runs = new List<int>();
        for (var round = 0; round < rounds; round++)
        {
            var made = await Task.WhenAll(Task.Run(() => RunDueAsync(first), Ct), Task.Run(() => RunDueAsync(second), Ct));
            runs.Add(made.Sum());
            clock.Advance(Counter.Interval);
        }

        Assert.Equal(Enumerable.Repeat(1, rounds), runs);
        Assert.Equal(rounds, counter.Runs);
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(1, await check.ScheduledTasks.CountAsync(t => t.Name == TaskName, Ct)); // two EnsureTasks, one row
    }

    [Fact]
    public async Task A_run_that_died_is_taken_over_when_its_lease_ends()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var counter = new Counter();
        await using var factory = Factory(connectionString, clock, counter);
        await EnsureAsync(factory);

        // Another instance claimed the task and died: the lease stays, nothing completes it.
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        await check.ScheduledTasks.Where(t => t.Name == TaskName)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LeaseUntil, Start + Scheduler.Lease), Ct);

        Assert.Equal(0, await RunDueAsync(factory));
        clock.Advance(Scheduler.Lease);
        Assert.Equal(1, await RunDueAsync(factory));

        var task = await check.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Name == TaskName, Ct);
        Assert.Equal((clock.Now + Counter.Interval, (DateTimeOffset?)null, clock.Now, (string?)null), (task.DueAt, task.LeaseUntil, task.LastRunAt, task.LastError));
        Assert.Equal(1, counter.Runs);
    }

    [Fact]
    public async Task A_host_that_slept_through_many_intervals_runs_the_task_once_on_waking()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var counter = new Counter();
        await using var factory = Factory(connectionString, clock, counter);
        await EnsureAsync(factory);
        Assert.Equal(1, await RunDueAsync(factory));

        clock.Advance(Counter.Interval * 10);

        Assert.Equal(1, await RunDueAsync(factory));
        Assert.Equal(0, await RunDueAsync(factory));
        Assert.Equal(2, counter.Runs);
        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(clock.Now + Counter.Interval, (await check.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Name == TaskName, Ct)).DueAt);
    }

    [Fact]
    public async Task A_failed_run_records_the_error_class_and_is_retried_later()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        var counter = new Counter { Fail = true };
        await using var factory = Factory(connectionString, clock, counter);
        await EnsureAsync(factory);

        Assert.Equal(1, await RunDueAsync(factory));

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        var task = await check.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Name == TaskName, Ct);
        Assert.Equal((Start + Scheduler.RetryAfterFailure, "InvalidOperationException", (DateTimeOffset?)null), (task.DueAt, task.LastError, task.LeaseUntil));
    }

    [Fact]
    public async Task Retention_deletes_delivered_messages_after_7_days_and_dead_ones_after_30()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var clock = new MovingClock(Start);
        await using var factory = new ApiFactory(connectionString, services: s => s.AddSingleton<TimeProvider>(clock));
        await using (var db = PostgresFixture.CreateDbContext(connectionString))
        {
            db.OutboxMessages.AddRange(
                Message("processed-8-days", processedAt: Start.AddDays(-8)),
                Message("processed-6-days", processedAt: Start.AddDays(-6)),
                Message("dead-31-days", deadAt: Start.AddDays(-31)),
                Message("dead-29-days", deadAt: Start.AddDays(-29)),
                Message("pending-old"));
            await db.SaveChangesAsync(Ct);
        }

        await EnsureAsync(factory);
        await RunDueAsync(factory);

        await using var check = PostgresFixture.CreateDbContext(connectionString);
        Assert.Equal(["dead-29-days", "pending-old", "processed-6-days"], await check.OutboxMessages.Select(m => m.Type).OrderBy(t => t).ToListAsync(Ct));
        var retention = await check.ScheduledTasks.AsNoTracking().SingleAsync(t => t.Name == "outbox.retention", Ct);
        Assert.Equal(Start.AddHours(1), retention.DueAt);
    }

    private static OutboxMessage Message(string type, DateTimeOffset? processedAt = null, DateTimeOffset? deadAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Payload = "{}",
        CreatedAt = Start.AddDays(-40),
        AvailableAt = Start.AddDays(-40),
        ProcessedAt = processedAt,
        DeadAt = deadAt,
    };

    private static ApiFactory Factory(string connectionString, MovingClock clock, Counter counter) =>
        new(connectionString, services: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(counter);
            // Only the test's task, so run counts are its own (retention is tested on its own below).
            services.RemoveAll<ScheduledTaskRoute>();
            services.AddScheduledTask<CountingTask>(TaskName);
        });

    private static Task EnsureAsync(ApiFactory factory) => factory.Services.GetRequiredService<Scheduler>().EnsureTasksAsync(Ct);

    private static Task<int> RunDueAsync(ApiFactory factory) => factory.Services.GetRequiredService<Scheduler>().RunDueAsync(Ct);

    public sealed class Counter
    {
        public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        private int _runs;

        public TimeSpan RunTime { get; init; }

        public bool Fail { get; init; }

        public int Runs => _runs;

        public async Task<DateTimeOffset> RunAsync(ScheduledRun run)
        {
            Interlocked.Increment(ref _runs);
            await Task.Delay(RunTime);
            return Fail ? throw new InvalidOperationException("Run failed.") : run.Now + Interval;
        }
    }

    private sealed class CountingTask(Counter counter) : IScheduledTask
    {
        public Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken) => counter.RunAsync(run);
    }
}
