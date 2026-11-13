using System.Diagnostics.Metrics;
using DidComm.Diagnostics;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// The closed vocabulary of the <c>didcomm.mediator.push.outcomes</c> counter: exactly one value per
/// stored message the forward sink handed to the push workers (or that they refused to take).
/// </summary>
internal static class PushOutcomes
{
    /// <summary>The recipient's live session took the <c>delivery</c>; the record stays leased to the session until acknowledged.</summary>
    internal const string PushedLive = "Pushed-Live";

    /// <summary>Direct push: the recipient's endpoint answered with a 2xx status; the queued copy was removed.</summary>
    internal const string Pushed = "Pushed";

    /// <summary>
    /// The last push tried did not deliver, and the message stays queued: a live session that did
    /// not take the <c>delivery</c> (with direct push off), or a direct push that failed (a transport
    /// error, a non-2xx answer); or the deadline, a store, session or client fault ended the push.
    /// </summary>
    internal const string Failed = "Push-Failed";

    /// <summary>The push backlog, or the grant's share of it, was full; the message stays queued.</summary>
    internal const string SkippedSaturated = "Push-Skipped-Saturated";

    /// <summary>Direct push: the message was not pushed live (no live session, or the session did not take it), and there is no directory, no endpoint for the grant, or the directory failed; the message stays queued.</summary>
    internal const string SkippedNoEndpoint = "Push-Skipped-NoEndpoint";

    /// <summary>Direct push is off and the recipient no longer had a live session with live delivery on when the push ran; the message stays queued.</summary>
    internal const string SkippedNoLiveSession = "Push-Skipped-NoLiveSession";

    /// <summary>Direct push: the directory named an endpoint that is not absolute <c>http</c> or <c>https</c>; the message stays queued.</summary>
    internal const string SkippedScheme = "Push-Skipped-Scheme";

    /// <summary>The recipient DID was no longer registered under the grant when the push was about to run.</summary>
    internal const string SkippedUnregistered = "Push-Skipped-Unregistered";

    /// <summary>
    /// The queue no longer held the message for delivery when the push was about to take it:
    /// expired or aged out, not yet due, leased to a pickup, passed over, acknowledged or purged.
    /// Nothing was sent; whatever the queue still holds stays for pickup.
    /// </summary>
    internal const string SkippedNotHeld = "Push-Skipped-NotHeld";

    /// <summary>
    /// The recipient's pushes were paused: one of its pushes (live or direct) reached the deadline
    /// shortly before, so its waiting and new pushes skip the workers for a cooldown; the message stays queued.
    /// </summary>
    internal const string SkippedCooldown = "Push-Skipped-Cooldown";

    /// <summary>The mediator was shutting down: the push was abandoned or never started; the message stays queued.</summary>
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
