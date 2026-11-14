namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// A deadline of the pickup client's own, on a token the send path hands to transports (host code):
/// cancelled by a <see cref="TimeProvider"/> timer whose callback swallows whatever a callback
/// registered on the token throws, never by <c>CancelAfter</c>, whose timer thread would carry such
/// an exception out (the <c>PushDispatcher.CancelQuietly</c> pattern). Used where a step must finish
/// within <see cref="MediatorClientOptions.RequestTimeout"/> whatever the caller's token does: the
/// acknowledgement of a fetch, and the end of a live stream. Linked to the caller's token, it is also
/// what wakes a live stream's wait for a push when its next cycle comes due.
/// </summary>
internal sealed class OwnDeadline : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;

    private OwnDeadline(CancellationTokenSource source, ITimer timer)
    {
        _source = source;
        _timer = timer;
    }

    /// <summary>The token that is cancelled when the deadline passes.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>Whether the deadline has passed (or the linked token was cancelled).</summary>
    internal bool Expired => _source.IsCancellationRequested;

    /// <summary>Start a deadline <paramref name="after"/> from now on <paramref name="time"/>.</summary>
    /// <param name="time">The clock of the timer.</param>
    /// <param name="after">How long until the token is cancelled.</param>
    /// <param name="linkedTo">A token that cancels <see cref="Token"/> too, or none (the default): independent of the caller.</param>
    internal static OwnDeadline Start(TimeProvider time, TimeSpan after, CancellationToken linkedTo = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        var source = linkedTo.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(linkedTo) : new CancellationTokenSource();
        try
        {
            var timer = time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), source, after, Timeout.InfiniteTimeSpan);
            return new OwnDeadline(source, timer);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Stop the timer and release the token source.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        _source.Dispose();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // A callback host code registered on the token threw, or the source is already
            // disposed: neither may escape onto a timer thread.
        }
    }
}
