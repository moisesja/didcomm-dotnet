namespace DidComm.Samples.Shared;

/// <summary>
/// Reads an async stream one item at a time, failing loudly instead of hanging, and never
/// disposes the stream while a read is still running.
/// </summary>
/// <remarks>
/// <para>
/// The samples read a live message stream item by item, because they act between items (Alice
/// sends again once Bob has his first message). A plain <c>await foreach</c> is the usual way to
/// consume such a stream and needs none of this; the samples add a hang guard so a broken run
/// fails after a while instead of waiting forever.
/// </para>
/// <para>
/// A hang guard has one trap: when it fires, the stream's <c>MoveNextAsync</c> is still running,
/// and disposing an async stream at that moment fails (or leaves the stream's own clean-up running
/// in the background). So on a timeout this reader first cancels the stream's token, waits for the
/// running read to end, and only then reports the timeout; and <see cref="DisposeAsync"/> does the
/// same when a read is still running. When no read is running, disposing ends the stream normally,
/// like a <c>break</c> out of <c>await foreach</c>, without cancelling it.
/// </para>
/// </remarks>
/// <typeparam name="T">The item type.</typeparam>
public sealed class GuardedStreamReader<T> : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly IAsyncEnumerator<T> _items;
    private readonly TimeSpan _hangGuard;
    private readonly TimeProvider _time;
    private Task<bool>? _pending;
    private bool _disposed;

    /// <summary>Start reading the stream that <paramref name="open"/> returns.</summary>
    /// <param name="open">
    /// Opens the stream with the token this reader cancels when a read times out or the reader is
    /// disposed mid-read; pass it on to the stream (for example as <c>ReceiveLiveAsync</c>'s
    /// <c>ct</c>).
    /// </param>
    /// <param name="hangGuard">How long one read may take before it counts as hung.</param>
    /// <param name="time">The clock the hang guard runs on (tests pass a manual one).</param>
    public GuardedStreamReader(Func<CancellationToken, IAsyncEnumerable<T>> open, TimeSpan hangGuard, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hangGuard, TimeSpan.Zero);
        _hangGuard = hangGuard;
        _time = time ?? TimeProvider.System;
        _items = open(_stop.Token).GetAsyncEnumerator(_stop.Token);
    }

    /// <summary>
    /// The next item. Fails with <see cref="TimeoutException"/> when it takes longer than the hang
    /// guard; by then the read has been cancelled and has ended.
    /// </summary>
    /// <param name="what">What is being waited for, for the error message.</param>
    /// <returns>The item.</returns>
    /// <exception cref="TimeoutException">No item arrived within the hang guard.</exception>
    /// <exception cref="InvalidOperationException">The stream ended first, or a read is still running.</exception>
    public async Task<T> NextAsync(string what)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is { IsCompleted: false })
            throw new InvalidOperationException("The previous read is still running.");

        var read = _items.MoveNextAsync().AsTask();
        _pending = read;
        bool moved;
        try
        {
            moved = await read.WaitAsync(_hangGuard, _time).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (!read.IsCompleted)
        {
            await StopRunningReadAsync().ConfigureAwait(false);
            throw new TimeoutException($"Timed out waiting for {what}.", ex);
        }

        if (!moved)
            throw new InvalidOperationException($"The stream ended before {what}.");
        return _items.Current;
    }

    /// <summary>
    /// End the stream. With no read running this is a normal end, like <c>break</c>; with a read
    /// running, the stream's token is cancelled and the read is waited for first.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (!await StopRunningReadAsync().ConfigureAwait(false))
            return; // the read is still running after the hang guard: never dispose a stream mid-read

        try
        {
            await _items.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped on purpose.
        }
        _stop.Dispose();
    }

    // Cancel a read that is still running and wait (up to the hang guard) for it to end, observing
    // its outcome: the caller reports its own reason. True when no read is running any more.
    private async Task<bool> StopRunningReadAsync()
    {
        if (_pending is not { IsCompleted: false } read)
            return true;

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await read.WaitAsync(_hangGuard, _time).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The cancellation itself, the stream's own failure, or a read that still did not end.
        }

        if (!read.IsCompleted)
            return false;
        _ = read.Exception; // observed
        return true;
    }
}
