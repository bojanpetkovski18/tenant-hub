using TenantHub.Infrastructure.Seed;

namespace TenantHub.Api.Features.Demo;

/// <summary>Runs <see cref="DemoReset"/> on its schedule, in demo mode only.</summary>
internal sealed partial class DemoResetService(IServiceScopeFactory scopes, TimeProvider time, ILogger<DemoResetService> logger)
    : BackgroundService
{
    // Re-read the schedule at least this often, so a reset made by another start moves the next one.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var next = await scope.ServiceProvider.GetRequiredService<DemoReset>().ResetIfDueAsync(stoppingToken);
                wait = next - time.GetUtcNow();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
                wait = RetryAfterFailure;
            }

            try
            {
                await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Demo reset failed; retrying in five minutes.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
