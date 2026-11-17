using TenantHub.Infrastructure.Background;

namespace TenantHub.Infrastructure.Seed;

/// <summary>
/// The demo reset on the scheduler (D039), registered in demo mode only. The reset's own schedule still lives in the
/// seeded organizations' creation time (D032); the task asks it whether a reset is due and sleeps until the answer.
/// The scheduler's lease now keeps two instances from resetting at once, which the hosted service could not.
/// </summary>
internal sealed class DemoResetTask(DemoReset reset) : IScheduledTask
{
    public const string Name = "demo.reset";

    public Task<DateTimeOffset> RunAsync(ScheduledRun run, CancellationToken cancellationToken) =>
        reset.ResetIfDueAsync(cancellationToken);
}
