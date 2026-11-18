namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>A clock the test moves by hand. Shared by two factories, it gives two "instances" the same time.</summary>
public sealed class MovingClock(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;

    public DateTimeOffset Now
    {
        get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => Now;
}
