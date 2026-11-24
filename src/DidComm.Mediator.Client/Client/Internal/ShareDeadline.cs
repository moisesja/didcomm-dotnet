namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// One message's share of its delivery's budget (#111, I-7, owner decision A1): a token the
/// message's host call gets, cancelled when the budget ends (it is linked to the budget's token), or
/// when the message is cut (<see cref="Cut"/>). When the share's time is up, its timer does not
/// cancel the token itself: it asks the delivery whether to cut the message or let it keep the rest
/// of the budget, and the delivery decides before any host code sees a cancellation.
/// </summary>
/// <remarks>
/// Every cancellation goes through <see cref="GuardedDeadline.CancelQuietly"/>, so nothing a
/// callback host code registered on the token throws escapes onto a timer thread. The timer's
/// decision runs at most once. After <see cref="Dispose"/> the share references nothing of its
/// delivery; the token source stays reachable from host code that holds the token, and references
/// only what was registered on it.
/// </remarks>
internal sealed class ShareDeadline : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly ITimer _timer;
    private readonly CancellationTokenRegistration _budget;
    private Action<ShareDeadline>? _due;

    /// <summary>Start the share.</summary>
    /// <param name="time">The clock of the timer.</param>
    /// <param name="share">How long until the share's time is up.</param>
    /// <param name="budget">The delivery's budget token: cancelling it cancels the share's token.</param>
    /// <param name="due">Runs once, on the timer's thread, when the share's time is up and the token is not yet cancelled; it cuts the message (<see cref="Cut"/>) or lets it run on.</param>
    internal ShareDeadline(TimeProvider time, TimeSpan share, CancellationToken budget, Action<ShareDeadline> due)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(due);
        _due = due;
        _timer = time.CreateTimer(static state => ((ShareDeadline)state!).Due(), this, share, Timeout.InfiniteTimeSpan);
        _budget = budget.UnsafeRegister(static state => ((ShareDeadline)state!).Cut(), this);
    }

    /// <summary>The token the host call gets.</summary>
    internal CancellationToken Token => _source.Token;

    /// <summary>The token's source, for a cut that comes from elsewhere (<see cref="UnpackReservations.Reservation.TryExtend"/>).</summary>
    internal CancellationTokenSource Source => _source;

    /// <summary>Cut the message: cancel the token. Does nothing once cancelled or disposed.</summary>
    internal void Cut() => GuardedDeadline.CancelQuietly(_source);

    /// <inheritdoc/>
    public void Dispose()
    {
        _due = null;
        _timer.Dispose();
        _budget.Dispose();
        _source.Dispose();
    }

    private void Due()
    {
        var due = Interlocked.Exchange(ref _due, null);
        if (due is null || _source.IsCancellationRequested)
            return;
        try
        {
            due(this);
        }
        catch
        {
            Cut(); // the decision failed (a host clock that throws): cut, as a share always did
        }
    }
}
