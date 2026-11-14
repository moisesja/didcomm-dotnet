using System.Diagnostics.Metrics;
using DidComm.Diagnostics;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The closed vocabulary of the <c>didcomm.mediator.push.outcomes</c> counter: one value per push
/// attempt (taking the grant's next record from the store and pushing it, live or direct; a fault or
/// the deadline while taking counts as a failed attempt), and one per wake-up of a
/// grant that was skipped or could push nothing it had, saying why. A wake-up that finds nothing
/// left to push (its message already pushed by a running operation, or taken by a pickup) is no
/// outcome. A message is never counted before a push takes it.
/// </summary>
internal static class PushOutcomes
{
    /// <summary>A record was pushed over the recipient's live session; it stays leased to the session until acknowledged.</summary>
    internal const string PushedLive = "Pushed-Live";

    /// <summary>A record was pushed to the recipient's endpoint, which answered with a 2xx status; the queued copy was removed.</summary>
    internal const string Pushed = "Pushed";

    /// <summary>
    /// A push did not deliver and its record stays queued (always available to pickup; a live push
    /// that was given back is tried again at the grant's next wake-up, a direct push that was sent is
    /// not tried again): the live session did not take it or it was too large for the
    /// session even alone (then passed over), the endpoint did not answer with a 2xx, a transport,
    /// store or session fault, or the deadline. At the deadline the worker stops waiting without
    /// knowing the result: the push runs on until its call ends and may still deliver (a direct
    /// push's 2xx then removes the record; a live session that reports it sent keeps it leased).
    /// </summary>
    internal const string Failed = "Push-Failed";

    /// <summary>A wake-up started nothing: <c>2 × PushConcurrency</c> push operations had not ended yet. The messages stay queued.</summary>
    internal const string SkippedSaturated = "Push-Skipped-Saturated";

    /// <summary>A wake-up with direct push on pushed nothing: no live push applied, and there is no directory, no endpoint for the grant, or the directory failed.</summary>
    internal const string SkippedNoEndpoint = "Push-Skipped-NoEndpoint";

    /// <summary>A wake-up with direct push off pushed nothing: the recipient no longer had a live session with live delivery on.</summary>
    internal const string SkippedNoLiveSession = "Push-Skipped-NoLiveSession";

    /// <summary>A wake-up with direct push on pushed nothing: the directory named an endpoint that is not absolute <c>http</c> or <c>https</c>.</summary>
    internal const string SkippedScheme = "Push-Skipped-Scheme";

    /// <summary>A record taken for a push was removed instead: its recipient DID is no longer registered under the grant.</summary>
    internal const string SkippedUnregistered = "Push-Skipped-Unregistered";

    /// <summary>A wake-up was skipped: the grant is paused after one of its pushes reached the deadline. The messages stay queued.</summary>
    internal const string SkippedCooldown = "Push-Skipped-Cooldown";

    /// <summary>The mediator was shutting down: a waiting wake-up was dropped, or a running push abandoned. The messages stay queued.</summary>
    internal const string SkippedShutdown = "Push-Skipped-Shutdown";
}

/// <summary>
/// The mediator pipeline's instruments on the <see cref="DidCommDiagnostics.MeterName"/> meter: the
/// push outcomes, the purged-message counter, and observable views of the in-memory replay
/// guard, session registry and message store. Thread-safe; recording never throws. Tags are a
/// closed vocabulary and never carry a DID.
/// </summary>
internal sealed class MediatorMetrics : IDisposable
{
    /// <summary>Counter of push outcomes (live and direct), tagged <see cref="PushOutcomeTag"/>.</summary>
    internal const string PushOutcomesInstrument = "didcomm.mediator.push.outcomes";

    /// <summary>The push counter's single tag key; values from <see cref="PushOutcomes"/>.</summary>
    internal const string PushOutcomeTag = "didcomm.push.outcome";

    /// <summary>Counter of messages the scheduled purge removed from the pickup queue.</summary>
    internal const string QueuePurgedInstrument = "didcomm.mediator.queue.purged";

    /// <summary>Observable counter: in-scope messages the in-memory replay guard refused because its budget was full.</summary>
    internal const string ReplaySaturatedInstrument = "didcomm.replay.saturated";

    /// <summary>Observable counter: in-scope messages the in-memory replay guard refused because the clock read behind its latest purge instant.</summary>
    internal const string ReplayClockBehindInstrument = "didcomm.replay.clock_behind_refusals";

    /// <summary>Observable counter: session lifecycle notifications the in-memory session registry dropped.</summary>
    internal const string DroppedNotificationsInstrument = "didcomm.return_route.dropped_notifications";

    /// <summary>Observable gauge: messages held by the in-memory message store.</summary>
    internal const string QueueMessagesInstrument = "didcomm.mediator.queue.messages";

    /// <summary>Observable gauge: payload bytes held by the in-memory message store.</summary>
    internal const string QueueBytesInstrument = "didcomm.mediator.queue.bytes";

    private readonly Counter<long> _pushes;
    private readonly Counter<long> _purged;
    private readonly bool _ownsMeter;
    private int _observablesRegistered;

    /// <summary>Create the instruments.</summary>
    /// <param name="meterFactory">The host's meter factory, which then owns the meter; <c>null</c> to create a meter this instance owns and disposes.</param>
    public MediatorMetrics(IMeterFactory? meterFactory)
    {
        if (meterFactory is not null)
        {
            Meter = meterFactory.Create(DidCommDiagnostics.MeterName);
        }
        else
        {
            Meter = new Meter(DidCommDiagnostics.MeterName);
            _ownsMeter = true;
        }

        _pushes = Meter.CreateCounter<long>(PushOutcomesInstrument, unit: "{message}");
        _purged = Meter.CreateCounter<long>(QueuePurgedInstrument, unit: "{message}");
    }

    /// <summary>The meter the instruments belong to.</summary>
    public Meter Meter { get; }

    /// <summary>Count one push outcome.</summary>
    /// <param name="outcome">A <see cref="PushOutcomes"/> value.</param>
    public void RecordPush(string outcome)
    {
        try
        {
            _pushes.Add(1, new KeyValuePair<string, object?>(PushOutcomeTag, outcome));
        }
        catch
        {
            // Metric listeners are host code; they must not change how a message is handled.
        }
    }

    /// <summary>Count messages removed by the scheduled purge.</summary>
    /// <param name="count">How many.</param>
    public void RecordPurged(long count)
    {
        try
        {
            _purged.Add(count);
        }
        catch
        {
            // As above.
        }
    }

    /// <summary>
    /// Register the observable instruments, once, for whichever of the given services is the
    /// library's in-memory type (a host's own implementation has its own telemetry). Each callback
    /// reads a counter of the instance and reports nothing if the read fails.
    /// </summary>
    /// <param name="replayGuard">The replay guard in effect.</param>
    /// <param name="sessions">The session registry in effect.</param>
    /// <param name="store">The message store in effect.</param>
    public void RegisterObservables(IInboundReplayGuard? replayGuard, IReturnRouteSessionRegistry? sessions, IMessageStore? store)
    {
        if (Interlocked.Exchange(ref _observablesRegistered, 1) != 0)
            return;

        if (replayGuard is InMemoryReplayGuard guard)
        {
            Meter.CreateObservableCounter(ReplaySaturatedInstrument, () => Observe(() => guard.SaturationCount), unit: "{message}");
            Meter.CreateObservableCounter(ReplayClockBehindInstrument, () => Observe(() => guard.ClockBehindRefusalCount), unit: "{message}");
        }
        if (sessions is InMemoryReturnRouteSessionRegistry registry)
            Meter.CreateObservableCounter(DroppedNotificationsInstrument, () => Observe(() => registry.DroppedNotifications), unit: "{notification}");
        if (store is InMemoryMessageStore memory)
        {
            Meter.CreateObservableGauge(QueueMessagesInstrument, () => Observe(() => memory.MessageCount), unit: "{message}");
            Meter.CreateObservableGauge(QueueBytesInstrument, () => Observe(() => memory.TotalBytes), unit: "By");
        }
    }

    private static IEnumerable<Measurement<long>> Observe(Func<long> read)
    {
        long value;
        try
        {
            value = read();
        }
        catch
        {
            return Array.Empty<Measurement<long>>();
        }
        return new[] { new Measurement<long>(value) };
    }

    /// <summary>Dispose the meter when this instance created it; a factory's meter is the factory's to dispose.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }
}
