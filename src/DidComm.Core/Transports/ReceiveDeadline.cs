namespace DidComm.Transports;

/// <summary>
/// The time a WebSocket peer has to finish one message: the <c>MessageReceiveTimeout</c> setting of
/// <c>DidCommReceiveOptions</c> (the ASP.NET Core endpoints) and of <c>WebSocketTransportOptions</c>
/// (the client transport's return-route session sockets). One timer per message, on the clock the
/// caller passes, that completes <see cref="Expired"/> when it fires.
/// </summary>
/// <remarks>
/// <para>
/// It never cancels a token. In .NET's managed WebSocket, cancelling the token of an outstanding
/// <c>ReceiveAsync</c> aborts the socket, so no close frame could follow; and a cancelled token would
/// run whatever callbacks host code registered on it on the timer's thread. The receive loops instead
/// wait for the receive or <see cref="Expired"/> (<see cref="WaitAsync"/>), and on expiry send their
/// own close frame while the receive is still outstanding.
/// </para>
/// <para>
/// A clock that cannot create the timer throws out of <see cref="Start"/>: the connection then ends,
/// rather than running without a limit (L-054).
/// </para>
/// </remarks>
internal sealed class ReceiveDeadline : IDisposable
{
    /// <summary>The default of both <c>MessageReceiveTimeout</c> settings: 60 seconds.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The largest finite value a timer accepts here.</summary>
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly TaskCompletionSource _expired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer _timer;

    private ReceiveDeadline(TimeProvider time, TimeSpan timeout)
        => _timer = time.CreateTimer(static state => ((ReceiveDeadline)state!)._expired.TrySetResult(), this, timeout, Timeout.InfiniteTimeSpan);

    /// <summary>Completes when the time is up. Its continuations never run on the timer's thread.</summary>
    internal Task Expired => _expired.Task;

    /// <summary>Whether the time is up.</summary>
    internal bool HasExpired => _expired.Task.IsCompleted;

    /// <summary>
    /// Check a <c>MessageReceiveTimeout</c> value: greater than zero and at most
    /// <see cref="int.MaxValue"/> milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/> (no limit) —
    /// the rule of <c>HttpClient.Timeout</c>.
    /// </summary>
    /// <param name="value">The value being set.</param>
    /// <param name="paramName">The setting's name, for the exception.</param>
    /// <returns><paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Any other value.</exception>
    internal static TimeSpan Validate(TimeSpan value, string paramName)
    {
        if (value == Timeout.InfiniteTimeSpan || (value > TimeSpan.Zero && value <= MaxTimeout))
            return value;
        throw new ArgumentOutOfRangeException(
            paramName,
            value,
            "The value must be greater than zero and at most Int32.MaxValue milliseconds, or Timeout.InfiniteTimeSpan to turn the limit off.");
    }

    /// <summary>Start the time limit of one message; <c>null</c> when <paramref name="timeout"/> is infinite (no limit).</summary>
    /// <param name="time">The clock of the timer.</param>
    /// <param name="timeout">A value <see cref="Validate"/> accepts.</param>
    /// <returns>The running deadline, or <c>null</c>.</returns>
    internal static ReceiveDeadline? Start(TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(time);
        return timeout == Timeout.InfiniteTimeSpan ? null : new ReceiveDeadline(time, timeout);
    }

    /// <summary>
    /// Wait until <paramref name="receive"/> completes or <paramref name="deadline"/> expires, whichever
    /// is first. Returns <c>true</c> when the receive completed (it also wins when both are done), so a
    /// message whose last part arrived in time is never cut off; <c>false</c> when the time ran out with
    /// the receive still outstanding. Never throws: the caller awaits the completed receive itself.
    /// </summary>
    /// <param name="receive">The outstanding receive.</param>
    /// <param name="deadline">The running deadline, or <c>null</c> for none (returns once the receive completes).</param>
    internal static async ValueTask<bool> WaitAsync(Task receive, ReceiveDeadline? deadline)
    {
        ArgumentNullException.ThrowIfNull(receive);
        if (receive.IsCompleted)
            return true;
        if (deadline is null)
        {
            await receive.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return true;
        }
        await Task.WhenAny(receive, deadline.Expired).ConfigureAwait(false);
        return receive.IsCompleted;
    }

    /// <summary>Stop the timer (idempotent).</summary>
    public void Dispose() => _timer.Dispose();
}
