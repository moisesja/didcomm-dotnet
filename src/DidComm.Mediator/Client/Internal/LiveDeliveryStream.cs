using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// One <see cref="PickupClient.ReceiveLiveAsync"/> enumeration (#96 design §4.8 as amended by
/// orchestrator note R-2). Owner: engineer C.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Start.</strong> At the first <c>MoveNextAsync</c>: the route, refused before anything is
/// sent when it is not a WebSocket; then the live-push mailbox of the (mediator, local DID) pair,
/// refused when one is already open.
/// </para>
/// <para>
/// <strong>Cycle.</strong> <c>live-delivery-change{true}</c> → <c>status</c>, then a drain
/// (<c>delivery-request{10}</c> until a <c>status</c>, at most 50 rounds). Live mode is turned on
/// before the drain, so a message that arrives between the two is pushed (it is after the
/// mediator's arrival mark) or drained, never neither. The cycle runs at the start, every
/// <see cref="MediatorClientOptions.LivePollInterval"/> on the monotonic clock (which also picks up
/// delayed messages, never pushed when they fall due, and recovers a dropped connection), and after
/// a failed step with back-off from 1 s doubling to 30 s. The mediator has no scheduler for due
/// messages, now or later (owner decision D2 on #88, 2026-11-14), so this polling is the permanent
/// design, not a stopgap. Only a failure a retry can fix is retried (<see cref="Retryable"/>); five
/// in a row end the stream with the last exception, and any other failure (a
/// <see cref="MediationProtocolException"/> such as live mode not supported, a pack-stage error such
/// as <see cref="SecretNotFoundException"/>) ends it at once.
/// </para>
/// <para>
/// <strong>Items.</strong> Pushes are read from the mailbox (at most 10 messages per delivery). Each
/// delivery the stream takes — a drain round's answer or a push — is opened up front, all of its
/// messages under one budget (<see cref="MediatorClientOptions.UnpackTimeout"/>, owner decision 2-A),
/// on the consumer's own <c>MoveNextAsync</c> and never on a receive loop; then its results are
/// yielded in delivery order. The consumer's time with an item is therefore never counted against
/// the budget, and a message the budget runs out on is a failure item (a
/// <see cref="TimeoutException"/> naming the drain's request id). A message this stream already
/// holds (opened and not yet yielded, or taken and not yet acknowledged) is skipped and not opened
/// again; one the duplicate filter remembers is acknowledged and not yielded; a failure is yielded,
/// never acknowledged, and reported again when it comes back. A drain answered with a delivery in
/// which no message has a usable id ends the stream (nothing in it could be acknowledged, so the
/// drain would ask again for ever); such a push is ignored.
/// </para>
/// <para>
/// <strong>Acknowledgement (R-2).</strong> A yielded message is taken — added to the duplicate filter
/// and queued for acknowledgement — when the consumer asks for the next item; taken messages are
/// acknowledged when 10 are waiting, when nothing is ready, or when the oldest has waited 10 s, in
/// <c>messages-received</c> messages of at most <see cref="MaxAckIds"/> ids each (several when more
/// are waiting, as after an acknowledgement outage). When the enumeration ends for any reason the
/// last yielded message is not taken; what was taken is acknowledged, live mode is turned off, both
/// within one <see cref="MediatorClientOptions.RequestTimeout"/> whatever the caller's token, and
/// the mailbox is closed. Buffered or drained messages never yielded are never acknowledged.
/// </para>
/// <para>
/// <strong>Bounds.</strong> At most 10 opened messages ready (one delivery), the mailbox's 8 pushes, and at
/// most 100 taken waiting for an acknowledgement (10 unless acknowledgements fail); the set of held
/// ids is bounded by their sum. Not thread-safe: one consumer, as <c>IAsyncEnumerator</c> requires.
/// </para>
/// </remarks>
internal sealed class LiveDeliveryStream
{
    /// <summary>The <c>limit</c> of a drain round.</summary>
    internal const int DrainLimit = 10;

    /// <summary>The most drain rounds of one cycle.</summary>
    internal const int MaxDrainRounds = 50;

    /// <summary>The most messages read from one delivery (a push or a drain answer).</summary>
    internal const int MaxPerDelivery = 10;

    /// <summary>Taken messages are acknowledged once this many are waiting.</summary>
    internal const int FlushAt = 10;

    /// <summary>
    /// The most ids of one <c>messages-received</c>. A mediator may lower the ids it accepts per
    /// message to its delivery batch size (this library's: <c>MessagePickupOptions.MaxAckIds</c>, at
    /// least <c>MaxDeliveryBatch</c>, 10 by default) and refuses a longer message as malformed, which
    /// would leave a stream recovering from an acknowledgement outage unable to acknowledge anything.
    /// </summary>
    internal const int MaxAckIds = 10;

    /// <summary>The most taken messages waiting for an acknowledgement.</summary>
    internal const int MaxTaken = 100;

    /// <summary>Consecutive failed steps that end the stream.</summary>
    internal const int MaxFailures = 5;

    /// <summary>Taken messages are acknowledged once the oldest has waited this long.</summary>
    internal static readonly TimeSpan FlushAge = TimeSpan.FromSeconds(10);

    /// <summary>The first back-off after a failed step; it doubles up to <see cref="MaxBackoff"/>.</summary>
    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The longest back-off.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IMediatorExchange _exchange;
    private readonly PickupLog _log;
    private readonly MediatorRoute _route;
    private readonly LiveRegistration _registration;
    private readonly string _mediatorDid;
    private readonly string _localDid;
    private readonly TimeProvider _time;

    private readonly Queue<Opened> _ready = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly List<(string Id, long At)> _taken = new();

    private PickupItem? _yielded;
    private UInt128 _yieldedKey;
    private long _nextCycleAt;
    private long _notBefore;
    private bool _draining;
    private int _drainRounds;
    private int _failures;
    private bool _liveRequested;
    private bool _warnedNotLive;

    private LiveDeliveryStream(IMediatorExchange exchange, PickupLog log, MediatorRoute route, LiveRegistration registration, string mediatorDid, string localDid)
    {
        _exchange = exchange;
        _log = log;
        _route = route;
        _registration = registration;
        _mediatorDid = mediatorDid;
        _localDid = localDid;
        _time = exchange.Time;
        _nextCycleAt = _time.GetTimestamp();
        _notBefore = _nextCycleAt;
    }

    /// <summary>How many taken messages wait for an acknowledgement (test seam).</summary>
    internal int TakenCount => _taken.Count;

    /// <summary>How many ids the stream holds (test seam).</summary>
    internal int HeldCount => _held.Count;

    /// <summary>
    /// The enumeration (the arguments were checked by the caller). Nothing happens until the first
    /// <c>MoveNextAsync</c>; every enumeration of the result is its own stream.
    /// </summary>
    /// <param name="exchange">The client's exchange.</param>
    /// <param name="log">The client's log lines.</param>
    /// <param name="mediatorDid">The mediator's DID (bare).</param>
    /// <param name="localDid">Our DID (bare).</param>
    /// <param name="serviceEndpointOverride">The caller's endpoint, or <c>null</c>.</param>
    /// <param name="ct">The caller's cancellation; combined with the one given to <c>GetAsyncEnumerator</c>.</param>
    internal static IAsyncEnumerable<PickupItem> ReceiveAsync(IMediatorExchange exchange, PickupLog log, string mediatorDid, string localDid, Uri? serviceEndpointOverride, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(log);
        return RunAsync(exchange, log, mediatorDid, localDid, serviceEndpointOverride, ct);
    }

    private static async IAsyncEnumerable<PickupItem> RunAsync(
        IMediatorExchange exchange, PickupLog log, string mediatorDid, string localDid, Uri? serviceEndpointOverride,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Refused before anything is sent: a route that is not a WebSocket, or a second stream for
        // the pair. Neither has turned live mode on, so neither turns it off.
        var route = await exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: true, ct).ConfigureAwait(false);
        var registration = exchange.RegisterLiveStream(mediatorDid, localDid);
        var stream = new LiveDeliveryStream(exchange, log, route, registration, mediatorDid, localDid);
        log.LiveStarted();
        try
        {
            while (true)
            {
                var item = await stream.NextAsync(ct).ConfigureAwait(false);
                if (item is null)
                    yield break;
                yield return item;

                // Reached only when the consumer asks for the next item (R-2). A consumer whose
                // token is cancelled by then is stopping: the item it was handed is not taken.
                ct.ThrowIfCancellationRequested();
                stream.Take(item);
            }
        }
        finally
        {
            await stream.EndAsync().ConfigureAwait(false);
        }
    }

    // ---- the loop ---------------------------------------------------------------------------

    /// <summary>The next item to yield, or <c>null</c> when the mailbox was closed.</summary>
    private async Task<PickupItem?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (FlushDue())
            {
                // One messages-received of at most MaxAckIds ids; looked at again, so the rest of a
                // longer backlog follows in further messages.
                await FlushAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (Now >= _nextCycleAt)
            {
                await CycleAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (_ready.TryDequeue(out var opened))
            {
                if (Yield(opened) is { } item)
                    return item;
                continue;
            }
            if (_registration.Pushes.TryRead(out var push))
            {
                await LoadPushAsync(push, ct).ConfigureAwait(false);
                continue;
            }
            if (_draining && Now >= _notBefore)
            {
                await DrainRoundAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (!await WaitAsync(ct).ConfigureAwait(false))
                return null;
        }
    }

    // Taken messages wait for an acknowledgement until 10 are waiting, nothing is ready (neither a
    // buffered message nor a push), or the oldest has waited 10 s; never during a back-off.
    private bool FlushDue()
    {
        if (_taken.Count == 0 || Now < _notBefore)
            return false;
        return _taken.Count >= FlushAt
            || (_ready.Count == 0 && !_registration.Pushes.TryPeek(out _))
            || _time.GetElapsedTime(_taken[0].At) >= FlushAge;
    }

    // Wait for a push, the next cycle, or the caller's cancellation. One awaited operation, so no
    // task is left behind for nobody to observe: two tasks raced with WhenAny leave the loser
    // running, and a mailbox the channel's disposal ends with an error after the race was decided
    // would fault it unobserved. The read is woken by a timer of our own instead (OwnDeadline: its
    // callback cannot carry an exception onto a timer thread), linked to the caller's token.
    // true: something to do (a push, or the cycle is due); false: the mailbox was closed without an
    // error, and the stream ends; an error it was closed with (the channel's disposal) is thrown.
    private async Task<bool> WaitAsync(CancellationToken ct)
    {
        var now = Now;
        if (now >= _nextCycleAt)
            return true;
        using var wake = OwnDeadline.Start(_time, _time.GetElapsedTime(now, _nextCycleAt), ct);
        // Read again once the timer exists: a clock that passed the cycle while the timer was being
        // set (a manual clock moved in between) is seen here, not waited out for a whole interval.
        if (Now >= _nextCycleAt)
            return true;
        try
        {
            return await _registration.Pushes.WaitToReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return true; // the timer: the cycle is due
        }
    }

    // ---- the cycle --------------------------------------------------------------------------

    private async Task CycleAsync(CancellationToken ct)
    {
        _nextCycleAt = Later(_exchange.Options.LivePollInterval);
        _liveRequested = true;
        var spec = DeliveryReader.LiveDeliveryChangeSpec;
        var request = MessagePickup.CreateLiveDeliveryChange(_localDid, _mediatorDid, liveDelivery: true, UuidV4MessageIdGenerator.Instance);
        InboundMessageSnapshot answer;
        try
        {
            answer = await _exchange.ExchangeAsync(_route, request, spec, _mediatorDid, _localDid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Retryable(ex, ct))
        {
            Failed(request.Id, spec, ex);
            return;
        }
        var status = DeliveryReader.ReadStatus(DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
        if (status.LiveDelivery == false && !_warnedNotLive)
        {
            _warnedNotLive = true;
            _log.LiveNotInEffect(request.Id);
        }
        _draining = true;
        _drainRounds = 0;
    }

    private async Task DrainRoundAsync(CancellationToken ct)
    {
        var spec = DeliveryReader.DeliveryRequestSpec;
        var request = MessagePickup.CreateDeliveryRequest(_localDid, _mediatorDid, DrainLimit, recipientDid: null, UuidV4MessageIdGenerator.Instance);
        InboundMessageSnapshot answer;
        try
        {
            answer = await _exchange.ExchangeAsync(_route, request, spec, _mediatorDid, _localDid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Retryable(ex, ct))
        {
            Failed(request.Id, spec, ex);
            return;
        }

        var message = DeliveryReader.Open(answer, request.Id, spec);
        if (DeliveryReader.IsStatus(message))
        {
            DeliveryReader.ReadStatus(message, request.Id, spec, checkFilter: false, filter: null);
            DrainDone();
            return;
        }
        if (!DeliveryReader.IsDelivery(message))
            throw DeliveryReader.Unexpected(request.Id, spec);
        var contents = DeliveryReader.ReadEntries(message.Attachments, MaxPerDelivery);
        if (contents.AttachmentCount == 0)
        {
            DrainDone();
            return;
        }
        // Nothing in it can be acknowledged, so the next round would get it again: not retried.
        if (contents.Entries.Count == 0)
            throw DeliveryReader.NoUsableIds(request.Id, spec);
        await LoadAsync(contents, request.Id, ct).ConfigureAwait(false);
        if (++_drainRounds >= MaxDrainRounds)
            DrainDone();
    }

    // A cycle ran to its end: the failure count starts again.
    private void DrainDone()
    {
        _draining = false;
        _failures = 0;
    }

    // A step failed: back off (the next cycle re-arms live mode and drains again), or end the
    // stream with this exception after MaxFailures in a row.
    private void Failed(string requestId, MediatorExchangeSpec spec, Exception error)
    {
        _failures++;
        _draining = false;
        if (_failures >= MaxFailures)
            ExceptionDispatchInfo.Capture(error).Throw();
        var backoff = Backoff(_failures);
        _notBefore = Later(backoff);
        _nextCycleAt = _notBefore;
        _log.CycleFailed(requestId, spec.Name, error, _failures, backoff);
    }

    /// <summary>1 s, doubling per failure in a row, at most 30 s.</summary>
    /// <param name="failures">The failures in a row so far (1 or more).</param>
    internal static TimeSpan Backoff(int failures)
    {
        var shift = Math.Clamp(failures - 1, 0, 30);
        var delay = TimeSpan.FromTicks(FirstBackoff.Ticks << Math.Min(shift, 5));
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    /// <summary>
    /// Whether a failed step is retried after a back-off: only a failure a retry can fix (Q2-2).
    /// Everything else ends the stream at once, as it ends a fetch: a problem report or a malformed
    /// answer, a pack-stage refusal such as <see cref="SecretNotFoundException"/>, the container's
    /// disposal, the caller's cancellation.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="TimeoutException"/>: no answer in time; the next attempt may get one.</item>
    /// <item><see cref="TransportException"/>: the connection failed; the next attempt reconnects.</item>
    /// <item>
    /// The channel's refusal because <see cref="MediatorClientOptions.MaxPendingRequests"/> of the
    /// container's requests are already waiting (<see cref="MediatorBusyException"/>, an
    /// <see cref="InvalidOperationException"/> to callers): transient, the others end within
    /// <see cref="MediatorClientOptions.RequestTimeout"/>. Every other
    /// <see cref="InvalidOperationException"/> — the send path's configuration refusals (no key curve
    /// in common with the mediator, a mediator reached through routing, no transport router) and
    /// <see cref="ObjectDisposedException"/> (the container is gone) — is thrown at once.
    /// </item>
    /// <item>
    /// <see cref="OperationCanceledException"/> the caller did not cause: a transport giving up on
    /// a wait of its own, a timeout by another name. The caller's cancellation is never retried.
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="error">The step's exception.</param>
    /// <param name="ct">The caller's token.</param>
    private bool Retryable(Exception error, CancellationToken ct)
        => error switch
        {
            TimeoutException or TransportException => true,
            ObjectDisposedException => false,
            MediatorBusyException => true,
            OperationCanceledException => !ct.IsCancellationRequested,
            _ => false,
        };

    // ---- items ------------------------------------------------------------------------------

    private async Task LoadPushAsync(InboundMessageSnapshot push, CancellationToken ct)
    {
        Message message;
        try
        {
            message = push.DeserializeMessage();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _log.PushIgnored("unreadable");
            return;
        }
        if (!DeliveryReader.IsDelivery(message))
        {
            _log.PushIgnored("not a delivery");
            return;
        }
        await LoadAsync(DeliveryReader.ReadEntries(message.Attachments, MaxPerDelivery), requestId: null, ct).ConfigureAwait(false);
    }

    // Take one delivery: skip the ids this stream already holds, open the rest up front under one
    // budget (the consumer's time with an item never counts against it), and queue the results in
    // delivery order. Failures are logged once per delivery, with the drain's request id.
    private async Task LoadAsync(DeliveryContents contents, string? requestId, CancellationToken ct)
    {
        if (contents.Excess > 0)
            _log.BeyondLimit(requestId, contents.Excess);
        if (contents.Unusable > 0)
            _log.UnusableIds(requestId, contents.Unusable);
        if (contents.RepeatedIds > 0)
            _log.RepeatedIds(requestId, contents.RepeatedIds);
        var taken = new List<DeliveryEntry>(contents.Entries.Count);
        var skipped = 0;
        foreach (var entry in contents.Entries)
        {
            if (_held.Add(entry.Id))
                taken.Add(entry);
            else
                skipped++;
        }
        if (skipped > 0)
            _log.HeldSkipped(skipped);
        if (taken.Count == 0)
            return;

        var payloads = new List<ReadOnlyMemory<byte>>(taken.Count);
        foreach (var entry in taken)
        {
            if (entry.Payload is { } payload)
                payloads.Add(payload);
        }
        var outcomes = payloads.Count == 0
            ? Array.Empty<DeliveredOutcome>()
            : await _exchange.UnpackDeliveredBatchAsync(payloads, requestId, ct).ConfigureAwait(false);
        if (outcomes.Count != payloads.Count)
            throw new InvalidOperationException("The unpacker returned another number of outcomes than it was given messages.");

        var failures = new List<Exception>();
        var next = 0;
        foreach (var entry in taken)
        {
            var outcome = entry.Payload is null ? null : outcomes[next++];
            if (outcome is null || outcome.Unpacked is null)
                failures.Add(outcome?.Error ?? entry.Error!);
            _ready.Enqueue(new Opened(entry, outcome));
        }
        _log.UnpackFailures(requestId, failures);
    }

    // The next opened message: a failure is yielded and let go (it comes back and is reported
    // again); a repeat of a message already returned from this mailbox is taken (acknowledged) and
    // not yielded. The duplicate filter is asked now, not when the delivery was opened, so a copy
    // that follows a message taken from the same delivery is recognised.
    private PickupItem? Yield(Opened opened)
    {
        var entry = opened.Entry;
        if (opened.Outcome is not { } outcome || outcome.Unpacked is not { } unpacked)
        {
            _held.Remove(entry.Id);
            return new PickupItem(entry.Id, unpacked: null, opened.Outcome?.Error ?? entry.Error);
        }
        if (_exchange.Duplicates.Contains(_localDid, outcome.DedupeKey))
        {
            AddTaken(entry.Id);
            return null;
        }
        var item = new PickupItem(entry.Id, unpacked, error: null);
        _yielded = item;
        _yieldedKey = outcome.DedupeKey;
        return item;
    }

    /// <summary>A message of a delivery the stream took, opened (or not decodable: <see cref="Outcome"/> <c>null</c>, the entry's error).</summary>
    private readonly record struct Opened(DeliveryEntry Entry, DeliveredOutcome? Outcome);

    /// <summary>The consumer asked for the item after <paramref name="item"/>: a message it was handed is taken.</summary>
    private void Take(PickupItem item)
    {
        if (ReferenceEquals(item, _yielded))
        {
            _exchange.Duplicates.Add(_localDid, _yieldedKey);
            AddTaken(item.Id);
        }
        _yielded = null;
    }

    private void AddTaken(string id)
    {
        if (_taken.Count >= MaxTaken)
        {
            // Only while acknowledgements keep failing: the oldest is forgotten unacknowledged; it
            // comes back and the duplicate filter recognises it.
            _held.Remove(_taken[0].Id);
            _taken.RemoveAt(0);
            _log.TakenDropped(1);
        }
        _taken.Add((id, Now));
    }

    // ---- acknowledgement and end --------------------------------------------------------------

    private async Task FlushAsync(CancellationToken ct)
    {
        var spec = DeliveryReader.MessagesReceivedSpec;
        var ids = TakenBatch();
        var request = MessagePickup.CreateMessagesReceived(_localDid, _mediatorDid, ids, UuidV4MessageIdGenerator.Instance);
        try
        {
            await _exchange.ExchangeAsync(_route, request, spec, _mediatorDid, _localDid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Retryable(ex, ct))
        {
            // Kept for the next attempt, after the back-off.
            _log.AckFailed(request.Id, ids.Length, ex);
            Failed(request.Id, spec, ex);
            return;
        }
        Acknowledged(ids.Length);
    }

    /// <summary>
    /// The end of the enumeration, whatever ended it: acknowledge what was taken and turn live mode
    /// off, within one <see cref="MediatorClientOptions.RequestTimeout"/> independent of the caller's
    /// token, then close the mailbox. Never throws: the enumeration's own outcome is what the consumer
    /// sees.
    /// </summary>
    private async Task EndAsync()
    {
        try
        {
            using var budget = OwnDeadline.Start(_time, _exchange.Options.RequestTimeout);
            while (_taken.Count > 0 && !budget.Expired)
            {
                var ids = TakenBatch();
                var request = MessagePickup.CreateMessagesReceived(_localDid, _mediatorDid, ids, UuidV4MessageIdGenerator.Instance);
                try
                {
                    await _exchange.ExchangeAsync(_route, request, DeliveryReader.MessagesReceivedSpec, _mediatorDid, _localDid, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.AckFailed(request.Id, ids.Length, ex);
                    break;
                }
                Acknowledged(ids.Length);
            }

            if (_liveRequested)
            {
                var off = MessagePickup.CreateLiveDeliveryChange(_localDid, _mediatorDid, liveDelivery: false, UuidV4MessageIdGenerator.Instance);
                try
                {
                    await _exchange.ExchangeAsync(_route, off, DeliveryReader.LiveDeliveryChangeSpec, _mediatorDid, _localDid, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.EndFailed(DeliveryReader.LiveDeliveryChangeSpec.Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            // The deadline's timer could not be created (a host TimeProvider): nothing more to do.
            _log.EndFailed("end", ex);
        }
        finally
        {
            _log.LiveEnded(_taken.Count);
            _registration.Dispose();
        }
    }

    // The oldest taken ids, at most MaxAckIds: one messages-received.
    private string[] TakenBatch()
    {
        var count = Math.Min(_taken.Count, MaxAckIds);
        var ids = new string[count];
        for (var i = 0; i < count; i++)
            ids[i] = _taken[i].Id;
        return ids;
    }

    private void Acknowledged(int count)
    {
        for (var i = 0; i < count; i++)
            _held.Remove(_taken[i].Id);
        _taken.RemoveRange(0, count);
    }

    // ---- time -------------------------------------------------------------------------------

    private long Now => _time.GetTimestamp();

    private long Later(TimeSpan by)
        => Now + (long)(by.Ticks * ((double)_time.TimestampFrequency / TimeSpan.TicksPerSecond));
}
