namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// A one-shot timer of a live stream that only records that it fired (#111: I-15, I-20). The live
/// stream decides that something is due — the next cycle, the end of a back-off, the flush age, the
/// give-up point — by reading <see cref="Fired"/>, never by comparing clock readings, so a host
/// clock that steps back or forward, or whose readings keep going lower, changes no decision: an
/// alarm fires when its <see cref="TimeProvider"/> timer does.
/// </summary>
/// <remarks>
/// <para>
/// The timer's callback sets <see cref="Fired"/> and then calls the stream's poke (which wakes the
/// stream's wait), and nothing else: no stream code runs on a timer thread, and nothing escapes
/// onto it. Setting the flag is a full fence, so the poke reads the stream's wake source after the
/// flag is visible (the alarm's half of the stream's wake handshake).
/// </para>
/// <para>
/// Each arming is a new alarm, and the stream disposes the one it replaces. A replaced alarm whose
/// callback was already running when it was disposed sets only its own flag, which nothing reads any
/// more, and pokes once more: one spurious wake, which finds nothing new to do and waits again.
/// </para>
/// <para>
/// Holds one timer from <see cref="Start"/> until <see cref="Dispose"/>; <see cref="AlreadyFired"/>
/// holds none.
/// </para>
/// </remarks>
internal sealed class Alarm : IDisposable
{
    private readonly Action? _poke;
    private ITimer? _timer;
    private int _fired;

    private Alarm(Action? poke, bool fired)
    {
        _poke = poke;
        _fired = fired ? 1 : 0;
    }

    /// <summary>An alarm that has fired and holds no timer (the first cycle of a stream is due at once).</summary>
    internal static Alarm AlreadyFired { get; } = new(poke: null, fired: true);

    /// <summary>Whether the alarm's timer has fired.</summary>
    internal bool Fired => Volatile.Read(ref _fired) != 0;

    /// <summary>Arm a new alarm that fires <paramref name="due"/> from now on <paramref name="time"/>.</summary>
    /// <param name="time">The clock whose timer fires the alarm.</param>
    /// <param name="due">How long until it fires (greater than zero).</param>
    /// <param name="poke">Called on the timer's thread right after the flag is set; it must not throw or wait.</param>
    internal static Alarm Start(TimeProvider time, TimeSpan due, Action poke)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(poke);
        var alarm = new Alarm(poke, fired: false);
        alarm._timer = time.CreateTimer(static state => ((Alarm)state!).Ring(), alarm, due, Timeout.InfiniteTimeSpan);
        return alarm;
    }

    /// <summary>Stop the timer (a no-op for <see cref="AlreadyFired"/>). A callback already running still sets this alarm's own flag.</summary>
    public void Dispose() => _timer?.Dispose();

    private void Ring()
    {
        try
        {
            Interlocked.Exchange(ref _fired, 1);
            _poke?.Invoke();
        }
        catch
        {
            // Nothing escapes onto a timer thread (the poke is written not to throw).
        }
    }
}
