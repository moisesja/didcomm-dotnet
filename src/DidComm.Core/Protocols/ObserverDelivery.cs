using System.Threading.Channels;
using DidComm.Facade;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols;

/// <summary>
/// Bounded, background, best-effort delivery of inbound observations to
/// <see cref="IProtocolObserver"/>s (FR-PROTO-12). Each observer gets its own bounded channel drained
/// by its own pump, so enqueue is non-blocking (never gates the dispatch outcome or reply delivery),
/// a slow/hung observer backs up only its own channel, and observer faults are isolated and logged.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Snapshot at enqueue.</strong> Each observation is deep-cloned into an immutable
/// <see cref="InboundObservation"/> synchronously at enqueue, so a handler or caller that mutates the
/// live message after dispatch cannot change what an observer later sees, and no two observers can see
/// different states.
/// </para>
/// <para>
/// <strong>Bounded, best-effort.</strong> Each observer's queue is bounded by BOTH an item count and a
/// byte budget (default a few MiB); when either is exceeded the newest observation is dropped and a
/// <em>rate-limited</em> warning is logged (first drop + periodically), so a flood cannot exhaust
/// memory or amplify logging. Delivery is therefore best-effort / at-most-once. Note the built-in
/// Discover Features correlation does NOT use this queue — it is a lossless inline
/// <see cref="IInboundCorrelator"/> — so a default deployment registers no observer here and has no
/// queue to flood.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Observers may be invoked concurrently with one another; per observer,
/// its own invocations are serialized in arrival order. Implementations MUST be thread-safe.
/// </para>
/// </remarks>
internal sealed class ObserverDelivery : IDisposable, IAsyncDisposable
{
    /// <summary>Default per-observer queued-item cap.</summary>
    internal const int DefaultCapacity = 64;

    /// <summary>Default per-observer queued-byte budget (approximate serialized size).</summary>
    internal const long DefaultByteBudget = 4L * 1024 * 1024;

    /// <summary>Log at most one drop warning per this many drops (after the first), to avoid log amplification.</summary>
    private const long DropLogEvery = 1000;

    private readonly ObserverChannel[] _channels;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed; // 0/1 — makes Dispose()/DisposeAsync() idempotent (a CTS throws if cancelled after dispose)

    private sealed record WorkItem(InboundObservation? Observation, int Bytes, TaskCompletionSource? Barrier);

    private sealed class ObserverChannel
    {
        public required IProtocolObserver Observer { get; init; }
        public required string? Filter { get; init; }
        public required bool Disabled { get; init; }
        public required long ByteBudget { get; init; }
        public required Channel<WorkItem> Channel { get; init; }
        public Task Pump = Task.CompletedTask; // assigned after construction so the pump can reference `this`
        public long QueuedBytes;
        public long Dropped;
    }

    public ObserverDelivery(
        IReadOnlyList<IProtocolObserver> observers, ILogger? logger,
        int capacity = DefaultCapacity, long byteBudget = DefaultByteBudget)
    {
        ArgumentNullException.ThrowIfNull(observers);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteBudget < 1) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        _logger = logger;
        _channels = new ObserverChannel[observers.Count];
        for (var i = 0; i < observers.Count; i++)
        {
            var observer = observers[i];
            // Read ProtocolUriFilter EXACTLY ONCE, here, guarded — it is arbitrary observer code and
            // must never run on the dispatch path (Enqueue). If the getter throws, disable the observer.
            string? filter = null;
            var disabled = false;
            try
            {
                filter = observer.ProtocolUriFilter;
            }
            catch (Exception ex)
            {
                disabled = true;
                _logger?.LogWarning(ex,
                    "IProtocolObserver '{Observer}' threw from ProtocolUriFilter at registration; it is disabled and will receive nothing (FR-PROTO-12).",
                    observer.GetType().FullName);
            }
            var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → our drop policy
                SingleReader = true,
                SingleWriter = false,
            });
            var oc = new ObserverChannel
            {
                Observer = observer,
                Filter = filter,
                Disabled = disabled,
                ByteBudget = byteBudget,
                Channel = channel,
            };
            // Start the pump after the channel object exists so it can decrement that channel's
            // queued-byte counter as it drains.
            if (!disabled)
                oc.Pump = Task.Run(() => PumpAsync(oc, _shutdown.Token));
            _channels[i] = oc;
        }
    }

    /// <summary>
    /// Enqueue an inbound message for every observer whose filter matches. Non-blocking and
    /// structurally non-throwing: runs no observer code, snapshots the message at enqueue, and drops
    /// (with a rate-limited log) when an observer's item-count or byte budget is exceeded.
    /// </summary>
    public void Enqueue(UnpackResult received)
    {
        foreach (var oc in _channels)
        {
            if (oc.Disabled)
                continue;
            if (!ObserverMatches(oc.Filter, received.Message.Type))
                continue;

            // Byte-budget pre-check BEFORE cloning: a byte-flood is dropped without paying the clone
            // cost. (Item-count is enforced by the bounded channel's TryWrite below.)
            if (Interlocked.Read(ref oc.QueuedBytes) >= oc.ByteBudget)
            {
                Drop(oc, received.Message.Id);
                continue;
            }

            var observation = InboundObservation.FromUnpackResult(received, out var bytes);
            // Reserve-then-verify so the byte budget is a HARD bound even under concurrent enqueues:
            // atomically add first, and if that put us over budget, back the reservation out and drop.
            // (Without this, N concurrent enqueues could each pass the pre-check and all add, overshooting.)
            if (Interlocked.Add(ref oc.QueuedBytes, bytes) > oc.ByteBudget)
            {
                Interlocked.Add(ref oc.QueuedBytes, -bytes);
                Drop(oc, received.Message.Id);
            }
            else if (!oc.Channel.Writer.TryWrite(new WorkItem(observation, bytes, Barrier: null)))
            {
                Interlocked.Add(ref oc.QueuedBytes, -bytes); // item-count full: release the reservation
                Drop(oc, received.Message.Id);
            }
        }
    }

    private void Drop(ObserverChannel oc, string messageId)
    {
        var n = Interlocked.Increment(ref oc.Dropped);
        // Rate-limit: log the first drop and then only periodically, so a flood cannot amplify into
        // one synchronous log write per message.
        if (n == 1 || n % DropLogEvery == 0)
        {
            _logger?.LogWarning(
                "Observer '{Observer}' is not keeping up; dropped inbound observation {MessageId} (queue full: capacity {Capacity} items / {ByteBudget} bytes). Total dropped for this observer: {Dropped} (FR-PROTO-12, best-effort delivery).",
                oc.Observer.GetType().FullName, DefaultCapacity, oc.ByteBudget, messageId, n);
        }
    }

    private async Task PumpAsync(ObserverChannel oc, CancellationToken shutdown)
    {
        try
        {
            await foreach (var item in oc.Channel.Reader.ReadAllAsync(shutdown).ConfigureAwait(false))
            {
                if (item.Barrier is not null)
                {
                    item.Barrier.TrySetResult(); // flush marker
                    continue;
                }

                // Free the item's reserved bytes as it leaves the queue (before the callback, so a slow
                // callback doesn't count against the budget for messages already dequeued).
                Interlocked.Add(ref oc.QueuedBytes, -item.Bytes);
                try
                {
                    await oc.Observer.OnMessageReceivedAsync(item.Observation!, shutdown).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(
                        ex,
                        "IProtocolObserver '{Observer}' threw while observing inbound message {MessageId}; isolated — the dispatch outcome is unaffected (FR-PROTO-12).",
                        oc.Observer.GetType().FullName, item.Observation!.Message.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; stop draining.
        }
    }

    /// <summary>
    /// Test/diagnostic seam: complete when every observation enqueued <em>before this call</em> has
    /// been processed. Honors <paramref name="timeout"/> for the WHOLE operation (including writing the
    /// barrier into a full channel), so a hung/full observer cannot block it forever.
    /// </summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var barriers = new List<Task>(_channels.Length);
        foreach (var oc in _channels)
        {
            if (oc.Disabled)
                continue; // no pump to drain; a barrier would never complete
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await oc.Channel.Writer.WriteAsync(new WorkItem(Observation: null, Bytes: 0, Barrier: tcs), cts.Token).ConfigureAwait(false);
            barriers.Add(tcs.Task);
        }
        await Task.WhenAll(barriers).WaitAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Audit description of registered observers, computed from cached data — invokes NO observer code.</summary>
    public string Describe()
        => string.Join("; ", _channels.Select(c =>
            $"{c.Observer.GetType().FullName} (filter: {c.Filter ?? "ALL"}{(c.Disabled ? "; DISABLED" : "")})"));

    /// <summary>Cancel the pumps and complete the channels; does not block. (Best-effort synchronous stop.)</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
        _shutdown.Cancel();
        foreach (var oc in _channels)
            oc.Channel.Writer.TryComplete();
        _shutdown.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
        _shutdown.Cancel(); // real shutdown: cancels ReadAllAsync + the in-flight observer callback token
        foreach (var oc in _channels)
            oc.Channel.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_channels.Select(c => c.Pump)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An observer that ignores its cancellation token can still hang; don't block disposal on it.
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    // A filter observes its whole protocol family: same protocol name and major version, any minor.
    // TryParse throughout so a crafted/malformed type fails closed to "no match" rather than throwing.
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
