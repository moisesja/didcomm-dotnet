namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// A deadline whose token may be handed to host code (a transport, a DID resolver): a timer on the
/// injected <see cref="TimeProvider"/> cancels it inside a <c>try</c>, and so do the caller's token
/// and the channel's shutdown token, through registrations of their own. A callback that host code
/// registered on <see cref="Token"/> and that throws can therefore never escape onto a timer thread
/// or into the caller's <c>Cancel()</c> — the <c>PushDispatcher.CancelQuietly</c> pattern, never
/// <c>CancelAfter</c> or a linked source (#92 follow-up 1). <see cref="TimedOut"/> says whether the
/// timer, and not the caller or the shutdown, ended it.
/// </summary>
internal sealed class GuardedDeadline : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly ITimer _timer;
    private readonly CancellationTokenRegistration _caller;
    private readonly CancellationTokenRegistration _shutdown;
    private int _timedOut;

    /// <summary>Start the deadline.</summary>
    /// <param name="time">The clock of the timer.</param>
    /// <param name="timeout">How long until it fires (validated by the options: greater than zero, at most 10 minutes).</param>
    /// <param name="caller">The caller's cancellation; cancelling it ends the deadline too.</param>
    /// <param name="shutdown">The owner's shutdown; cancelling it ends the deadline too.</param>
    internal GuardedDeadline(TimeProvider time, TimeSpan timeout, CancellationToken caller, CancellationToken shutdown = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        _timer = time.CreateTimer(static state => ((GuardedDeadline)state!).Fire(), this, timeout, Timeout.InfiniteTimeSpan);
        _caller = caller.UnsafeRegister(static state => CancelQuietly(((GuardedDeadline)state!)._source), this);
        _shutdown = shutdown.UnsafeRegister(static state => CancelQuietly(((GuardedDeadline)state!)._source), this);
    }

    /// <summary>The token to pass on: cancelled when the timer fires, the caller cancels or the owner shuts down.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>Whether the timer fired (as opposed to the caller or the shutdown ending the deadline).</summary>
    internal bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    /// <summary>Cancel <paramref name="source"/>; nothing a registered callback throws, and no disposal race, escapes.</summary>
    /// <param name="source">The source to cancel.</param>
    internal static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // A callback host code registered on the token threw, or the source is already
            // disposed: neither may escape onto a timer thread or into a caller's Cancel().
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _timer.Dispose();
        _caller.Dispose();
        _shutdown.Dispose();
        _source.Dispose();
    }

    private void Fire()
    {
        Volatile.Write(ref _timedOut, 1);
        CancelQuietly(_source);
    }
}
