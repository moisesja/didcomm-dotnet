using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The QA-1 clock (#111): the readings and timers of a <see cref="ManualTimeProvider"/>, with every
/// timer tracked so a driver can move the clock exactly to the next one that is due
/// (<see cref="NextDueIn"/>). Its readings can be moved without firing a timer (<see cref="Step"/>),
/// made to fall by one second at each reading (<see cref="Falling"/>), or made to throw
/// (<see cref="Throws"/>); its timers always fire, on <see cref="Inner"/>'s moves only.
/// </summary>
internal sealed class Qa1Clock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Tracked> _timers = new();
    private long _offset;
    private long _falls;
    private long _fallFrom;
    private volatile bool _throws;
    private volatile bool _falling;

    /// <summary>The manual clock whose moves fire the timers.</summary>
    public ManualTimeProvider Inner { get; } = new();

    /// <summary>Whether every reading throws (the timers still fire).</summary>
    public bool Throws
    {
        get => _throws;
        set => _throws = value;
    }

    /// <summary>Whether every reading is one second lower than the one before it, from the reading when it was set, whatever the moves.</summary>
    public bool Falling
    {
        get => _falling;
        set
        {
            Interlocked.Exchange(ref _fallFrom, Inner.GetTimestamp() + Interlocked.Read(ref _offset));
            _falling = value;
        }
    }

    /// <summary>The time on <see cref="Inner"/>, which no step or fall changes: what the tests measure.</summary>
    public DateTimeOffset TrueNow => Inner.GetUtcNow();

    /// <summary>How many readings threw.</summary>
    public int Thrown;

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override long GetTimestamp()
    {
        if (_throws)
        {
            Interlocked.Increment(ref Thrown);
            throw new InvalidOperationException("the host's clock failed");
        }
        if (_falling)
            return Interlocked.Read(ref _fallFrom) - Interlocked.Increment(ref _falls) * TimeSpan.TicksPerSecond;
        return Inner.GetTimestamp() + Interlocked.Read(ref _offset);
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_throws)
        {
            Interlocked.Increment(ref Thrown);
            throw new InvalidOperationException("the host's clock failed");
        }
        return Inner.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offset));
    }

    /// <summary>Move every later reading by <paramref name="by"/> (forward or back) without firing a timer.</summary>
    public void Step(TimeSpan by) => Interlocked.Add(ref _offset, by.Ticks);

    /// <summary>How long until the next tracked timer is due, or <c>null</c> when none is scheduled.</summary>
    public TimeSpan? NextDueIn()
    {
        DateTimeOffset? next = null;
        lock (_gate)
        {
            foreach (var timer in _timers)
            {
                if (timer.DueAt is { } due && (next is null || due < next))
                    next = due;
            }
        }
        if (next is not { } at)
            return null;
        var left = at - Inner.GetUtcNow();
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>How many tracked timers are scheduled now.</summary>
    public int Scheduled
    {
        get
        {
            lock (_gate)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var tracked = new Tracked(this, callback, state);
        tracked.Timer = Inner.CreateTimer(static s => ((Tracked)s!).Fire(), tracked, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate)
            _timers.Add(tracked);
        tracked.Change(dueTime, period);
        return tracked;
    }

    private sealed class Tracked(Qa1Clock owner, TimerCallback callback, object? state) : ITimer
    {
        public ITimer Timer = null!;

        public DateTimeOffset? DueAt;

        private TimeSpan? _period;

        public void Fire()
        {
            lock (owner._gate)
                DueAt = _period is { } period && DueAt is { } due ? due + period : null;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Inner.GetUtcNow() + dueTime;
                _period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }
            return Timer.Change(dueTime, period);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
            Timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>What a stand-in host unpack does with one message (QA-1, #111).</summary>
internal enum Qa1Kind
{
    /// <summary>Opens, after <see cref="Qa1Step.After"/> on the clock (at once when zero).</summary>
    Opens,

    /// <summary>Fails (a decryption failure), after <see cref="Qa1Step.After"/> on the clock (at once when zero).</summary>
    Fails,

    /// <summary>Never ends on its own; ends when its token is cancelled.</summary>
    Stalls,

    /// <summary>Never ends on its own and ignores its token; ends when the test releases it.</summary>
    IgnoresToken,
}

/// <summary>One stand-in host unpack's behaviour.</summary>
/// <param name="Kind">What it does.</param>
/// <param name="After">How long it takes on the clock, for <see cref="Qa1Kind.Opens"/> and <see cref="Qa1Kind.Fails"/>.</param>
internal readonly record struct Qa1Step(Qa1Kind Kind, TimeSpan After = default)
{
    public static Qa1Step Stall => new(Qa1Kind.Stalls);

    public static Qa1Step Open => new(Qa1Kind.Opens);

    public static Qa1Step OpenAfter(TimeSpan after) => new(Qa1Kind.Opens, after);

    public static Qa1Step FailAfter(TimeSpan after) => new(Qa1Kind.Fails, after);
}

/// <summary>
/// The host's unpack step for the QA-1 tests: what each message does is chosen from its label
/// (<see cref="TestJwe.LabelOf"/>); every call is recorded; and the calls waiting on the clock (or on
/// nothing) with their token still live are counted (<see cref="Waiting"/>), which is how a driver
/// knows that every worker of a delivery is parked.
/// </summary>
internal sealed class Qa1Host
{
    private readonly object _gate = new();
    private readonly List<(Task Wait, CancellationToken Token)> _waits = new();
    private readonly Qa1Clock _clock;
    private readonly Func<string, Qa1Step> _step;
    private readonly Func<string, string> _recipientOf;
    private TaskCompletionSource<DeliveredUnpack> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _neverEnding;

    public Qa1Host(Qa1Clock clock, Func<string, Qa1Step> step, Func<string, string>? recipientOf = null)
    {
        _clock = clock;
        _step = step;
        _recipientOf = recipientOf ?? (_ => FakePickupExchange.L);
    }

    /// <summary>Every host call, with its label and token.</summary>
    public HostCallLog Calls { get; } = new();

    /// <summary>How many calls wait on the clock (or on nothing) with their token not cancelled.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                _waits.RemoveAll(w => w.Wait.IsCompleted);
                return _waits.Count(w => !w.Token.IsCancellationRequested);
            }
        }
    }

    /// <summary>
    /// How many calls are inside a lookup that never ends on its own and ignores its token
    /// (<see cref="Qa1Kind.IgnoresToken"/>): each keeps its reservation until the test releases it
    /// (<see cref="ReleaseIgnored"/>), whatever its delivery does.
    /// </summary>
    public int NeverEnding => Volatile.Read(ref _neverEnding);

    /// <summary>
    /// When set and it returns a task for a call's label, that call, if it stalls
    /// (<see cref="Qa1Kind.Stalls"/>), ends only once the task has completed after its token was
    /// cancelled: a lookup that honours its token and takes a moment more to end. Until then it
    /// holds its reservation.
    /// </summary>
    public Func<string, Task?>? EndGate { get; set; }

    /// <summary>End every call that ignores its token.</summary>
    public void ReleaseIgnored()
    {
        TaskCompletionSource<DeliveredUnpack> never;
        lock (_gate)
        {
            never = _never;
            _never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        never.TrySetException(new CryptoException("JWE could not be decrypted."));
    }

    /// <summary>The unpack step.</summary>
    public async Task<DeliveredUnpack> UnpackAsync(string packed, CancellationToken ct)
    {
        var label = TestJwe.LabelOf(packed);
        var step = _step(label);
        using (Calls.Enter(label, ct))
        {
            switch (step.Kind)
            {
                case Qa1Kind.Opens:
                    if (step.After > TimeSpan.Zero)
                        await Wait(Task.Delay(step.After, _clock, ct), ct);
                    return new DeliveredUnpack(FakePickupExchange.Authcrypt(FakePickupExchange.A, _recipientOf(label), label), packed, 0);
                case Qa1Kind.Fails:
                    if (step.After > TimeSpan.Zero)
                        await Wait(Task.Delay(step.After, _clock, ct), ct);
                    throw new CryptoException("JWE could not be decrypted.");
                case Qa1Kind.Stalls:
                    try
                    {
                        await Wait(Task.Delay(Timeout.InfiniteTimeSpan, ct), ct);
                    }
                    catch (OperationCanceledException) when (EndGate?.Invoke(label) is { } held)
                    {
                        await held.ConfigureAwait(false);
                        throw;
                    }
                    throw new InvalidOperationException("unreachable");
                default:
                    Task<DeliveredUnpack> never;
                    lock (_gate)
                        never = _never.Task;
                    Interlocked.Increment(ref _neverEnding);
                    try
                    {
                        await Wait(never, ct);
                        return await never;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _neverEnding);
                    }
            }
        }
    }

    private Task Wait(Task wait, CancellationToken ct)
    {
        lock (_gate)
            _waits.Add((wait, ct));
        return wait;
    }
}

/// <summary>One message queued at a <see cref="Qa1Mediator"/>.</summary>
internal sealed class Qa1Queued(string id, string packed, long sequence, string mailbox, string recipient)
{
    public string Id { get; } = id;

    public string Packed { get; } = packed;

    public long Sequence { get; } = sequence;

    /// <summary>The DID whose mailbox holds it (the <c>from</c> of a request).</summary>
    public string Mailbox { get; } = mailbox;

    /// <summary>The DID a <c>recipient_did</c> filter selects it by.</summary>
    public string Recipient { get; } = recipient;

    public DateTimeOffset LeasedUntil { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>
/// A Message Pickup mediator for the QA-1 tests (3.0 and 4.0): one queue, oldest first; a
/// <c>delivery-request</c> takes up to its limit of the messages not leased (all of them when it
/// does not lease) and leases them for <see cref="Lease"/> on the clock's true time; a
/// <c>messages-received</c> deletes its ids. A 3.0 request with nothing to deliver is answered with
/// a status, a 4.0 one with an empty delivery. A <c>recipient_did</c> filter takes only that DID's
/// messages.
/// </summary>
internal sealed class Qa1Mediator
{
    private readonly object _gate = new();
    private readonly List<Qa1Queued> _queue = new();
    private readonly List<IReadOnlyList<string>> _deliveries = new();
    private readonly Qa1Clock _clock;
    private long _sequence;

    public Qa1Mediator(Qa1Clock clock, TimeSpan? lease, string? did = null)
    {
        _clock = clock;
        Lease = lease;
        Did = did ?? FakePickupExchange.M;
    }

    /// <summary>The mediator's DID: the requests addressed to it are its own.</summary>
    public string Did { get; }

    /// <summary>How long a delivered message is set aside; <c>null</c>: never.</summary>
    public TimeSpan? Lease { get; }

    /// <summary>The ids of every delivery, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Deliveries
    {
        get
        {
            lock (_gate)
                return _deliveries.ToArray();
        }
    }

    /// <summary>How many messages are queued.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    /// <summary>Whether a message with <paramref name="id"/> is queued.</summary>
    public bool Holds(string id)
    {
        lock (_gate)
            return _queue.Any(q => q.Id == id);
    }

    /// <summary>Queue a message (its packed text) in <paramref name="mailbox"/>'s mailbox (Bob's by default), for <paramref name="recipient"/> (the mailbox's DID by default).</summary>
    public void Enqueue(string id, string packed, string? mailbox = null, string? recipient = null)
    {
        mailbox ??= FakePickupExchange.L;
        lock (_gate)
            _queue.Add(new Qa1Queued(id, packed, ++_sequence, mailbox, recipient ?? mailbox));
    }

    /// <summary>The earliest instant (true time) a leased message is deliverable again, if any is leased now.</summary>
    public DateTimeOffset? NextLeaseEnd()
    {
        var now = _clock.TrueNow;
        lock (_gate)
        {
            DateTimeOffset? next = null;
            foreach (var q in _queue)
            {
                if (q.LeasedUntil > now && (next is null || q.LeasedUntil < next))
                    next = q.LeasedUntil;
            }
            return next;
        }
    }

    /// <summary>Answer <paramref name="wire"/>'s pickup requests.</summary>
    public void Install(FakePickupExchange wire) => InstallAll(wire, this);

    /// <summary>Answer <paramref name="wire"/>'s pickup requests, each by the mediator it is addressed to.</summary>
    public static void InstallAll(FakePickupExchange wire, params Qa1Mediator[] mediators)
    {
        Qa1Mediator For(SentRequest r) => mediators.Single(m => m.Did == r.MediatorDid);
        wire.On(MessagePickup.DeliveryRequestType, r => For(r).Deliver(r, v4: false));
        wire.On(MessagePickup40.DeliveryRequestType, r => For(r).Deliver(r, v4: true));
        wire.On(MessagePickup.MessagesReceivedType, r =>
        {
            For(r).Remove(r.LocalDid, r.Ids);
            return FakePickupExchange.Status(r, For(r).Count);
        });
        wire.OneWay = sent =>
        {
            if (sent.Is(MessagePickup40.MessagesReceivedType))
                For(sent).Remove(sent.LocalDid, sent.Ids);
            return Task.CompletedTask;
        };
    }

    // A messages-received deletes the ids of the mailbox it comes from (ids are exact).
    private void Remove(string mailbox, IReadOnlyList<string> ids)
    {
        lock (_gate)
            _queue.RemoveAll(q => q.Mailbox == mailbox && ids.Contains(q.Id, StringComparer.Ordinal));
    }

    private InboundMessageSnapshot Deliver(SentRequest request, bool v4)
    {
        var limit = v4 ? request.MessageCountLimit : request.Limit;
        var filter = request.RecipientDid;
        var now = _clock.TrueNow;
        Qa1Queued[] picked;
        int count;
        lock (_gate)
        {
            picked = _queue.Where(q => q.Mailbox == request.LocalDid && q.LeasedUntil <= now && (filter is null || q.Recipient == filter)).OrderBy(q => q.Sequence).Take(limit).ToArray();
            if (Lease is { } lease)
            {
                foreach (var q in picked)
                    q.LeasedUntil = now + lease;
            }
            _deliveries.Add(picked.Select(q => q.Id).ToArray());
            count = _queue.Count(q => q.Mailbox == request.LocalDid && (filter is null || q.Recipient == filter));
        }
        var messages = picked.Select(q => new DeliveredMessage(q.Id, Encoding.UTF8.GetBytes(q.Packed))).ToArray();
        if (v4)
            return FakePickupExchange.Snapshot(MessagePickup40.CreateDelivery(Did, request.LocalDid, messages, thid: request.Request.Id));
        if (messages.Length == 0)
            return FakePickupExchange.Status(request, count, recipientDid: filter);
        return FakePickupExchange.Snapshot(MessagePickup.CreateDelivery(Did, request.LocalDid, messages, thid: request.Request.Id));
    }
}

/// <summary>One delivery handed to the unpacker through a <see cref="Qa1Exchange"/>.</summary>
/// <param name="Scope">Its scope.</param>
/// <param name="Ids">Its attachment ids, in order.</param>
/// <param name="Held">Which of them the memory held back when it was handed over.</param>
/// <param name="SettledAtStart">The unpacker's settled count when it was handed over.</param>
internal sealed record Qa1Delivery(DeliveryScope Scope, IReadOnlyList<string?> Ids, IReadOnlyList<bool> Held, long SettledAtStart)
{
    /// <summary>How many of its messages the workers open (not held back).</summary>
    public int Open => Held.Count(h => !h);

    /// <summary>The unpacker's task.</summary>
    public Task? Task { get; set; }
}

/// <summary>
/// A pickup exchange for the QA-1 tests: the wire is a <see cref="FakePickupExchange"/> (answered by
/// a <see cref="Qa1Mediator"/> when one is installed), and every delivery is opened by a real
/// <see cref="DeliveredMessageUnpacker"/> on a <see cref="Qa1Clock"/> over a <see cref="Qa1Host"/>.
/// <see cref="DriveAsync{T}"/> runs a call to its end, moving the clock to the next due timer each
/// time every worker of the delivery being opened is parked on the clock.
/// </summary>
/// <remarks>
/// <strong>The host's lookups, as modelled.</strong> A lookup that honours its token has ended
/// before the next call is driven. A host call keeps its reservation until it ends, and one whose
/// token was cancelled can end after its delivery's result is set (nothing makes its continuation
/// run inside the cancellation). So <see cref="DriveAsync{T}"/> first waits until the only
/// reservations held are those of lookups that never end (<see cref="Qa1Host.NeverEnding"/>): the
/// event "every other host call has ended", not a neighbour of it. Every loop over this exchange
/// that asserts a bound or an exact time gets that order from here
/// (<see cref="SlowSenderFetchQa1Tests.ReachAsync"/>, <see cref="Qa1Loop"/>). A test of what a
/// delivery does when it arrives before such lookups have ended drives it with
/// <c>unwaited: true</c>. Not modelled: a new unpacker (<see cref="Restart"/>) while lookups that
/// never end are still held.
/// </remarks>
internal sealed class Qa1Exchange : IMediatorExchange
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly List<Qa1Delivery> _deliveries = new();
    private int _mostReserved;

    public Qa1Exchange(Qa1Clock clock, Qa1Host host, Action<MediatorClientOptions>? configure = null)
    {
        Clock = clock;
        Host = host;
        Wire = new FakePickupExchange(configure);
        Restart();
    }

    public Qa1Clock Clock { get; }

    public Qa1Host Host { get; }

    /// <summary>The wire: requests sent and their answers.</summary>
    public FakePickupExchange Wire { get; }

    /// <summary>The unpacker of the current client.</summary>
    public DeliveredMessageUnpacker Unpacker { get; private set; } = null!;

    /// <summary>Every delivery handed to the unpacker.</summary>
    public IReadOnlyList<Qa1Delivery> Deliveries
    {
        get
        {
            lock (_gate)
                return _deliveries.ToArray();
        }
    }

    /// <summary>A new client in a new process: a new unpacker (its memory and reservations empty) and a new duplicate filter.</summary>
    public void Restart()
    {
        Unpacker = new DeliveredMessageUnpacker(Host.UnpackAsync, Wire.Options.UnpackTimeout, Clock);
        Duplicates = new DuplicateFilter(Wire.Options.DedupeCapacity);
    }

    /// <summary>A pickup client over this exchange.</summary>
    public PickupClient Client() => new(this, logger: null);

    public MediatorClientOptions Options => Wire.Options;

    public TimeProvider Time => Clock;

    public DuplicateFilter Duplicates { get; private set; } = null!;

    public NegotiatedVersionCache<CoordinateMediationVersion> Versions => Wire.Versions;

    public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => Wire.PickupVersions;

    public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
        => Wire.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

    public Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        => Wire.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);

    public Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct)
        => Wire.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);

    public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => Wire.RegisterLiveStream(mediatorDid, localDid);

    public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct)
    {
        var ids = messages.Select(m => m.Id).ToArray();
        // The same question the unpacker asks first, at the same reading, so the driver knows how
        // many messages its workers open; it moves every id it names to the back of the eviction
        // order in the same order the unpacker's own question does, so it changes no eviction.
        bool[] held;
        try
        {
            held = Unpacker.Memory.HeldBack(scope, ids);
        }
        catch (InvalidOperationException) when (Clock.Throws)
        {
            held = new bool[ids.Length]; // the host's clock throws: the unpacker's own question will too
        }
        var delivery = new Qa1Delivery(scope, ids, held, Unpacker.Settled);
        lock (_gate)
            _deliveries.Add(delivery);
        var task = Unpacker.UnpackAllAsync(scope, messages, requestId, ct);
        delivery.Task = task;
        return task;
    }

    /// <summary>
    /// Run <paramref name="start"/>'s call to its end: each time every worker of the delivery being
    /// opened is parked on the clock (or the delivery has not started), the clock moves exactly to
    /// the next due timer. Real time is only a hang guard.
    /// </summary>
    /// <param name="start">Starts the call.</param>
    /// <param name="unwaited">True only for a test of a delivery that arrives while lookups of the one before are still ending: the call then starts at once.</param>
    public async Task<T> DriveAsync<T>(Func<Task<T>> start, bool unwaited = false)
    {
        if (!unwaited)
            LookupsEnded();
        int before;
        lock (_gate)
            before = _deliveries.Count;
        var operation = start();
        while (true)
        {
            if (!SpinWait.SpinUntil(() => operation.IsCompleted || Parked(before), Guard))
                throw new TimeoutException($"The call neither ended nor parked: waiting {Host.Waiting}, settled {Unpacker.Settled}, deliveries {Deliveries.Count}, timers {Clock.Scheduled}.");
            if (operation.IsCompleted)
                return await operation;
            var next = Clock.NextDueIn() ?? throw new InvalidOperationException($"Every worker is parked and no timer is due: waiting {Host.Waiting}, settled {Unpacker.Settled}, open {Deliveries[^1].Open}, from {Deliveries[^1].SettledAtStart}, tracked {Clock.Scheduled}, inner {Clock.Inner.ScheduledTimers}, done {operation.IsCompleted}.");
            Clock.Inner.Advance(next);
        }
    }

    /// <summary>The most reservations seen still held by lookups that honour their token, while a call was kept from starting for them.</summary>
    public int MostReservedBeforeACall => Volatile.Read(ref _mostReserved);

    /// <summary>Whether a call was kept from starting while a lookup that honours its token still held its reservation.</summary>
    public bool SawLookupsStillEnding => Volatile.Read(ref _mostReserved) > 0;

    /// <summary>
    /// Wait (bounded, in real time) until every host call that honours its token has ended and
    /// given its reservation back: the only reservations left are those of lookups that never end.
    /// </summary>
    public void LookupsEnded()
    {
        var ended = SpinWait.SpinUntil(() =>
        {
            var stillEnding = Unpacker.Reserved - Host.NeverEnding;
            if (stillEnding == 0)
                return true;
            if (stillEnding > Volatile.Read(ref _mostReserved))
                Volatile.Write(ref _mostReserved, stillEnding);
            return false;
        }, Guard);
        if (!ended)
            throw new TimeoutException($"The host calls of the deliveries before did not end: {Unpacker.Reserved} reserved, {Host.NeverEnding} of them by lookups that never end.");
    }

    // Every worker of the latest delivery (handed over after `before`) waits on the clock, and some
    // message of it is still unsettled.
    private bool Parked(int before)
    {
        Qa1Delivery? delivery;
        lock (_gate)
            delivery = _deliveries.Count > before ? _deliveries[^1] : null;
        if (delivery?.Task is not { IsCompleted: false })
            return false;
        var open = delivery.Open;
        var unsettled = open - (int)(Unpacker.Settled - delivery.SettledAtStart);
        var workers = Math.Min(DeliveredMessageUnpacker.FetchConcurrency, open);
        // With every message settled the delivery ends on its own, without the clock.
        return unsettled > 0 && Host.Waiting == Math.Min(workers, unsettled);
    }
}

/// <summary>What a stand-in host does with the failed items a fetch or a stream hands it (#111).</summary>
public enum Qa1Discard
{
    /// <summary>It keeps every failed item queued.</summary>
    Nothing,

    /// <summary>
    /// The careful host: it discards a failed item (<see cref="PickupClient.AckAsync"/>) unless
    /// <see cref="PickupItem.NotAttempted"/> says the client did not try to open it.
    /// </summary>
    WhatWasTried,

    /// <summary>A host that does not read <see cref="PickupItem.NotAttempted"/>: it discards every failed item.</summary>
    EveryFailure,
}

/// <summary>Shorthands of the QA-1 tests.</summary>
internal static class Qa1
{
    /// <summary>The ids of the failed items among <paramref name="items"/> that a host with <paramref name="discard"/> discards, in order.</summary>
    public static string[] ToDiscard(IEnumerable<PickupItem> items, Qa1Discard discard)
        => discard switch
        {
            Qa1Discard.EveryFailure => items.Where(i => i.Error is not null).Select(i => i.Id).ToArray(),
            Qa1Discard.WhatWasTried => items.Where(i => i.Error is not null && !i.NotAttempted).Select(i => i.Id).ToArray(),
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// Wait (bounded, in real time) until every host call of <paramref name="unpacker"/> has ended
    /// and given its reservation back. A delivery's end cancels the calls still running and does not
    /// wait for them, so a test whose next delivery follows at once, with no request to a mediator
    /// in between, waits here first: whether that delivery's first messages are refused for capacity
    /// would otherwise depend on which continuation ran first (L-065).
    /// </summary>
    public static void LookupsEnded(DeliveredMessageUnpacker unpacker)
    {
        if (!SpinWait.SpinUntil(() => unpacker.Reserved == 0, Qa1Exchange.Guard))
            throw new TimeoutException($"The host calls of the last delivery did not end: {unpacker.Reserved} reservations are still held.");
    }

    /// <summary>The default budget (20 s).</summary>
    public static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout;

    /// <summary>The default lease of this library's mediator (60 s).</summary>
    public static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout;

    /// <summary>The scope of Bob's mailbox at the default mediator.</summary>
    public static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>Anoncrypt carrying <paramref name="label"/>.</summary>
    public static string Anon(string label) => TestJwe.Anoncrypt(label);

    /// <summary>Authcrypt from <paramref name="kid"/> carrying <paramref name="label"/>.</summary>
    public static string From(string kid, string label) => TestJwe.Authcrypt(kid, label);

    /// <summary>A delivered message with <paramref name="id"/>.</summary>
    public static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));
}
