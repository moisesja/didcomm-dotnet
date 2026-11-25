using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-2 (#111): a manual clock whose timers fire only when it is advanced, and whose readings can be
/// stepped back and forth or made to go lower on every read, independently of its timers. It can
/// advance to its next due timer, so a test drives simulated time from one event to the next.
/// </summary>
internal sealed class SimClock : TimeProvider
{
    private static readonly long Start = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero).UtcTicks;
    private readonly object _gate = new();
    private readonly List<SimTimer> _timers = new();
    private long _now;
    private long _offset;
    private long _reads;
    private long _created;

    /// <summary>Whether every reading is lower than the one before (timers are not affected).</summary>
    public bool ReadingsKeepFalling { get; set; }

    /// <summary>Simulated time on the timers since the start.</summary>
    public TimeSpan Now
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromTicks(_now);
        }
    }

    /// <summary>Timers created and not disposed.</summary>
    public int LiveTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count;
        }
    }

    /// <summary>Timers created so far.</summary>
    public long Created => Interlocked.Read(ref _created);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return Start + _now + _offset - (ReadingsKeepFalling ? ++_reads : 0);
    }

    public override DateTimeOffset GetUtcNow() => new(GetTimestamp(), TimeSpan.Zero);

    /// <summary>Move the readings (not the timers) by <paramref name="by"/>; negative steps back.</summary>
    public void Step(TimeSpan by)
    {
        lock (_gate)
            _offset += by.Ticks;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new SimTimer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        Interlocked.Increment(ref _created);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Advance the timers by <paramref name="by"/>, firing every timer that comes due, in due order, on this thread.</summary>
    public int Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
            target = _now + by.Ticks;
        var fired = 0;
        while (true)
        {
            SimTimer? next;
            lock (_gate)
            {
                next = null;
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is { } due && due <= target && (next is null || due < next.DueAt))
                        next = timer;
                }
                if (next is null)
                {
                    if (target > _now)
                        _now = target;
                    return fired;
                }
                if (next.DueAt!.Value > _now)
                    _now = next.DueAt.Value;
                next.DueAt = next.Period is { } period ? next.DueAt + period : null;
            }
            next.Fire();
            fired++;
        }
    }

    /// <summary>Advance to the next due timer and fire every timer due then; false when no timer is scheduled.</summary>
    public bool AdvanceToNextDue()
    {
        long? next = null;
        long now;
        lock (_gate)
        {
            now = _now;
            foreach (var timer in _timers)
            {
                if (timer.DueAt is { } due && (next is null || due < next))
                    next = due;
            }
        }
        if (next is null)
            return false;
        Advance(TimeSpan.FromTicks(Math.Max(0, next.Value - now)));
        return true;
    }

    private sealed class SimTimer(SimClock owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        public long? DueAt { get; set; }

        public long? Period { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                    return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + Math.Max(0, dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// QA-2 (#111): an <see cref="IMediatorExchange"/> over a <see cref="FakePickupExchange"/> (requests,
/// answers, live mailboxes) with its own clock (<see cref="SimClock"/>) and its own real
/// <see cref="DeliveredMessageUnpacker"/> on that clock, slow memory included. Each batch is opened
/// in simulated time: once every message the workers hold is waiting on the clock, the clock moves
/// to its next timer (the budget, an alarm), until the batch ends.
/// </summary>
/// <remarks>
/// Payload texts: <c>ok:{id}</c> opens, <c>bad:{text}</c> fails at once, <c>stall:{text}</c> waits on
/// its token until the budget ends it (a sender whose DID never resolves).
/// </remarks>
internal sealed class SimExchange : IMediatorExchange
{
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private readonly List<(TimeSpan At, string Type)> _log = new();
    private readonly List<(string? RequestId, string[] Ids, TimeSpan At)> _loads = new();
    private readonly List<string> _opened = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, CancellationToken> _stalls = new();
    private int _hostCalls;
    private long _stallIds;
    private volatile bool _disposed;

    public SimExchange(Action<MediatorClientOptions>? configure = null, CancellationToken shutdown = default)
    {
        Inner = new FakePickupExchange(configure) { Route = FakePickupExchange.WebSocketRoute };
        Clock = new SimClock();
        Unpacker = new DeliveredMessageUnpacker(HostAsync, Inner.Options.UnpackTimeout, Clock, shutdown);
    }

    public FakePickupExchange Inner { get; }

    public SimClock Clock { get; }

    public DeliveredMessageUnpacker Unpacker { get; }

    /// <summary>The scope every stream of these tests opens with.</summary>
    public static DeliveryScope Scope => new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>Runs before each batch is opened (to keep a push waiting, for instance).</summary>
    public Action? BeforeBatch { get; set; }

    /// <summary>Runs when a batch is settled on the clock, before the clock moves; true when it acted instead (the clock then does not move).</summary>
    public Func<bool>? OnSettled { get; set; }

    /// <summary>How many host unpack calls were made.</summary>
    public int HostCalls => Volatile.Read(ref _hostCalls);

    /// <summary>The payload texts handed to the host, in order.</summary>
    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_gate)
                return _opened.ToArray();
        }
    }

    /// <summary>Every request sent: when, on the timers, and its kind.</summary>
    public IReadOnlyList<(TimeSpan At, string Type)> Log
    {
        get
        {
            lock (_gate)
                return _log.ToArray();
        }
    }

    /// <summary>Every delivery opened: its request id (<c>null</c> for a push), its attachment ids, and when.</summary>
    public IReadOnlyList<(string? RequestId, string[] Ids, TimeSpan At)> Loads
    {
        get
        {
            lock (_gate)
                return _loads.ToArray();
        }
    }

    /// <summary>The container is gone: a batch the shutdown ended reports the disposal, as the channel does.</summary>
    public void MarkDisposed() => _disposed = true;

    public MediatorClientOptions Options => Inner.Options;

    public TimeProvider Time => Clock;

    public DuplicateFilter Duplicates => Inner.Duplicates;

    public NegotiatedVersionCache<CoordinateMediationVersion> Versions => Inner.Versions;

    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => Inner.PickupVersions;

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        => Inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
    {
        lock (_gate)
            _log.Add((Clock.Now, Kind(request)));
        return Inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
    }

    public Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct)
    {
        lock (_gate)
            _log.Add((Clock.Now, Kind(request)));
        return Inner.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);
    }

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => Inner.RegisterLiveStream(mediatorDid, localDid);

    public async Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct)
    {
        BeforeBatch?.Invoke();
        lock (_gate)
            _loads.Add((requestId, messages.Select(m => m.Id ?? "").ToArray(), Clock.Now));
        if (!SpinWait.SpinUntil(() => Waiting == 0, RealTime))
            throw new TimeoutException("the previous batch's host calls did not end");
        var n = Unpacker.Memory.HeldBack(scope, messages.Select(m => m.Id).ToArray()).Count(held => !held);
        var entered = HostCalls;
        var settled = Unpacker.Settled;
        var batch = Unpacker.UnpackAllAsync(scope, messages, requestId, ct);
        while (!batch.IsCompleted)
        {
            if (!SpinWait.SpinUntil(() => batch.IsCompleted || Settled(n, entered, settled), RealTime))
                throw new TimeoutException("the batch neither ended nor settled on the clock");
            if (batch.IsCompleted)
                break;
            if (OnSettled?.Invoke() == true)
            {
                SpinWait.SpinUntil(() => batch.IsCompleted, RealTime);
                break;
            }
            if (!Clock.AdvanceToNextDue())
                throw new InvalidOperationException("a batch waits on the clock with no timer scheduled");
        }
        try
        {
            return await batch.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            throw new ObjectDisposedException("the container");
        }
    }

    // Host calls waiting on the clock: stalls whose token has not been cancelled. One whose budget
    // just ended stops counting at once, before its outcome has been recorded.
    private int Waiting => _stalls.Values.Count(token => !token.IsCancellationRequested);

    // Every message the workers took has entered its host call, every worker that can take one holds
    // one, and every call whose outcome is not recorded yet waits on the clock — at least one does:
    // a batch whose every message has its outcome completes without the clock.
    private bool Settled(int n, int entered, long settled)
    {
        var e = HostCalls - entered;
        var s = (int)(Unpacker.Settled - settled);
        var waiting = Waiting;
        return waiting > 0 && e == Math.Min(n, s + DeliveredMessageUnpacker.FetchConcurrency) && e - s == waiting;
    }

    private async Task<DeliveredUnpack> HostAsync(string packed, CancellationToken ct)
    {
        var text = Unwrap(packed);
        lock (_gate)
            _opened.Add(text);
        if (text.StartsWith("stall:", StringComparison.Ordinal))
        {
            var id = Interlocked.Increment(ref _stallIds);
            _stalls[id] = ct;
            Interlocked.Increment(ref _hostCalls);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            finally
            {
                _stalls.TryRemove(id, out _);
            }
        }
        else
        {
            Interlocked.Increment(ref _hostCalls);
        }
        var outcome = FakePickupExchange.DefaultOutcome(text);
        return outcome.Unpacked is { } result ? new DeliveredUnpack(result, text, outcome.LayersOpened) : throw outcome.Error!;
    }

    private static string Unwrap(string packed)
    {
        try
        {
            if (JsonNode.Parse(packed) is JsonObject envelope && envelope["ciphertext"] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }
        catch (JsonException)
        {
        }
        return packed;
    }

    /// <summary>The kind of a request: live-on, live-off, drain or ack.</summary>
    public static string Kind(Message request) => request.Type switch
    {
        MessagePickup.LiveDeliveryChangeType => request.Body![PickupBodies.LiveDelivery]!.GetValue<bool>() ? PickupClientLiveStreamTests.LiveOn : PickupClientLiveStreamTests.LiveOff,
        MessagePickup.DeliveryRequestType => PickupClientLiveStreamTests.Drain,
        MessagePickup.MessagesReceivedType => PickupClientLiveStreamTests.Ack,
        _ => request.Type,
    };
}

/// <summary>
/// QA-2 (#111): a Message Pickup 3.0 mediator queue behind a <see cref="SimExchange"/>. A delivery
/// request gets the first <c>limit</c> messages not leased now, each then leased for
/// <see cref="Lease"/> (zero: the mediator does not set delivered messages aside), or a status when
/// none is free; an acknowledgement removes what it names. It can mint a new id for the same bytes
/// at every delivery.
/// </summary>
internal sealed class LeasingMediator
{
    private readonly object _gate = new();
    private readonly List<Entry> _queue = new();
    private readonly SimExchange _exchange;
    private int _minted;

    public LeasingMediator(SimExchange exchange, TimeSpan lease)
    {
        _exchange = exchange;
        Lease = lease;
        exchange.Inner.On(MessagePickup.DeliveryRequestType, Deliver);
        exchange.Inner.On(MessagePickup.MessagesReceivedType, Received);
    }

    public TimeSpan Lease { get; }

    /// <summary>Give every message a new id each time it is delivered.</summary>
    public bool MintNewIds { get; set; }

    /// <summary>Fail delivery requests and acknowledgements with this, when set.</summary>
    public Func<Exception?>? Failure { get; set; }

    public void Enqueue(string id, string text)
    {
        lock (_gate)
            _queue.Add(new Entry { Id = id, Text = text, LeasedUntil = TimeSpan.MinValue });
    }

    public IReadOnlyList<string> Queued
    {
        get
        {
            lock (_gate)
                return _queue.Select(e => e.Id).ToArray();
        }
    }

    private InboundMessageSnapshot Deliver(SentRequest request)
    {
        if (Failure?.Invoke() is { } error)
            throw error;
        var now = _exchange.Clock.Now;
        (string Id, string Payload)[] picked;
        int left;
        lock (_gate)
        {
            var free = _queue.Where(e => e.LeasedUntil <= now).Take(request.Limit).ToList();
            foreach (var entry in free)
            {
                entry.LeasedUntil = now + Lease;
                if (MintNewIds)
                    entry.Id = $"minted-{++_minted}";
            }
            picked = free.Select(e => (e.Id, e.Text)).ToArray();
            left = _queue.Count;
        }
        return picked.Length == 0 ? FakePickupExchange.Status(request, count: left) : FakePickupExchange.Delivery(request, picked);
    }

    private InboundMessageSnapshot Received(SentRequest request)
    {
        if (Failure?.Invoke() is { } error)
            throw error;
        var ids = request.Ids;
        int left;
        lock (_gate)
        {
            _queue.RemoveAll(e => ids.Contains(e.Id, StringComparer.Ordinal));
            left = _queue.Count;
        }
        return FakePickupExchange.Status(request, count: left);
    }

    private sealed class Entry
    {
        public required string Id { get; set; }

        public required string Text { get; init; }

        public TimeSpan LeasedUntil { get; set; }
    }
}

/// <summary>
/// QA-2 (#111): drives one live stream in simulated time. Each time the stream waits (its wait's
/// seam), the test may act (push a message); otherwise the clock moves to its next timer. Items are
/// collected as the stream yields them, and the consumer asks for the next one at once.
/// </summary>
internal sealed class StreamDriver : IAsyncDisposable
{
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private readonly SimExchange _exchange;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly IAsyncEnumerator<PickupItem> _stream;
    private int _waits;
    private TaskCompletionSource _waited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? _move;
    private int _mark;
    private bool _parked;

    public StreamDriver(SimExchange exchange)
    {
        _exchange = exchange;
        _stream = LiveDeliveryStream.ReceiveAsync(exchange, new PickupLog(logger: null), FakePickupExchange.M, FakePickupExchange.L, serviceEndpointOverride: null,
            static (_, _, _, _) => Task.FromResult(MessagePickupVersion.V3), _cts.Token, OnWaitPublished).GetAsyncEnumerator(_cts.Token);
    }

    /// <summary>The items yielded so far, with the simulated time of each.</summary>
    public List<(PickupItem Item, TimeSpan At)> Items { get; } = new();

    /// <summary>The exception the stream ended with, if it ended with one.</summary>
    public Exception? EndedWith { get; private set; }

    /// <summary>When the stream ended (simulated time), or <c>null</c>.</summary>
    public TimeSpan? EndedAt { get; private set; }

    /// <summary>How many waits the stream has reached.</summary>
    public int Waits
    {
        get
        {
            lock (_gate)
                return _waits;
        }
    }

    /// <summary>The token the stream was given (the caller's).</summary>
    public CancellationTokenSource Caller => _cts;

    /// <summary>Runs on the stream's thread at each wait, before it looks again at what is due.</summary>
    public Action? AtWait { get; set; }

    /// <summary>
    /// Run until <paramref name="stop"/> holds, the stream ends, the clock reaches
    /// <paramref name="until"/>, or the stream waits with no timer scheduled. At each wait,
    /// <paramref name="onWait"/> may act (true: the clock is not moved). A stream left waiting is
    /// acted on first by the next call.
    /// </summary>
    public async Task RunAsync(Func<bool> stop, TimeSpan until, Func<bool>? onWait = null)
    {
        if (_move is null)
        {
            _mark = Waits;
            _move = _stream.MoveNextAsync().AsTask();
        }
        while (!stop() && EndedAt is null)
        {
            if (!_parked)
            {
                var first = await Task.WhenAny(_move, WaitBeyond(_mark)).WaitAsync(RealTime).ConfigureAwait(false);
                if (first == _move)
                {
                    bool moved;
                    try
                    {
                        moved = await _move.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        EndedWith = ex;
                        EndedAt = _exchange.Clock.Now;
                        return;
                    }
                    if (!moved)
                    {
                        EndedAt = _exchange.Clock.Now;
                        return;
                    }
                    Items.Add((_stream.Current, _exchange.Clock.Now));
                    _mark = Waits;
                    _move = _stream.MoveNextAsync().AsTask();
                    continue;
                }
                _parked = true;
                continue; // the stream waits: look at stop() before acting
            }
            _mark = Waits;
            if (onWait?.Invoke() == true)
            {
                _parked = false;
                continue;
            }
            if (_exchange.Clock.Now >= until || !_exchange.Clock.AdvanceToNextDue())
                return; // left waiting
            _parked = false;
        }
    }

    /// <summary>The stream object behind the enumeration (for its test seams).</summary>
    public LiveDeliveryStream Stream
    {
        get
        {
            var member = _stream.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(f => f.FieldType == typeof(LiveDeliveryStream));
            return (LiveDeliveryStream)member.GetValue(_stream)!;
        }
    }

    /// <summary>How many ids the stream remembers for the current drain: yielded as failures, or taken (its test seam).</summary>
    public int Reported => Stream.DoneThisDrainCount;

    /// <summary>Stop the stream: cancel, observe the pending move, dispose (which acknowledges and turns live mode off).</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_move is not null && EndedAt is null)
        {
            try
            {
                await _move.WaitAsync(RealTime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (EndedWith is not null)
            {
            }
        }
        await _stream.DisposeAsync().AsTask().WaitAsync(RealTime).ConfigureAwait(false);
    }

    private void OnWaitPublished()
    {
        AtWait?.Invoke();
        TaskCompletionSource signal;
        lock (_gate)
        {
            _waits++;
            signal = _waited;
            _waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        signal.TrySetResult();
    }

    private async Task WaitBeyond(int count)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_waits > count)
                    return;
                changed = _waited.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }
}
