using DidComm.Mediator.Tests.Pipeline;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="ManualTimeProvider"/> that counts the timers not
/// yet disposed and can run a hook when a timer is created or the clock is read (ADV-4: a
/// deterministic rendezvous inside the unpacker's worker, which reads the clock for the start rule
/// right before it reserves and starts the host code).
/// </summary>
internal sealed class CountingTimeProvider(ManualTimeProvider inner, bool readingsFromZero = false) : TimeProvider
{
    // With readingsFromZero the timestamps count from this clock's creation, as a host clock's may:
    // a reading of zero is then a reading the clock can give.
    private readonly long _origin = readingsFromZero ? inner.GetTimestamp() : 0;
    private int _live;
    private int _created;

    public ManualTimeProvider Inner => inner;

    /// <summary>Runs with the 1-based number of the timer about to be created, on the creating thread.</summary>
    public Action<int>? OnCreate { get; set; }

    /// <summary>Runs before each reading of the clock's timestamp, on the reading thread.</summary>
    public Action? OnRead { get; set; }

    /// <summary>How many timers were created and not yet disposed.</summary>
    public int LiveTimers => Volatile.Read(ref _live);

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp()
    {
        OnRead?.Invoke();
        return inner.GetTimestamp() - _origin;
    }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var number = Interlocked.Increment(ref _created);
        OnCreate?.Invoke(number);
        Interlocked.Increment(ref _live);
        return new Counted(this, inner.CreateTimer(callback, state, dueTime, period));
    }

    private sealed class Counted(CountingTimeProvider owner, ITimer timer) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            timer.Dispose();
            Interlocked.Decrement(ref owner._live);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A host clock whose every timestamp reading is one tick lower than the one before (a
/// <see cref="TimeProvider"/> whose readings run backwards, or wobble below the last reading on
/// every read). Timers are the inner manual clock's: they fire only when it is advanced.
/// </summary>
internal sealed class BackwardTimeProvider : TimeProvider
{
    private long _reads;

    public ManualTimeProvider Inner { get; } = new();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override long GetTimestamp() => Inner.GetTimestamp() - Interlocked.Increment(ref _reads);

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => Inner.CreateTimer(callback, state, dueTime, period);
}
