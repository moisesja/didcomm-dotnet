namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// A <see cref="TimeProvider"/> over another one that counts timestamp readings and, once armed,
/// cancels a brake after a number of them (ADV-5). A live stream reads the clock a handful of times
/// per pass of its loop and then awaits something; one that keeps reading without awaiting is
/// spinning, and the brake turns that spin into a cancellation the test can observe instead of a
/// hung test host. Timers are the inner provider's.
/// </summary>
internal sealed class SpinBrakeTimeProvider(TimeProvider inner) : TimeProvider
{
    private long _reads;
    private long _limit = long.MaxValue;
    private int _tripped;

    /// <summary>The source the brake cancels.</summary>
    public CancellationTokenSource? Brake { get; set; }

    /// <summary>Runs once, on the reading thread, right before the brake is cancelled (to record the state of the spin).</summary>
    public Action? OnTrip { get; set; }

    /// <summary>Whether the readings since <see cref="Arm"/> passed the limit.</summary>
    public bool Tripped => Volatile.Read(ref _tripped) != 0;

    /// <summary>Readings since <see cref="Arm"/> (or since creation).</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>Count from zero again and trip the brake after <paramref name="reads"/> readings.</summary>
    public void Arm(long reads)
    {
        Interlocked.Exchange(ref _reads, 0);
        Interlocked.Exchange(ref _limit, reads);
    }

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp()
    {
        if (Interlocked.Increment(ref _reads) > Interlocked.Read(ref _limit) && Interlocked.Exchange(ref _tripped, 1) == 0)
        {
            OnTrip?.Invoke();
            Brake?.Cancel();
        }
        return inner.GetTimestamp();
    }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => inner.CreateTimer(callback, state, dueTime, period);
}

/// <summary>Waits of the ADV-5 tests that never leave a faulted task for nobody to observe.</summary>
internal static class Adv5Wait
{
    /// <summary>
    /// Wait (bounded, in real time) until <paramref name="move"/> finishes or a timer of
    /// <paramref name="clock"/> is scheduled (the stream waits). The timer wait that loses is left to
    /// end on its own; its time-out, if it comes, is observed here.
    /// </summary>
    public static async Task MoveOrTimerAsync(Pipeline.ManualTimeProvider clock, Task move)
    {
        var timer = clock.WaitForScheduledTimersAsync(1, PickupTest.Timeout);
        _ = timer.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await Task.WhenAny(move, timer);
    }
}
