using TenantHub.Infrastructure.Background;

namespace TenantHub.Api.Common;

/// <summary>
/// Runs the outbox dispatcher and the scheduler in this process (D039). Every instance may run both: claims and
/// leases in the database keep them from doing the same work twice. <c>BackgroundWork:Enabled=false</c> turns both
/// off, for the integration tests, which drive them one step at a time.
/// </summary>
public static class BackgroundWork
{
    public const string EnabledSetting = "BackgroundWork:Enabled";

    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue(EnabledSetting, true))
        {
            services.AddHostedService<OutboxDispatcherService>();
            services.AddHostedService<SchedulerService>();
        }

        return services;
    }
}

/// <summary>Delivers outbox messages: at once when this instance commits one, otherwise at least every 30 seconds.</summary>
internal sealed partial class OutboxDispatcherService(
    OutboxDispatcher dispatcher,
    OutboxSignal signal,
    OutboxOptions options,
    ILogger<OutboxDispatcherService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A full batch suggests more are waiting; otherwise sleep until a commit here or the next poll.
                if (await dispatcher.DispatchBatchAsync(stoppingToken) < options.BatchSize)
                {
                    await signal.WaitAsync(Poll, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The database is unreachable, most likely; readiness reports that. Try again at the next poll.
                LogFailed(logger, exception);
                if (!await SleepAsync(Poll, stoppingToken))
                {
                    return;
                }
            }
        }
    }

    internal static async Task<bool> SleepAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(wait, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox dispatcher failed; retrying in 30 seconds.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Runs due scheduled tasks, then sleeps until the next is due, at most a minute.</summary>
internal sealed partial class SchedulerService(Scheduler scheduler, TimeProvider time, ILogger<SchedulerService> logger)
    : BackgroundService
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ensured = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = MaxSleep;
            try
            {
                if (!ensured)
                {
                    await scheduler.EnsureTasksAsync(stoppingToken);
                    ensured = true;
                }

                await scheduler.RunDueAsync(stoppingToken);
                if (await scheduler.NextDueAsync(stoppingToken) is { } next)
                {
                    var untilNext = next - time.GetUtcNow();
                    wait = untilNext < MinSleep ? MinSleep : untilNext > MaxSleep ? MaxSleep : untilNext;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }

            if (!await OutboxDispatcherService.SleepAsync(wait, stoppingToken))
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduler failed; retrying within a minute.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
