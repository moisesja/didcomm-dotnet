using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using DidComm.Exceptions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// One <see cref="PickupClient.ReceiveLiveAsync"/> enumeration (#96 design §4.8 as amended by
/// orchestrator note R-2; its scheduling simplified in #111). Owner: engineer C.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Start.</strong> At the first <c>MoveNextAsync</c>: the route, refused before anything is
/// sent when it is not a WebSocket; then the live-push mailbox of the (mediator, local DID) pair,
/// refused when one is already open; then the Message Pickup version (#100 DC-6), once for the
/// stream's life: a version query that fails ends the stream at once, not retried, and closes the
/// mailbox. Every request the stream sends and every answer it reads goes through
/// <see cref="PickupWire"/> with that version; a push of the other version is ignored, not
/// acknowledged.
/// </para>
/// <para>
/// <strong>One loop, one rule.</strong> The stream is in one of three phases — idle, draining or
/// backing off — and has three alarms (<see cref="Alarm"/>): the next cycle, the flush age and the
/// give-up point. Each pass of its loop asks one function what to do next (in this order: flush
/// what was taken, start a cycle, yield an opened message, run a drain round when no push is owed
/// its turn, take a waiting push, run a drain round) and does it; when the answer is "nothing", it
/// waits, and the wait returns without awaiting exactly when that same function has something to
/// do. Every action changes the state that made it due, so the loop never spins.
/// </para>
/// <para>
/// <strong>Cycle.</strong> <c>live-delivery-change{true}</c> → <c>status</c>, then a drain
/// (<c>delivery-request{10}</c> rounds; see "The drain's end"). Live mode is turned on before the
/// drain, so a message that arrives between the two is pushed (it is after the mediator's arrival
/// mark) or drained, never neither. The cycle runs at the start, one
/// <see cref="MediatorClientOptions.LivePollInterval"/> after the previous cycle's drain ended (which
/// also picks up delayed messages, never pushed when they fall due, and recovers a dropped
/// connection), and after a failed step once its back-off has passed (1 s, doubling to 30 s). A
/// cycle that has started is never pre-empted: it runs until its drain ends or one of its steps
/// fails, and the alarm of the next cycle is armed only then, never while a drain runs (R1-1). The
/// mediator has no scheduler for due messages, now or later (owner decision D2 on #88,
/// 2026-11-14), so this polling is the permanent design, not a stopgap. Only a failure a retry can
/// fix is retried (<see cref="Retryable"/>); any other failure (a
/// <see cref="MediationProtocolException"/> such as live mode not supported, a pack-stage error such
/// as <see cref="SecretNotFoundException"/>) ends the stream at once. While a back-off lasts nothing
/// is sent, acknowledgements included.
/// </para>
/// <para>
/// <strong>The run of failures (ADV5-2, ADV5-3, I-18).</strong> A failed step adds to two things:
/// the failure count, which sets the back-off (1 s, doubling to 30 s), and the give-up alarm, armed
/// at the first failure since it was last let go for <see cref="MediatorClientOptions.RequestTimeout"/>
/// plus <see cref="GiveUpMargin"/> (60 s by default). A retryable failure after that alarm has fired
/// ends the stream with its exception; earlier ones back off. A request that succeeds and makes
/// progress resets both: a drain that ends, a drain round that brings something new (a message this
/// stream neither holds nor has already handled in the current drain), or an acknowledgement the
/// mediator accepted (under 4.0: sent). So failures separated by such progress never add up to a
/// give-up or a long back-off, however many there are. A push that brings something new lets the
/// give-up alarm go but keeps the back-off (owner decision B1 on #111): it shows the session is
/// alive, not that requests work. So a stream whose requests all fail never gives up while such
/// pushes keep coming, and its requests stay at one per back-off, at most one per 30 s once the
/// back-off has reached it, however often pushes come (R-11).
/// </para>
/// <para>
/// <strong>The drain's end (ADV5-4, QA2-3).</strong> A drain ends at a <c>status</c> (under 4.0 an
/// empty <c>delivery</c>), at a failed step, after <see cref="MaxDrainRounds"/> rounds, or at a round
/// that brings nothing new: every message in it is one the stream holds, or one it has already
/// handled in this drain — yielded as a failure, or taken (acknowledged, or recognised as a repeat
/// and acknowledged). A mediator that does not set delivered messages aside answers every round with
/// the head of its queue, so a message there that never opens is yielded once per cycle, and one it
/// keeps after its acknowledgement is opened once per cycle, each at a cost of two delivery requests
/// per cycle, instead of filling every round of every drain.
/// </para>
/// <para>
/// <strong>Drain rounds and pushes take turns (ADV3-1).</strong> Once a cycle is draining, its next
/// drain round runs before the stream takes another push, and a push waiting in the mailbox gets its
/// turn after that round. Opening a delivery can take its whole budget (a sender whose DID resolves
/// slowly or never), so a stream that served a waiting push before the drain let a steady flow of
/// slow pushes keep the drain from ever running: what only a drain reaches (messages queued before
/// the stream started, delayed ones, pushes the full mailbox dropped) never arrived. Taking turns,
/// neither a flow of slow pushes nor a drain that keeps meeting slow messages keeps the other from
/// its turn. A push that waits stays leased to the session at the mediator: nothing is lost.
/// </para>
/// <para>
/// <strong>Clock (ADV3-5, ADV4-4).</strong> The stream never reads the clock to decide anything.
/// The next cycle, the end of a back-off, the 10-second flush age and the give-up point are each an
/// alarm the stream arms when the state that starts them begins (a drain ends, a step fails, the
/// first message is taken, a run of failures starts), and each is due when its timer has fired. So
/// a host clock that steps back or forward (an NTP correction, a restored snapshot), or whose
/// readings keep going lower, costs nothing: no early cycle, retry or flush, no storm, and no
/// postponement. Each arming is a new alarm and disposes the one it replaces; a replaced alarm's
/// late callback sets only its own flag and changes no decision. The wait is one awaited operation —
/// the mailbox's <c>WaitToReadAsync</c> on a wake source of its own — and the hand-over with an
/// alarm cannot miss a firing: the wait publishes its fresh wake source and only then looks again at
/// what is due; an alarm sets its flag and only then cancels the wake source it finds published.
/// The mailbox runs no continuation inline, so no stream code runs on a timer thread.
/// </para>
/// <para>
/// <strong>Items.</strong> Pushes are read from the mailbox (at most 10 messages per delivery). Each
/// delivery the stream takes — a drain round's answer or a push — is opened up front, all of its
/// messages under one budget (<see cref="MediatorClientOptions.UnpackTimeout"/>, owner decision 2-A),
/// on the consumer's own <c>MoveNextAsync</c> and never on a receive loop; then its results are
/// yielded in delivery order. The consumer's time with an item is therefore never counted against
/// the budget, and a message the budget runs out on is a failure item (a
/// <see cref="TimeoutException"/> that names the drain round's request id; one from a push names no
/// request, since a push answers none). A delivery that brings nothing new is not opened at all.
/// Otherwise a message this stream already holds (opened and not yet yielded, or taken and not yet
/// acknowledged) is skipped and not opened again; one the duplicate filter remembers is
/// acknowledged and not yielded; a failure is yielded, never acknowledged, and reported again when
/// it comes back (in a later drain, or outside one). A drain answered with a delivery in which no
/// message has a usable id ends the stream (nothing in it could be acknowledged, so the drain would
/// ask again for ever); such a push is ignored.
/// </para>
/// <para>
/// <strong>Acknowledgement (R-2).</strong> A yielded message is taken — added to the duplicate filter
/// and queued for acknowledgement — when the consumer asks for the next item; taken messages are
/// acknowledged when 10 are waiting, when nothing is ready, or once 10 s have passed since the first
/// of them was taken (then until none wait), in <c>messages-received</c> messages of at most
/// <see cref="MaxAckIds"/> ids each (several when more are waiting, as after an acknowledgement
/// outage); under 4.0 each is sent one way (<see cref="IMediatorExchange.SendOneWayAsync"/>: nothing
/// answers it, so only the send is waited for). When the enumeration ends for any reason the last
/// yielded message is not taken; what was taken is acknowledged, live mode is turned off, both
/// within one <see cref="MediatorClientOptions.RequestTimeout"/> whatever the caller's token, and the
/// mailbox is closed. Buffered or drained messages never yielded are never acknowledged.
/// </para>
/// <para>
/// <strong>Bounds.</strong> At most 10 opened messages ready (one delivery), the mailbox's 8 pushes,
/// and at most 100 taken waiting for an acknowledgement (10 unless acknowledgements fail); the set of
/// held ids is bounded by their sum. The ids handled in the current drain (yielded as failures, or
/// taken) are remembered as 128-bit keys of their attachment ids (compared exactly): added only
/// while a drain runs and cleared when the next cycle starts, so at most (50 rounds + 50 pushes) × 10
/// — each comes from a delivery handled in the drain, or from the one delivery still being yielded
/// when it started, and a drain has at most 50 rounds and takes a push only between two of them. At most one timer per alarm, three in all,
/// each disposed when it is replaced or let go, and when the stream ends. Not thread-safe: one
/// consumer, as <c>IAsyncEnumerator</c> requires.
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

    /// <summary>
    /// How much longer than <see cref="MediatorClientOptions.RequestTimeout"/> a run of failures may
    /// last before a retryable failure ends the stream (ADV5-2): the give-up alarm is armed at the
    /// run's first failure for <c>RequestTimeout</c> plus this (60 s by default), so a transient
    /// condition that lasts one <c>RequestTimeout</c> — the busy refusal, whose cause ends within one;
    /// a mediator restarting — clears before the stream gives up, and the back-off really reaches
    /// <see cref="MaxBackoff"/> on the way.
    /// </summary>
    internal static readonly TimeSpan GiveUpMargin = TimeSpan.FromSeconds(30);

    /// <summary>Taken messages are acknowledged once this long has passed since the first of them was taken.</summary>
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
    private readonly MessagePickupVersion _version;
    private readonly Action _poke;
    private readonly Action? _onWakePublished;

    private readonly Queue<Opened> _ready = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly List<string> _taken = new();
    private readonly HashSet<UInt128> _doneThisDrain = new();

    private PickupItem? _yielded;
    private UInt128 _yieldedKey;
    private Phase _phase = Phase.Idle;
    private Alarm _cycle = Alarm.AlreadyFired; // the first cycle runs at the first MoveNextAsync
    private Alarm? _flushAge;
    private Alarm? _giveUp;
    private CancellationTokenSource? _wake;
    private bool _pushTurn;
    private int _drainRounds;
    private int _failures;
    private bool _liveRequested;
    private bool _warnedNotLive;

    private LiveDeliveryStream(IMediatorExchange exchange, PickupLog log, MediatorRoute route, LiveRegistration registration, string mediatorDid, string localDid, MessagePickupVersion version, Action? onWakePublished)
    {
        _exchange = exchange;
        _version = version;
        _log = log;
        _route = route;
        _registration = registration;
        _mediatorDid = mediatorDid;
        _localDid = localDid;
        _time = exchange.Time;
        _poke = Poke;
        _onWakePublished = onWakePublished;
    }

    /// <summary>Where the stream is in its cycle.</summary>
    private enum Phase
    {
        /// <summary>Between cycles: the next one is due when the cycle alarm fires.</summary>
        Idle,

        /// <summary>A cycle turned live mode on and its drain runs; no cycle can come due.</summary>
        Draining,

        /// <summary>A step failed: nothing is sent until the cycle alarm (the back-off) fires.</summary>
        BackingOff,
    }

    /// <summary>What the loop does next (<see cref="NextAction"/>).</summary>
    private enum Step
    {
        /// <summary>Nothing: wait for a push, an alarm or the caller's cancellation.</summary>
        None,

        /// <summary>Send one <c>messages-received</c> of the oldest taken ids.</summary>
        Flush,

        /// <summary>Turn live mode on and start a drain.</summary>
        Cycle,

        /// <summary>Hand the next opened message to the consumer.</summary>
        Yield,

        /// <summary>Send one <c>delivery-request</c> of the drain.</summary>
        DrainRound,

        /// <summary>Take the next push from the mailbox.</summary>
        Push,
    }

    /// <summary>How many taken messages wait for an acknowledgement (test seam).</summary>
    internal int TakenCount => _taken.Count;

    /// <summary>How many ids the stream holds (test seam).</summary>
    internal int HeldCount => _held.Count;

    /// <summary>How many ids the current drain has handled: yielded as failures or taken (test seam).</summary>
    internal int DoneThisDrainCount => _doneThisDrain.Count;

    /// <summary>
    /// The enumeration (the arguments were checked by the caller). Nothing happens until the first
    /// <c>MoveNextAsync</c>; every enumeration of the result is its own stream.
    /// </summary>
    /// <param name="exchange">The client's exchange.</param>
    /// <param name="log">The client's log lines.</param>
    /// <param name="mediatorDid">The mediator's DID (bare).</param>
    /// <param name="localDid">Our DID (bare).</param>
    /// <param name="serviceEndpointOverride">The caller's endpoint, or <c>null</c>.</param>
    /// <param name="resolveVersion">The client's version resolution (route, mediator DID, local DID, token): pinned, remembered or negotiated.</param>
    /// <param name="ct">The caller's cancellation; combined with the one given to <c>GetAsyncEnumerator</c>.</param>
    /// <param name="onWakePublished">
    /// Test seam, <c>null</c> in production: runs on the stream's thread each time its wait has
    /// published a fresh wake source and before it looks again at what is due — the window in which
    /// an alarm that fires must still wake the wait.
    /// </param>
    internal static IAsyncEnumerable<PickupItem> ReceiveAsync(
        IMediatorExchange exchange, PickupLog log, string mediatorDid, string localDid, Uri? serviceEndpointOverride,
        Func<MediatorRoute, string, string, CancellationToken, Task<MessagePickupVersion>> resolveVersion, CancellationToken ct,
        Action? onWakePublished = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(resolveVersion);
        return RunAsync(exchange, log, mediatorDid, localDid, serviceEndpointOverride, resolveVersion, onWakePublished, ct);
    }

    private static async IAsyncEnumerable<PickupItem> RunAsync(
        IMediatorExchange exchange, PickupLog log, string mediatorDid, string localDid, Uri? serviceEndpointOverride,
        Func<MediatorRoute, string, string, CancellationToken, Task<MessagePickupVersion>> resolveVersion, Action? onWakePublished,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Refused before anything is sent: a route that is not a WebSocket, or a second stream for
        // the pair. Neither has turned live mode on, so neither turns it off.
        var route = await exchange.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket: true, ct).ConfigureAwait(false);
        var registration = exchange.RegisterLiveStream(mediatorDid, localDid);
        // The version, once for the stream's life (#100 DC-6). A query that fails ends the stream
        // here, not retried; nothing has turned live mode on, and the mailbox is closed, so another
        // stream for the pair can open.
        MessagePickupVersion version;
        try
        {
            version = await resolveVersion(route, mediatorDid, localDid, ct).ConfigureAwait(false);
        }
        catch
        {
            registration.Dispose();
            throw;
        }
        var stream = new LiveDeliveryStream(exchange, log, route, registration, mediatorDid, localDid, version, onWakePublished);
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
            switch (NextAction())
            {
                case Step.Flush:
                    // One messages-received of at most MaxAckIds ids; looked at again, so the rest of
                    // a longer backlog follows in further messages.
                    await FlushAsync(ct).ConfigureAwait(false);
                    break;
                case Step.Cycle:
                    await CycleAsync(ct).ConfigureAwait(false);
                    break;
                case Step.Yield:
                    if (Yield(_ready.Dequeue()) is { } item)
                        return item;
                    break;
                case Step.DrainRound:
                    await DrainRoundAsync(ct).ConfigureAwait(false);
                    break;
                case Step.Push:
                    if (_registration.Pushes.TryRead(out var push))
                    {
                        _pushTurn = false;
                        await LoadPushAsync(push, ct).ConfigureAwait(false);
                    }
                    break;
                default:
                    if (!await WaitAsync(ct).ConfigureAwait(false))
                        return null;
                    break;
            }
        }
    }

    /// <summary>
    /// What the loop does next: the one rule the loop dispatches on and the wait returns on (I-16).
    /// Each step changes the state that made it due — a flush shrinks the taken list or starts a
    /// back-off, a cycle starts a drain or a back-off, a yield dequeues, a drain round loads, ends the
    /// drain or starts a back-off, a push is read off the mailbox — so the loop cannot spin.
    /// </summary>
    private Step NextAction()
    {
        // Nothing is sent while a back-off lasts, acknowledgements included.
        var backingOff = _phase == Phase.BackingOff && !_cycle.Fired;
        var pushWaiting = _registration.Pushes.TryPeek(out _);
        // Taken messages wait for an acknowledgement until 10 are waiting, nothing is ready (neither
        // an opened message nor a push), or the flush age has passed since the first was taken.
        if (!backingOff && _taken.Count > 0
            && (_taken.Count >= FlushAt || (_ready.Count == 0 && !pushWaiting) || _flushAge is { Fired: true }))
            return Step.Flush;
        // A cycle that has started runs its drain to its end before the next one is due (R1-1):
        // while draining, no cycle is armed at all.
        if (_phase != Phase.Draining && _cycle.Fired)
            return Step.Cycle;
        if (_ready.Count > 0)
            return Step.Yield;
        // While a cycle drains, its rounds and the pushes take turns, a drain round first (ADV3-1):
        // a waiting push is taken only after a drain round, so a steady flow of pushes that each take
        // their whole budget to open cannot keep the drain from running, and a drain that keeps
        // meeting slow messages cannot keep the pushes from their turn.
        if (_phase == Phase.Draining && !_pushTurn)
            return Step.DrainRound;
        if (pushWaiting)
            return Step.Push;
        if (_phase == Phase.Draining)
            return Step.DrainRound;
        return Step.None;
    }

    // Wait for a push, an alarm or the caller's cancellation. One awaited operation, so no task is
    // left behind for nobody to observe: two tasks raced with WhenAny leave the loser running, and a
    // mailbox the channel's disposal ends with an error after the race was decided would fault it
    // unobserved. The read is woken by a wake source of the stream's own, which an alarm cancels
    // when it fires (Poke) and the caller's token cancels through a registration.
    // true: something to do (a push, or an alarm fired); false: the mailbox was closed without an
    // error, and the stream ends; an error it was closed with (the channel's disposal) is thrown.
    private async Task<bool> WaitAsync(CancellationToken ct)
    {
        // The wake hand-over (a Dekker pair; both writes are full fences): this side publishes its
        // wake source, then looks at the alarms; an alarm sets its flag, then cancels the wake source
        // it finds. Whatever the interleaving, either the look below sees the flag or the alarm
        // cancels this wake source, so a firing is never slept through. The source is not disposed:
        // a late poke may still cancel it, and a plain source holds nothing that needs disposing.
        var wake = new CancellationTokenSource();
        Interlocked.Exchange(ref _wake, wake);
        _onWakePublished?.Invoke();
        if (NextAction() != Step.None)
            return true;
        using var caller = ct.UnsafeRegister(static state => GuardedDeadline.CancelQuietly((CancellationTokenSource)state!), wake);
        try
        {
            return await _registration.Pushes.WaitToReadAsync(wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wake.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            return true; // an alarm fired
        }
    }

    // An alarm fired (on its timer's thread, after it set its flag): wake the wait, if one is
    // published. Never throws and never waits; a source that is not being waited on any more is
    // cancelled for nothing.
    private void Poke()
    {
        if (Volatile.Read(ref _wake) is { } wake)
            GuardedDeadline.CancelQuietly(wake);
    }

    // Arm the cycle alarm: the next cycle (one interval after a drain ended) or the end of a back-off.
    // The alarm it replaces is disposed; its late callback, if any, sets only its own flag.
    private void ArmCycle(TimeSpan due)
    {
        var replaced = _cycle;
        _cycle = Alarm.Start(_time, due, _poke);
        replaced.Dispose();
    }

    // ---- the cycle --------------------------------------------------------------------------

    // The next cycle is not armed here, before the answer (R1-1: an answer slower than the interval
    // would find it due on return, and the stream would send live-on for ever): it is armed when
    // this cycle's drain ends (DrainDone), or for a back-off when a step fails (Failed).
    private async Task CycleAsync(CancellationToken ct)
    {
        _liveRequested = true;
        var (request, spec) = PickupWire.LiveDeliveryChange(_version, _localDid, _mediatorDid, liveDelivery: true);
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
        var status = PickupWire.ReadStatus(_version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, checkFilter: false, filter: null);
        if (status.LiveDelivery == false && !_warnedNotLive)
        {
            _warnedNotLive = true;
            _log.LiveNotInEffect(request.Id);
        }
        _phase = Phase.Draining;
        _doneThisDrain.Clear();
        _pushTurn = false; // the cycle's first drain round goes before any waiting push
        _drainRounds = 0;
    }

    private async Task DrainRoundAsync(CancellationToken ct)
    {
        // Whatever this round's outcome, a waiting push has the next turn (ADV3-1).
        _pushTurn = true;
        var (request, spec) = PickupWire.DeliveryRequest(_version, _localDid, _mediatorDid, DrainLimit, recipientDid: null);
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

        // A 3.0 status (nothing deliverable) ends the drain; so does an empty delivery, the 4.0 way
        // of saying it (DrainLimit is MaxPerDelivery: what is read is what was asked for). A 4.0
        // delivery beyond the limit or without attachments is malformed and ends the stream.
        var read = PickupWire.ReadDeliveryAnswer(_version, DeliveryReader.Open(answer, request.Id, spec), request.Id, spec, DrainLimit, checkFilter: false, filter: null);
        if (read.Status is not null)
        {
            DrainDone();
            return;
        }
        var contents = read.Contents!;
        if (contents.AttachmentCount == 0)
        {
            DrainDone();
            return;
        }
        // Nothing in it can be acknowledged, so the next round would get it again: not retried.
        if (contents.Entries.Count == 0)
            throw DeliveryReader.NoUsableIds(request.Id, spec);
        // A round that brings nothing new ends the drain (ADV5-4): a mediator that does not set
        // delivered messages aside would answer the next round with the same messages again.
        if (!await LoadAsync(contents, request.Id, ct).ConfigureAwait(false))
        {
            DrainDone();
            return;
        }
        // A request that succeeded and brought something new: the run of failures is over.
        LetGiveUpGo();
        RestartBackoff();
        if (++_drainRounds >= MaxDrainRounds)
            DrainDone();
    }

    // A cycle ran to its end: the run of failures is over, and the next cycle is due one
    // LivePollInterval from now — measured from the end of this one, never from its start (R1-1).
    // With Failed, the only place the cycle alarm is armed (I-17).
    private void DrainDone()
    {
        _phase = Phase.Idle;
        LetGiveUpGo();
        RestartBackoff();
        ArmCycle(_exchange.Options.LivePollInterval);
    }

    // A step failed: back off (the next cycle re-arms live mode and drains again), or end the stream
    // with this exception once the run's give-up alarm has fired (ADV5-2: a count of five gave up
    // after 1+2+4+8 = 15 s, before a busy refusal, whose cause ends within one RequestTimeout, could
    // clear). The first failure since the give-up alarm was let go arms it again (LetGiveUpGo).
    private void Failed(string requestId, MediatorExchangeSpec spec, Exception error)
    {
        if (_giveUp is { Fired: true })
            ExceptionDispatchInfo.Capture(error).Throw();
        _failures++;
        _phase = Phase.BackingOff;
        var backoff = Backoff(_failures);
        ArmCycle(backoff);
        _giveUp ??= Alarm.Start(_time, _exchange.Options.RequestTimeout + GiveUpMargin, _poke);
        _log.CycleFailed(requestId, spec.Name, error, _failures, backoff);
    }

    // Progress of any kind (a request that succeeded with progress, or a push with something new):
    // the give-up alarm is let go, and the next failure arms a new one (I-18, ADV5-3).
    private void LetGiveUpGo()
    {
        _giveUp?.Dispose();
        _giveUp = null;
    }

    // A request that succeeded with progress (a drain that ended, a drain round with something new,
    // an accepted acknowledgement), never a push (owner decision B1): the failure count starts again,
    // so the next failure backs off 1 s.
    private void RestartBackoff() => _failures = 0;

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
        if (!PickupWire.IsDelivery(_version, message))
        {
            // Only a delivery of the stream's own version is taken; a push of the other version is
            // the mediator speaking another protocol on this session, and is let go unacknowledged.
            if (PickupWire.IsDeliveryOfAnotherVersion(_version, message))
                _log.PushOfOtherVersion();
            else
                _log.PushIgnored("not a delivery");
            return;
        }
        // A push is not a drain round: one that brings nothing new opens nothing, and ends nothing.
        // One that brings something new shows the session alive, not that requests work: it lets the
        // give-up alarm go and keeps the back-off (owner decision B1).
        if (await LoadAsync(DeliveryReader.ReadEntries(message.Attachments, MaxPerDelivery), requestId: null, ct).ConfigureAwait(false))
            LetGiveUpGo();
    }

    // Take one delivery. When it brings nothing new — every message in it is held by this stream or
    // was handled in this drain (yielded as a failure, or taken) — nothing is opened and the answer
    // is false; the caller decides what that ends. Otherwise skip the ids this stream already holds,
    // open the rest up front under one budget (the consumer's time with an item never counts against
    // it), queue the results in delivery order, and answer true (progress, as the caller counts it). Failures are logged once per delivery, with the drain
    // round's request id; a push's (requestId null) name none, in the log and in their
    // TimeoutException alike.
    private async Task<bool> LoadAsync(DeliveryContents contents, string? requestId, CancellationToken ct)
    {
        if (contents.Excess > 0)
            _log.BeyondLimit(requestId, contents.Excess);
        if (contents.Unusable > 0)
            _log.UnusableIds(requestId, contents.Unusable);
        if (contents.RepeatedIds > 0)
            _log.RepeatedIds(requestId, contents.RepeatedIds);
        var fresh = 0;
        var held = 0;
        foreach (var entry in contents.Entries)
        {
            // Every entry is looked at (no early exit); attachment ids compare exactly.
            var isHeld = _held.Contains(entry.Id);
            var isDone = _doneThisDrain.Contains(DrainKey(entry.Id));
            held += isHeld ? 1 : 0;
            fresh += isHeld | isDone ? 0 : 1;
        }
        if (held > 0)
            _log.HeldSkipped(held);
        if (fresh == 0)
            return false;

        var taken = new List<DeliveryEntry>(contents.Entries.Count);
        foreach (var entry in contents.Entries)
        {
            if (_held.Add(entry.Id))
                taken.Add(entry);
        }

        var inputs = new List<DeliveredInput>(taken.Count);
        foreach (var entry in taken)
        {
            if (entry.Payload is { } payload)
                inputs.Add(new DeliveredInput(entry.Id, payload));
        }
        var outcomes = inputs.Count == 0
            ? Array.Empty<DeliveredOutcome>()
            : await _exchange.UnpackDeliveredBatchAsync(new DeliveryScope(_mediatorDid, _localDid), inputs, requestId, ct).ConfigureAwait(false);
        if (outcomes.Count != inputs.Count)
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
        return true;
    }

    // The next opened message: a failure is yielded and let go (it comes back and is reported
    // again; while a drain runs, it is remembered for the rest of that drain, so a round that brings
    // only it again ends the drain); a repeat of a message already returned from this mailbox is
    // taken (acknowledged) and not yielded (AddTaken remembers it for the drain too). The duplicate filter is asked now, not when the delivery
    // was opened, so a copy that follows a message taken from the same delivery is recognised.
    private PickupItem? Yield(Opened opened)
    {
        var entry = opened.Entry;
        if (opened.Outcome is not { } outcome || outcome.Unpacked is not { } unpacked)
        {
            _held.Remove(entry.Id);
            // Only while a drain runs: what a drain remembers is bounded by its rounds and its pushes.
            if (_phase == Phase.Draining)
                _doneThisDrain.Add(DrainKey(entry.Id));
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

    // The key a handled id is remembered by for the rest of a drain: SHA-256 truncated to 128 bits
    // over the attachment id's exact UTF-8 bytes. Attachment ids are opaque and compare exactly
    // (Message Pickup 3.0 L170, 4.0 L257): no case folding, which could merge two distinct ids.
    private static UInt128 DrainKey(string attachmentId)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(attachmentId), digest);
        return BinaryPrimitives.ReadUInt128BigEndian(digest[..16]);
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

    // Queue a taken id for acknowledgement; the first one taken arms the flush age. While a drain
    // runs, the id is remembered for the rest of it (QA2-3): a mediator that keeps acknowledged
    // messages and delivers them again makes a round that brings nothing new, which ends the drain.
    private void AddTaken(string id)
    {
        if (_phase == Phase.Draining)
            _doneThisDrain.Add(DrainKey(id));
        if (_taken.Count >= MaxTaken)
        {
            // Only while acknowledgements keep failing: the oldest is forgotten unacknowledged; it
            // comes back and the duplicate filter recognises it.
            _held.Remove(_taken[0]);
            _taken.RemoveAt(0);
            _log.TakenDropped(1);
        }
        _taken.Add(id);
        _flushAge ??= Alarm.Start(_time, FlushAge, _poke);
    }

    // ---- acknowledgement and end --------------------------------------------------------------

    private async Task FlushAsync(CancellationToken ct)
    {
        var spec = DeliveryReader.MessagesReceivedSpec;
        var ids = TakenBatch();
        var request = PickupWire.MessagesReceived(_version, _localDid, _mediatorDid, ids);
        try
        {
            await AcknowledgeAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Retryable(ex, ct))
        {
            // Kept for the next attempt, after the back-off.
            _log.AckFailed(request.Id, ids.Length, ex);
            Failed(request.Id, spec, ex);
            return;
        }
        Acknowledged(ids.Length);
        // A request that succeeded and made progress: the run of failures is over.
        LetGiveUpGo();
        RestartBackoff();
    }

    /// <summary>
    /// The end of the enumeration, whatever ended it: acknowledge what was taken and turn live mode
    /// off, within one <see cref="MediatorClientOptions.RequestTimeout"/> independent of the caller's
    /// token, then close the mailbox and let the alarms go. Never throws: the enumeration's own
    /// outcome is what the consumer sees.
    /// </summary>
    private async Task EndAsync()
    {
        try
        {
            using var budget = OwnDeadline.Start(_time, _exchange.Options.RequestTimeout);
            while (_taken.Count > 0 && !budget.Expired)
            {
                var ids = TakenBatch();
                var request = PickupWire.MessagesReceived(_version, _localDid, _mediatorDid, ids);
                try
                {
                    await AcknowledgeAsync(request, budget.Token).ConfigureAwait(false);
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
                var (off, offSpec) = PickupWire.LiveDeliveryChange(_version, _localDid, _mediatorDid, liveDelivery: false);
                try
                {
                    await _exchange.ExchangeAsync(_route, off, offSpec, _mediatorDid, _localDid, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.EndFailed(offSpec.Name, ex);
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
            _cycle.Dispose();
            _flushAge?.Dispose();
            _giveUp?.Dispose();
        }
    }

    // One messages-received: under 3.0 an exchange (the mediator SHOULD answer with its status,
    // which is not read); under 4.0 sent one way, since nothing answers it (L62, L294).
    private Task AcknowledgeAsync(Message request, CancellationToken ct)
        => PickupWire.AnswersAcknowledgement(_version)
            ? _exchange.ExchangeAsync(_route, request, DeliveryReader.MessagesReceivedSpec, _mediatorDid, _localDid, ct)
            : _exchange.SendOneWayAsync(_route, request, PickupWire.MessagesReceivedName, _mediatorDid, _localDid, ct);

    // The oldest taken ids, at most MaxAckIds: one messages-received.
    private string[] TakenBatch()
    {
        var count = Math.Min(_taken.Count, MaxAckIds);
        var ids = new string[count];
        for (var i = 0; i < count; i++)
            ids[i] = _taken[i];
        return ids;
    }

    // The oldest `count` taken ids were acknowledged; once none wait, the flush age is let go (the
    // next one taken arms it again).
    private void Acknowledged(int count)
    {
        for (var i = 0; i < count; i++)
            _held.Remove(_taken[i]);
        _taken.RemoveRange(0, count);
        if (_taken.Count == 0)
        {
            _flushAge?.Dispose();
            _flushAge = null;
        }
    }
}
