using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols;

/// <summary>
/// Bounded, background, best-effort delivery of immutable inbound snapshots to
/// <see cref="IProtocolObserver"/>s (FR-PROTO-12). Each observer has an independent pump and
/// receives its own mutable message clone only after the snapshot has passed item and byte admission.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Admission before materialization.</strong> The unpack boundary preserves the exact verified
/// plaintext once as an immutable <see cref="InboundMessageSnapshot"/>. Enqueue reserves one
/// outstanding item and its exact UTF-8 byte count before writing a snapshot reference. The pump
/// deserializes an independent <see cref="InboundObservation"/> only for accepted work, so a full
/// queue cannot force repeated full-message clones on the receive path.
/// </para>
/// <para>
/// <strong>Hard bounds.</strong> Each observer is bounded by both outstanding item count and exact
/// plaintext UTF-8 bytes. The accounting includes queued and in-flight work. Overflow drops the newest
/// observation and emits a rate-limited warning. Delivery is best-effort / at-most-once.
/// </para>
/// <para>
/// <strong>Shutdown.</strong> Disposal atomically stops admission, cancels cooperative callbacks, and
/// drains queued snapshots. A trusted observer callback that ignores cancellation cannot be forcibly
/// stopped by .NET; only that single in-flight observation can remain until the callback returns.
/// </para>
/// </remarks>
internal sealed class ObserverDelivery : IDisposable, IAsyncDisposable
{
    internal const int DefaultCapacity = 64;
    internal const long DefaultByteBudget = 4L * 1024 * 1024;

    private static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);
    private const long DropLogEvery = 1000;

    private readonly ObserverChannel[] _channels;
    private readonly ILogger? _logger;
    private readonly Func<InboundMessageSnapshot, InboundObservation> _materialize;
    private readonly TimeSpan _shutdownGrace;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly TaskCompletionSource _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _shutdownStarted;

    private sealed record WorkItem(InboundMessageSnapshot? Snapshot, TaskCompletionSource? Barrier);

    private sealed class ObserverChannel
    {
        public object Gate { get; } = new();
        public required IProtocolObserver Observer { get; init; }
        public required string? Filter { get; init; }
        public required bool Disabled { get; init; }
        public required int Capacity { get; init; }
        public required long ByteBudget { get; init; }
        public required Channel<WorkItem> Channel { get; init; }
        public Task Pump = Task.CompletedTask;
        public int OutstandingItems;
        public long OutstandingBytes;
        public long Dropped;
    }

    public ObserverDelivery(
        IReadOnlyList<IProtocolObserver> observers,
        ILogger? logger,
        int capacity = DefaultCapacity,
        long byteBudget = DefaultByteBudget,
        TimeSpan? shutdownGrace = null,
        Func<InboundMessageSnapshot, InboundObservation>? materialize = null)
    {
        ArgumentNullException.ThrowIfNull(observers);
        if (capacity < 1 || capacity == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteBudget < 1)
            throw new ArgumentOutOfRangeException(nameof(byteBudget));

        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
        if (_shutdownGrace <= TimeSpan.Zero && _shutdownGrace != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));

        _logger = logger;
        _materialize = materialize ?? InboundObservation.FromSnapshot;
        _shutdownToken = _shutdown.Token; // capture before any pump can start
        _channels = new ObserverChannel[observers.Count];

        for (var i = 0; i < observers.Count; i++)
        {
            var observer = observers[i];
            string? filter = null;
            var disabled = false;
            try
            {
                filter = observer.ProtocolUriFilter;
            }
            catch (Exception ex)
            {
                disabled = true;
                SafeLogWarning(
                    ex,
                    "IProtocolObserver '{Observer}' threw from ProtocolUriFilter at registration; it is disabled and will receive nothing (FR-PROTO-12).",
                    observer.GetType().FullName);
            }

            // One extra physical slot is reserved for the serialized Flush barrier. Observation
            // admission is independently capped at `capacity`, including the in-flight callback.
            var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity + 1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false, // the shutdown path may drain while the pump is in a callback
                SingleWriter = false,
            });
            var observerChannel = new ObserverChannel
            {
                Observer = observer,
                Filter = filter,
                Disabled = disabled,
                Capacity = capacity,
                ByteBudget = byteBudget,
                Channel = channel,
            };

            if (!disabled)
                observerChannel.Pump = Task.Run(() => PumpAsync(observerChannel, _shutdownToken));
            _channels[i] = observerChannel;
        }
    }

    /// <summary>
    /// Admit one immutable snapshot to each matching observer. This method executes no observer or
    /// message-materialization code and returns immediately; full/budget-exhausted observers drop it.
    /// </summary>
    internal void Enqueue(InboundMessageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _shutdownStarted) != 0)
            return;

        foreach (var observerChannel in _channels)
        {
            if (observerChannel.Disabled || !ObserverMatches(observerChannel.Filter, snapshot.Type))
                continue;

            var accepted = false;
            lock (observerChannel.Gate)
            {
                if (Volatile.Read(ref _shutdownStarted) == 0
                    && observerChannel.OutstandingItems < observerChannel.Capacity
                    && snapshot.Utf8ByteCount <= observerChannel.ByteBudget - observerChannel.OutstandingBytes)
                {
                    observerChannel.OutstandingItems++;
                    observerChannel.OutstandingBytes += snapshot.Utf8ByteCount;
                    accepted = observerChannel.Channel.Writer.TryWrite(
                        new WorkItem(snapshot, Barrier: null));
                    if (!accepted)
                        ReleaseReservationUnderLock(observerChannel, snapshot.Utf8ByteCount);
                }
            }

            if (!accepted && Volatile.Read(ref _shutdownStarted) == 0)
                Drop(observerChannel, snapshot.Id);
        }
    }

    private async Task PumpAsync(ObserverChannel observerChannel, CancellationToken shutdown)
    {
        try
        {
            await foreach (var item in observerChannel.Channel.Reader.ReadAllAsync(shutdown).ConfigureAwait(false))
            {
                if (item.Barrier is not null)
                {
                    item.Barrier.TrySetResult();
                    continue;
                }

                var snapshot = item.Snapshot!;
                try
                {
                    var observation = _materialize(snapshot);
                    await observerChannel.Observer.OnMessageReceivedAsync(observation, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    // Normal cooperative shutdown.
                }
                catch (Exception ex)
                {
                    SafeLogWarning(
                        ex,
                        "IProtocolObserver '{Observer}' failed while observing inbound message {MessageId}; isolated — the dispatch outcome is unaffected (FR-PROTO-12).",
                        observerChannel.Observer.GetType().FullName,
                        snapshot.Id);
                }
                finally
                {
                    ReleaseReservation(observerChannel, snapshot.Utf8ByteCount);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Normal pump shutdown.
        }
        catch (Exception ex)
        {
            SafeLogWarning(
                ex,
                "Observer delivery pump for '{Observer}' faulted; the fault was observed and isolated (FR-PROTO-12).",
                observerChannel.Observer.GetType().FullName);
        }
        finally
        {
            DrainQueued(observerChannel);
        }
    }

    /// <summary>
    /// Complete when every observation admitted before this call has been processed. The timeout
    /// covers flush serialization, barrier insertion, and callback completion.
    /// </summary>
    internal async Task FlushAsync(TimeSpan timeout)
    {
        ThrowIfShutdown();
        using var cts = new CancellationTokenSource(timeout);
        await _flushGate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfShutdown();
            var barriers = new List<Task>(_channels.Length);
            foreach (var observerChannel in _channels)
            {
                if (observerChannel.Disabled)
                    continue;

                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (observerChannel.Gate)
                {
                    ThrowIfShutdown();
                    // Observation admission is capped at Capacity while the channel has Capacity+1
                    // physical slots, and Flush calls are serialized. The barrier therefore fits.
                    if (!observerChannel.Channel.Writer.TryWrite(
                            new WorkItem(Snapshot: null, Barrier: barrier)))
                    {
                        throw new InvalidOperationException("Observer flush barrier could not be admitted despite its reserved channel slot.");
                    }
                }
                barriers.Add(barrier.Task);
            }

            await Task.WhenAll(barriers).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    internal string Describe()
        => string.Join("; ", _channels.Select(channel =>
            $"{channel.Observer.GetType().FullName} (filter: {channel.Filter ?? "ALL"}{(channel.Disabled ? "; DISABLED" : "")})"));

    /// <summary>Test/diagnostic seam for proving shutdown and reservation accounting.</summary>
    internal Task ShutdownCompletion => _shutdownCompletion.Task;

    /// <summary>Test/diagnostic seam for one observer's outstanding item/byte charge.</summary>
    internal (int Items, long Bytes) GetOutstanding(int observerIndex)
    {
        var observerChannel = _channels[observerIndex];
        lock (observerChannel.Gate)
            return (observerChannel.OutstandingItems, observerChannel.OutstandingBytes);
    }

    /// <summary>Begin cancellation/drain without blocking on callbacks.</summary>
    public void Dispose() => BeginShutdown();

    /// <summary>
    /// Begin cancellation/drain and await cooperative pump completion for the configured grace.
    /// A callback that ignores cancellation may outlive this method, but its eventual completion is
    /// still observed and owns the CTS until it exits.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var completion = BeginShutdown();
        try
        {
            await completion.WaitAsync(_shutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The one in-flight callback may ignore cancellation. Queued snapshots were already
            // drained; ObservePumpsAndDisposeAsync will observe it and dispose the CTS if it returns.
        }
    }

    private Task BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            foreach (var observerChannel in _channels)
                observerChannel.Channel.Writer.TryComplete();

            try
            {
                _shutdown.Cancel(throwOnFirstException: false);
            }
            catch (Exception ex)
            {
                SafeLogWarning(ex, "An observer cancellation callback threw during shutdown; continuing to drain observer queues.");
            }

            foreach (var observerChannel in _channels)
                DrainQueued(observerChannel);

            _ = ObservePumpsAndDisposeAsync();
        }

        return _shutdownCompletion.Task;
    }

    private async Task ObservePumpsAndDisposeAsync()
    {
        try
        {
            await Task.WhenAll(_channels.Select(channel => channel.Pump)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Pumps guard their own work, but observe any unexpected fault here as a final boundary.
            SafeLogWarning(ex, "One or more observer delivery pumps faulted during shutdown; faults were observed.");
        }
        finally
        {
            _shutdown.Dispose();
            _shutdownCompletion.TrySetResult();
        }
    }

    private void DrainQueued(ObserverChannel observerChannel)
    {
        while (observerChannel.Channel.Reader.TryRead(out var item))
        {
            if (item.Barrier is not null)
            {
                item.Barrier.TrySetException(new ObjectDisposedException(nameof(ObserverDelivery)));
                continue;
            }

            ReleaseReservation(observerChannel, item.Snapshot!.Utf8ByteCount);
        }
    }

    private static void ReleaseReservation(ObserverChannel observerChannel, int bytes)
    {
        lock (observerChannel.Gate)
            ReleaseReservationUnderLock(observerChannel, bytes);
    }

    private static void ReleaseReservationUnderLock(ObserverChannel observerChannel, int bytes)
    {
        observerChannel.OutstandingItems--;
        observerChannel.OutstandingBytes -= bytes;
    }

    private void Drop(ObserverChannel observerChannel, string messageId)
    {
        var dropped = Interlocked.Increment(ref observerChannel.Dropped);
        if (dropped != 1 && dropped % DropLogEvery != 0)
            return;

        SafeLogWarning(
            exception: null,
            "Observer '{Observer}' is not keeping up; dropped inbound observation {MessageId} " +
            "(outstanding bound: {Capacity} items / {ByteBudget} UTF-8 bytes). " +
            "Total dropped for this observer: {Dropped} (FR-PROTO-12, best-effort delivery).",
            observerChannel.Observer.GetType().FullName,
            messageId,
            observerChannel.Capacity,
            observerChannel.ByteBudget,
            dropped);
    }

    private void SafeLogWarning(Exception? exception, string message, params object?[] args)
    {
        try
        {
            if (exception is null)
                _logger?.LogWarning(message, args);
            else
                _logger?.LogWarning(exception, message, args);
        }
        catch
        {
            // Logging providers are host code. They cannot be allowed to violate the side-channel's
            // structural promise that observation never changes dispatch or shutdown behavior.
        }
    }

    private void ThrowIfShutdown()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
            throw new ObjectDisposedException(nameof(ObserverDelivery));
    }

    // A filter observes its whole protocol family: same protocol name and major version, any minor.
    internal static bool ObserverMatches(string? filter, string? messageType)
    {
        if (filter is null)
            return true;
        if (!MessageTypeUri.TryParse(messageType, out var mturi))
            return false;
        if (!ProtocolIdentifier.TryParse(filter, out var filterPiuri))
            return false;
        if (!ProtocolIdentifier.TryParse(mturi!.ProtocolIdentifier, out var inboundPiuri))
            return false;
        return filterPiuri.MatchesProtocolAndMajor(inboundPiuri);
    }
}
