using System.Threading.Channels;
using DidComm.Facade;
using Microsoft.Extensions.Logging;

namespace DidComm.Protocols;

/// <summary>
/// Bounded, background delivery of inbound observations to <see cref="IProtocolObserver"/>s
/// (FR-PROTO-12). Each observer gets its <em>own</em> bounded channel drained by its own pump, so:
/// <list type="bullet">
///   <item><description>enqueue is non-blocking and never gates the dispatch outcome or reply
///     delivery — <see cref="ProtocolDispatcher"/> enqueues and returns immediately;</description></item>
///   <item><description>a slow or never-completing observer backs up only its own channel (and
///     eventually drops, per the overflow policy) — it cannot starve other observers or block the
///     receive path;</description></item>
///   <item><description>observer faults are isolated and logged; they can never reach the
///     dispatch result.</description></item>
/// </list>
/// The message is deep-cloned lazily <em>in the pump</em>, so a dropped observation pays no clone
/// cost and each observer still gets its own isolated copy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Overflow policy.</strong> Channels are bounded to a fixed capacity; when an
/// observer's channel is full (it is not keeping up) the newest observation for that observer is
/// dropped and a warning is logged with a running drop count. Dropping is explicit and visible
/// rather than growing memory unboundedly.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> Observers may be invoked concurrently with one another (each has
/// its own pump) and concurrently across inbound messages; per observer, its own invocations are
/// serialized in arrival order by its single-reader pump. An observer implementation MUST be safe
/// under concurrent invocation.
/// </para>
/// </remarks>
internal sealed class ObserverDelivery : IDisposable, IAsyncDisposable
{
    /// <summary>Default per-observer channel capacity before the overflow policy drops.</summary>
    internal const int DefaultCapacity = 1024;

    private readonly ObserverChannel[] _channels;
    private readonly ILogger? _logger;

    private sealed record WorkItem(UnpackResult? Received, TaskCompletionSource? Barrier);

    private sealed class ObserverChannel
    {
        public required IProtocolObserver Observer { get; init; }
        public required Channel<WorkItem> Channel { get; init; }
        public required Task Pump { get; init; }
        public long Dropped;
    }

    public ObserverDelivery(IReadOnlyList<IProtocolObserver> observers, ILogger? logger, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(observers);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _logger = logger;
        _channels = new ObserverChannel[observers.Count];
        for (var i = 0; i < observers.Count; i++)
        {
            var observer = observers[i];
            var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
            {
                // Wait mode + TryWrite lets us observe "full" and apply our own drop-and-log policy,
                // rather than DropWrite silently discarding.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            _channels[i] = new ObserverChannel
            {
                Observer = observer,
                Channel = channel,
                Pump = Task.Run(() => PumpAsync(observer, channel)),
            };
        }
    }

    /// <summary>
    /// Enqueue an inbound message for every observer whose filter matches. Non-blocking: on a full
    /// channel the observation is dropped for that observer and a warning is logged.
    /// </summary>
    public void Enqueue(UnpackResult received)
    {
        foreach (var oc in _channels)
        {
            if (!ObserverMatches(oc.Observer.ProtocolUriFilter, received.Message.Type))
                continue;
            if (!oc.Channel.Writer.TryWrite(new WorkItem(received, Barrier: null)))
            {
                var n = Interlocked.Increment(ref oc.Dropped);
                _logger?.LogWarning(
                    "Observer '{Observer}' is not keeping up; its queue is full (capacity {Capacity}) so inbound observation {MessageId} was dropped. Total dropped for this observer: {Dropped} (FR-PROTO-12).",
                    oc.Observer.GetType().FullName, DefaultCapacity, received.Message.Id, n);
            }
        }
    }

    private async Task PumpAsync(IProtocolObserver observer, Channel<WorkItem> channel)
    {
        await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Barrier is not null)
            {
                // Flush marker: everything queued for this observer before it has now been processed.
                item.Barrier.TrySetResult();
                continue;
            }

            try
            {
                // Deep clone here (not at enqueue): dropped items pay no clone cost, and each
                // observer gets its own isolated copy that mutation can never leak out of.
                var observation = InboundObservation.FromUnpackResult(item.Received!);
                await observer.OnMessageReceivedAsync(observation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "IProtocolObserver '{Observer}' threw while observing inbound message {MessageId}; isolated — the dispatch outcome is unaffected (FR-PROTO-12).",
                    observer.GetType().FullName, item.Received!.Message.Id);
            }
        }
    }

    /// <summary>
    /// Test/diagnostic seam: complete when every observation enqueued <em>before this call</em> has
    /// been processed. Enqueues a FIFO barrier per observer and awaits it. A never-completing
    /// observer will not complete its barrier, so callers pass a <paramref name="timeout"/>.
    /// </summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var barriers = new List<Task>(_channels.Length);
        foreach (var oc in _channels)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await oc.Channel.Writer.WriteAsync(new WorkItem(Received: null, Barrier: tcs)).ConfigureAwait(false);
            barriers.Add(tcs.Task);
        }
        await Task.WhenAll(barriers).WaitAsync(timeout).ConfigureAwait(false);
    }

    /// <summary>Signal the pumps to stop (completes the channels); does not block on in-flight observers.</summary>
    public void Dispose()
    {
        foreach (var oc in _channels)
            oc.Channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var oc in _channels)
            oc.Channel.Writer.TryComplete();
        try
        {
            await Task.WhenAll(_channels.Select(c => c.Pump)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A hung observer can keep its pump from draining; don't block disposal on it.
        }
    }

    // A filter observes its whole protocol family: same protocol name and major version, any minor
    // (a 2.0 filter sees 2.1 traffic). TryParse throughout so a crafted/malformed type fails closed
    // to "no match" rather than throwing (see L-032).
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
