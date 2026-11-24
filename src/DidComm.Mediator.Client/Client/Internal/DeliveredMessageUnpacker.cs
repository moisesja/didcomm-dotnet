using System.Globalization;
using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Opens the messages of one delivery (#96 design D-19, owner decision 2-A; #111): one budget
/// (<see cref="Budget"/>, a <see cref="GuardedDeadline"/> on the injected <see cref="TimeProvider"/>,
/// never a <c>CancelAfter</c> token handed to host code) for all of a delivery's messages, every
/// re-wrap layer included; at most <see cref="FetchConcurrency"/> opened at a time; each message a
/// share of the time left (<see cref="ShareAt"/>), never below the floor <see cref="Floor"/>;
/// messages remembered as slow to open (<see cref="Memory"/>) held back without being opened; and
/// every host unpack call reserved (<see cref="Reservations"/>) before it starts. A bad message is a
/// failure outcome, never an exception; only the caller's cancellation and the owner's shutdown throw.
/// One unpacker per channel, so one per container.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Held back (I-13).</strong> Before any message is opened, the memory is asked once which
/// of the delivery's attachment ids are in their pause. Each such message fails at once (a
/// <see cref="TimeoutException"/> naming only our request id), takes no reservation, runs no host
/// code and is not acknowledged. The shares are sized from the other messages alone, and a delivery
/// made only of held-back messages returns at once, with no timer.
/// </para>
/// <para>
/// <strong>Each message a worker takes</strong>, at take position p (0-based) of the n messages not
/// held back, with B the budget, F the floor and e the time since the budget started (read once,
/// clamped to [0, B]):
/// </para>
/// <list type="number">
/// <item><description>It is decoded as strict UTF-8; one that is not, or is empty, fails as malformed.</description></item>
/// <item><description>It starts only if e ≤ B − F (I-6); otherwise it fails as out of time before its turn.</description></item>
/// <item><description>Its sender is read from the outer envelope (<see cref="SenderPartition"/>) and one host call is reserved for it (I-1, I-2). A refusal fails it at once ("unpack capacity exhausted").</description></item>
/// <item><description>Its share is <c>min(B − e, max(S(n), F, (B − e) / ceil((n − p) / 4)))</c>, with S(n) = <see cref="ShareOf"/> (I-7). So every message that starts has at least F.</description></item>
/// <item><description>Under the delivery's lock, and only while the delivery is open, its host call is created as a thread-pool work item holding only the unpack step, the packed text and the share's token (I-3, I-4). A call whose token is cancelled before it runs never runs.</description></item>
/// <item><description>The worker waits for the call through its share. Opened: a success. Failed: a failure. Out of its share: a failure, and the call is let go; it keeps its reservation until it ends.</description></item>
/// </list>
/// <para>
/// <strong>The end of a delivery (I-5).</strong> The first of "every message ended" and "the budget
/// ended" closes the delivery, under one lock. The budget is linked to the caller's token and to the
/// owner's shutdown (the channel's, cancelled when the container is disposed: ADV3-7), so its own
/// callback runs on any of the three: it fills in every message that has no outcome yet as out of
/// time and completes the result, never waiting for host code; whatever such code does afterwards is
/// discarded. After the caller's cancellation or the shutdown the result is cancelled, and the host
/// code still running sees its token cancelled. The result's continuations run asynchronously.
/// </para>
/// <para>
/// <strong>What is remembered (I-9).</strong> A message whose host call ran and did not open is
/// struck when the call started after the budget did and ran for at least <see cref="SlowThreshold"/>
/// until its outcome was decided: by its own failure, by its share's end, or by the budget's end.
/// A message that opens is forgotten. Nothing is written for a refusal, a message that did not
/// start, a call whose token was cancelled before its work item ran (it never runs), a malformed
/// message, or a message still running when the caller cancelled or the shutdown came. Whoever records an outcome makes its write, and every write is made before the delivery's
/// result is set.
/// </para>
/// <para>
/// <strong>Host code never runs on the caller's stack (ADV3-4).</strong> Every host unpack call (the
/// DID and secrets resolvers behind it) runs on a thread-pool work item of its own, never on the
/// stack of the caller of
/// <see cref="UnpackAllAsync(DeliveryScope, IReadOnlyList{DeliveredInput}, string?, CancellationToken)"/>
/// or of a timer. So the caller always gets its task back, and the budget decides the outcome, even
/// when a resolver blocks its thread. The lookup is host code and may outlive its share: its
/// reservation is held until it returns, so at most <see cref="UnpackReservations.MaxReserved"/> such
/// calls run per container (R-8).
/// </para>
/// </remarks>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>How many messages of one delivery are opened at the same time.</summary>
    internal const int FetchConcurrency = 4;

    // τ = (B − F) / SlowDivisor: ceil(100 / 4) rounds of failures that each cost just under τ fit in
    // B − F, so a delivery of up to 100 fast failures starts every message (I-8).
    private const int SlowDivisor = 25;

    // T0 = 30·B and Cap = 180·B (D-2).
    private const int FirstPauseInBudgets = 30;
    private const int LongestPauseInBudgets = 180;

    // The scope of the overloads without one: their messages have no ids, so nothing is remembered.
    private static readonly DeliveryScope DefaultScope = new(string.Empty, string.Empty);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private long _settled;
    private int _opening;

    /// <summary>Create an unpacker.</summary>
    /// <param name="unpack">The unpack step: <see cref="DidCommClient.UnpackDeliveredAsync"/> in production, a gated fake in tests.</param>
    /// <param name="budget">The budget of one delivery (<see cref="MediatorClientOptions.UnpackTimeout"/>).</param>
    /// <param name="time">The clock of the budget timers and of the memory's pauses.</param>
    /// <param name="shutdown">The owner's shutdown (the channel's, cancelled when the container is disposed): it ends every delivery being opened, as the caller's cancellation does; none by default.</param>
    /// <param name="reservations">The host-call reservations (a test seam); a new set by default.</param>
    internal DeliveredMessageUnpacker(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, TimeSpan budget, TimeProvider time, CancellationToken shutdown = default, UnpackReservations? reservations = null)
    {
        ArgumentNullException.ThrowIfNull(unpack);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        Unpack = unpack;
        Budget = budget;
        Time = time;
        Shutdown = shutdown;
        Floor = TimeSpan.FromTicks(Math.Max(1, budget.Ticks / 4));
        SlowThreshold = TimeSpan.FromTicks(Math.Max(1, (budget.Ticks - Floor.Ticks) / SlowDivisor));
        Memory = new SlowMemory(time, TimeSpan.FromTicks(budget.Ticks * FirstPauseInBudgets), TimeSpan.FromTicks(budget.Ticks * LongestPauseInBudgets), SlowMemory.EntriesPerPartition, SlowMemory.MaxPartitions);
        Reservations = reservations ?? new UnpackReservations();
    }

    /// <summary>The unpack step.</summary>
    internal Func<string, CancellationToken, Task<DeliveredUnpack>> Unpack { get; }

    /// <summary>B: the budget of one delivery, how long opening all of its messages may take.</summary>
    internal TimeSpan Budget { get; }

    /// <summary>The clock of the budget timers and of the memory's pauses.</summary>
    internal TimeProvider Time { get; }

    /// <summary>The owner's shutdown: cancelling it ends every delivery being opened (ADV3-7).</summary>
    internal CancellationToken Shutdown { get; }

    /// <summary>
    /// F = max(1 tick, B / 4): the least share a message that starts gets, and how much of the budget
    /// must be left for a message to start at all (I-6).
    /// </summary>
    internal TimeSpan Floor { get; }

    /// <summary>
    /// τ = (B − F) / 25 (at least 1 tick): a message whose host call ran at least this long and did not
    /// open is remembered as slow (I-9, D-6).
    /// </summary>
    internal TimeSpan SlowThreshold { get; }

    /// <summary>The messages remembered as slow to open, per (mediator, mailbox); pauses of 30·B doubling up to 180·B (D-2).</summary>
    internal SlowMemory Memory { get; }

    /// <summary>The reservations of host unpack calls: per container, and per (mediator, mailbox, sender).</summary>
    internal UnpackReservations Reservations { get; }

    /// <summary>How many host unpack calls are started and not yet ended now (test seam: <see cref="UnpackReservations.Reserved"/>).</summary>
    internal int Reserved => Reservations.Reserved;

    /// <summary>
    /// How many messages so far had their outcome recorded by their own end — opened, failed, refused
    /// or out of their share — rather than by the budget's (test seam: unpacks run on the thread pool,
    /// so a test waits for this before it moves the clock past messages that open at once).
    /// </summary>
    internal long Settled => Interlocked.Read(ref _settled);

    /// <summary>
    /// How many deliveries are being opened now (test seam: a delivery whose unpacks are still
    /// queued on the thread pool is being opened though no host code runs yet).
    /// </summary>
    internal int Opening => Volatile.Read(ref _opening);

    /// <summary>Open a delivery of one message (UTF-8 packed bytes) under its own budget, with no scope or id: nothing is remembered.</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled.</exception>
    internal async Task<DeliveredOutcome> UnpackAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => (await UnpackAllAsync(new[] { payload }, requestId: null, ct).ConfigureAwait(false))[0];

    /// <summary>Open the messages of one delivery under one budget, with no request id to name and no scope or ids: nothing is remembered.</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled.</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => UnpackAllAsync(payloads, requestId: null, ct);

    /// <summary>
    /// Open the messages of one delivery under one budget with no scope or ids: they are counted in
    /// one fixed scope's reservations, and nothing is remembered or held back.
    /// </summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="requestId">Our id of the request the delivery answers, named by the timeout failures; <c>null</c> for a live push.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled.</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var messages = new DeliveredInput[payloads.Count];
        for (var i = 0; i < messages.Length; i++)
            messages[i] = new DeliveredInput(Id: null, payloads[i]);
        return UnpackAllAsync(DefaultScope, messages, requestId, ct);
    }

    /// <summary>
    /// Open the messages of one delivery from <paramref name="scope"/> (the mediator and mailbox the
    /// host's call named), each with its attachment id: messages in their pause fail at once without
    /// being opened, and the others are opened under one budget, at most
    /// <see cref="FetchConcurrency"/> at a time. The outcomes are in the order of
    /// <paramref name="messages"/>. The returned task is complete when the last message has ended or,
    /// at the latest, when the budget runs out; it is returned before any host code runs.
    /// </summary>
    /// <param name="scope">The mediator and the mailbox the delivery came from.</param>
    /// <param name="messages">The delivered messages, in delivery order.</param>
    /// <param name="requestId">Our id of the request the delivery answers, named by the timeout failures; <c>null</c> for a live push.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled (the task is cancelled; the channel reports the shutdown as <see cref="ObjectDisposedException"/>).</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeliveredOutcome>>(ct);
        if (Shutdown.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeliveredOutcome>>(Shutdown);
        if (messages.Count == 0)
            return Task.FromResult<IReadOnlyList<DeliveredOutcome>>(Array.Empty<DeliveredOutcome>());

        // The pre-pass (I-13): no host code, no reservation and no timer for a held-back message.
        var ids = new string?[messages.Count];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = messages[i].Id;
        var held = Memory.HeldBack(scope, ids);
        var outcomes = new DeliveredOutcome[messages.Count];
        var order = new List<int>(messages.Count);
        for (var i = 0; i < held.Length; i++)
        {
            if (held[i])
                outcomes[i] = HeldBackFailure(requestId);
            else
                order.Add(i);
        }
        if (order.Count == 0)
            return Task.FromResult<IReadOnlyList<DeliveredOutcome>>(outcomes);
        return new Delivery(this, scope, messages, order.ToArray(), outcomes, requestId, ct).Start();
    }

    /// <summary>
    /// The static share S(n): the budget divided by the number of rounds the workers need for n
    /// messages (<c>ceil(n / 4)</c>). A delivery of up to <see cref="FetchConcurrency"/> messages gives
    /// each the whole budget. It is the least share of <see cref="ShareAt"/> after the floor.
    /// </summary>
    /// <param name="budget">The delivery's budget.</param>
    /// <param name="count">The number of messages the workers open (at least 1).</param>
    internal static TimeSpan ShareOf(TimeSpan budget, int count)
    {
        var workers = Math.Min(FetchConcurrency, Math.Max(1, count));
        var rounds = (Math.Max(1, count) + workers - 1) / workers;
        return TimeSpan.FromTicks(Math.Max(1, budget.Ticks / rounds));
    }

    /// <summary>
    /// The share of the message at take position <paramref name="position"/> of <paramref name="count"/>,
    /// started <paramref name="elapsed"/> into the budget (I-7): the time left divided by the rounds
    /// of four the messages not yet taken need, this one included, never below <see cref="ShareOf"/>
    /// or <see cref="Floor"/>, and never beyond the time left.
    /// </summary>
    /// <param name="count">n: the messages the workers open (held-back ones excluded).</param>
    /// <param name="position">p: the message's take position, from 0.</param>
    /// <param name="elapsed">e: the time since the budget started, in [0, B − F].</param>
    internal TimeSpan ShareAt(int count, int position, TimeSpan elapsed)
    {
        var left = Budget - elapsed;
        var rounds = (count - position + FetchConcurrency - 1) / FetchConcurrency;
        var byTimeLeft = TimeSpan.FromTicks(left.Ticks / Math.Max(1, rounds));
        var share = ShareOf(Budget, count);
        if (Floor > share)
            share = Floor;
        if (byTimeLeft > share)
            share = byTimeLeft;
        return share < left ? share : left;
    }

    // I-9 (with the call having run): it started after the budget did, and ran for at least τ until
    // its outcome was decided.
    private bool WasSlow(long budgetStart, long started, long ended)
        => budgetStart <= started && started <= ended && Time.GetElapsedTime(started, ended) >= SlowThreshold;

    private DeliveredOutcome OutOfTime(string? requestId, bool started)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not unpacked: the delivery's budget of {Budget} (MediatorClientOptions.UnpackTimeout) ran out {(started ? "while it was being opened" : "before its turn came")}. It was not acknowledged.")));

    private static DeliveredOutcome OutOfShare(string? requestId, TimeSpan share)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not unpacked: it used up its share of {share} of the delivery's budget (MediatorClientOptions.UnpackTimeout) while it was being opened. It was not acknowledged.")));

    private static DeliveredOutcome HeldBackFailure(string? requestId)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not opened: it was slow to open when it was delivered before, and it is not opened again until its pause ends. It was not acknowledged.")));

    private static DeliveredOutcome CapacityExhausted(string? requestId, UnpackReservations.Refusal refusal)
        => DeliveredOutcome.Failure(new TimeoutException(refusal == UnpackReservations.Refusal.Container
            ? string.Create(CultureInfo.InvariantCulture,
                $"The delivered message{ForRequest(requestId)} was not unpacked: {UnpackReservations.MaxReserved} unpacks are still running, the bound for all mailboxes together (unpack capacity exhausted). It was not acknowledged.")
            : string.Create(CultureInfo.InvariantCulture,
                $"The delivered message{ForRequest(requestId)} was not unpacked: the unpacks still running for its sender in this mailbox (or, without a named sender, for every such message in it) are at their bound (unpack capacity exhausted). It was not acknowledged.")));

    private static string ForRequest(string? requestId)
        => requestId is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" of request '{requestId}'");

    // The host call's work item (I-4): it references exactly the unpack step, the packed text and
    // the share's token, so nothing reachable from it references the delivery. It also notes whether
    // the unpack ran: one whose token was cancelled before the work item ran never runs, and is not
    // remembered as slow (I-9).
    private sealed class HostCall(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, string packed, CancellationToken token)
    {
        private int _ran;

        internal bool Ran => Volatile.Read(ref _ran) != 0;

        internal Task<DeliveredUnpack> Run()
        {
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _ran, 1);
            return unpack(packed, token);
        }
    }

    // What the recorder of an outcome writes to the memory.
    private enum MemoryWrite
    {
        None,
        Strike,
        Forget,
    }

    // Why a delivery was closed.
    private enum Ending
    {
        Open,
        AllEnded,
        Budget,
        Caller,
        Shutdown,
    }

    /// <summary>
    /// One delivery being opened: up to <see cref="FetchConcurrency"/> workers take its messages in
    /// order; the first of "every message ended" and "the budget ended" closes it, under one lock.
    /// After it is closed nothing is recorded, no worker takes a new message and no host call is
    /// created. The result is set by the last of the outcome recorders, once its memory write is made.
    /// </summary>
    private sealed class Delivery
    {
        private readonly DeliveredMessageUnpacker _owner;
        private readonly DeliveryScope _scope;
        private readonly IReadOnlyList<DeliveredInput> _messages;
        private readonly int[] _order;
        private readonly string? _requestId;
        private readonly CancellationToken _ct;
        private readonly long _budgetStart;
        private readonly GuardedDeadline _budget;
        private readonly CancellationToken _budgetToken;
        private readonly DeliveredOutcome[] _outcomes;
        private readonly long[] _startedAt;
        private readonly HostCall?[] _calls;
        private readonly TaskCompletionSource<IReadOnlyList<DeliveredOutcome>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _next;
        private int _done;
        private int _writing;
        private Ending _ending;
        private bool _finished;

        // _order: the indices of the messages the workers open, in delivery order; _outcomes already
        // holds the held-back ones.
        internal Delivery(DeliveredMessageUnpacker owner, DeliveryScope scope, IReadOnlyList<DeliveredInput> messages, int[] order, DeliveredOutcome[] outcomes, string? requestId, CancellationToken ct)
        {
            _owner = owner;
            _scope = scope;
            _messages = messages;
            _order = order;
            _outcomes = outcomes;
            _requestId = requestId;
            _ct = ct;
            _startedAt = new long[messages.Count];
            _calls = new HostCall?[messages.Count];
            _budgetStart = owner.Time.GetTimestamp();
            // Linked to the caller and to the owner's shutdown, so the container's disposal ends
            // the delivery at once and every host lookup's token is cancelled (ADV3-7).
            _budget = new GuardedDeadline(owner.Time, owner.Budget, ct, owner.Shutdown);
            // Read once: the token stays readable after the deadline is disposed, its source does not.
            _budgetToken = _budget.Token;
        }

        internal Task<IReadOnlyList<DeliveredOutcome>> Start()
        {
            Interlocked.Increment(ref _owner._opening); // given back once, when the result is set
            // Runs inside the budget's (or the caller's) Cancel(): ours only, it never throws, waits
            // for nothing and runs no continuation inline (the result's run asynchronously).
            _budgetToken.UnsafeRegister(static state => ((Delivery)state!).OnBudgetEnded(), this);
            var workers = Math.Min(FetchConcurrency, _order.Length);
            for (var w = 0; w < workers; w++)
                GuardedDeadline.ObserveAbandoned(RunWorkerAsync());
            return _result.Task;
        }

        // Take the next message, open it, record its outcome; until there is none or the delivery is
        // closed. No exception leaves it: a bad message is an outcome, the budget's end, the caller's
        // cancellation and the owner's shutdown are OnBudgetEnded's to record.
        private async Task RunWorkerAsync()
        {
            while (true)
            {
                int position;
                lock (_gate)
                {
                    if (_ending != Ending.Open || _next >= _order.Length)
                        return;
                    position = _next++;
                }
                var index = _order[position];

                // Malformed: never remembered.
                string packed;
                try
                {
                    packed = StrictUtf8.GetString(_messages[index].Payload.Span);
                }
                catch (DecoderFallbackException ex)
                {
                    if (!Complete(index, DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is not valid UTF-8.", ex)), MemoryWrite.None))
                        return;
                    continue;
                }
                if (packed.Length == 0)
                {
                    if (!Complete(index, DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is empty.")), MemoryWrite.None))
                        return;
                    continue;
                }

                // The start rule (I-6): too little of the budget left for the floor; not started.
                var elapsed = Elapsed();
                if (elapsed > _owner.Budget - _owner.Floor)
                {
                    if (!Complete(index, _owner.OutOfTime(_requestId, started: false), MemoryWrite.None))
                        return;
                    continue;
                }

                // The reservation (I-1, I-2, D-7): taken before the host call exists; a refusal starts
                // nothing and is not remembered.
                if (!_owner.Reservations.TryTake(_scope, SenderPartition.Of(packed), out var reservation, out var refusal))
                {
                    if (!Complete(index, CapacityExhausted(_requestId, refusal), MemoryWrite.None))
                        return;
                    continue;
                }

                // This message's share (I-7): its own guarded deadline, linked to the budget, so a slow
                // message gives up its worker and the messages behind it get their turn.
                var share = _owner.ShareAt(_order.Length, position, elapsed);
                GuardedDeadline? deadline = null;
                CancellationToken shareToken;
                long startedAt;
                HostCall? call = null;
                Task<DeliveredUnpack>? work = null;
                try
                {
                    deadline = new GuardedDeadline(_owner.Time, share, _budgetToken);
                    shareToken = deadline.Token;
                    startedAt = _owner.Time.GetTimestamp();

                    // The host call is created under the lock, only while the delivery is open, with
                    // the share's token (I-3): if the token is cancelled before the work item runs, the
                    // unpack never runs. The work item holds only (unpack, text, token) (I-4). It runs
                    // on the thread pool, never on this worker's stack (ADV3-4).
                    lock (_gate)
                    {
                        if (_ending == Ending.Open)
                        {
                            call = new HostCall(_owner.Unpack, packed, shareToken);
                            _startedAt[index] = startedAt;
                            _calls[index] = call;
                            work = Task.Factory.StartNew(
                                static state => ((HostCall)state!).Run(),
                                call,
                                shareToken,
                                TaskCreationOptions.DenyChildAttach,
                                TaskScheduler.Default).Unwrap();
                        }
                    }
                }
                catch
                {
                    // The host's clock threw before the call existed: the reservation is not kept.
                    reservation.Release();
                    if (deadline is not null)
                        ReleaseShare(deadline);
                    throw;
                }
                if (work is null || call is null)
                {
                    // Closed before the call was created: OnBudgetEnded recorded it as not started.
                    reservation.Release();
                    ReleaseShare(deadline);
                    return;
                }
                // Whenever the call ends, its reservation is given back (once, whoever is first).
                reservation.ReleaseWhen(work);

                DeliveredOutcome outcome;
                MemoryWrite write;
                try
                {
                    // Through the message's share (linked to the budget), not on the work itself:
                    // when either ends this worker moves on.
                    var unpacked = await work.WaitAsync(shareToken).ConfigureAwait(false);
                    // D-13: the call ended while this worker waited for it, so its reservation is
                    // given back before the next take, whatever order the continuations run in.
                    reservation.Release();
                    outcome = DeliveredOutcome.Success(unpacked.Result, unpacked.LayersOpened, DuplicateFilter.KeyOf(unpacked.Result, unpacked.InnermostPacked));
                    write = MemoryWrite.Forget;
                }
                catch (OperationCanceledException) when (_budgetToken.IsCancellationRequested)
                {
                    ReleaseShare(deadline);
                    return; // the budget ended (or the caller or the shutdown): OnBudgetEnded records it
                }
                catch (OperationCanceledException) when (shareToken.IsCancellationRequested)
                {
                    // The message's share ran out before the budget: the call is let go and keeps
                    // its reservation until it ends.
                    ReleaseShare(deadline);
                    var cut = _owner.Time.GetTimestamp();
                    if (!Complete(index, OutOfShare(_requestId, share), call.Ran && _owner.WasSlow(_budgetStart, startedAt, cut) ? MemoryWrite.Strike : MemoryWrite.None))
                        return;
                    // Never take the next message on the share timer's own thread.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    continue;
                }
                catch (Exception ex)
                {
                    reservation.Release(); // D-13, as on success
                    var failed = _owner.Time.GetTimestamp();
                    outcome = DeliveredOutcome.Failure(ex);
                    write = call.Ran && _owner.WasSlow(_budgetStart, startedAt, failed) ? MemoryWrite.Strike : MemoryWrite.None;
                }
                // Disposed here, on whatever thread completed the unpack. When host code completed
                // it from a callback on the share's own token, that thread is inside the share's
                // Cancel(), and the share is disposed there: .NET tolerates it (the rest of the
                // callbacks still run; pinned by DeliveredUnpackAdv3Tests).
                deadline.Dispose();
                if (!Complete(index, outcome, write))
                    return;
            }
        }

        // e: the time since the budget started, read once, clamped to [0, B] (a clock that steps
        // back reads 0; one that jumps reads B).
        private TimeSpan Elapsed()
        {
            var elapsed = _owner.Time.GetElapsedTime(_budgetStart);
            if (elapsed < TimeSpan.Zero)
                return TimeSpan.Zero;
            return elapsed > _owner.Budget ? _owner.Budget : elapsed;
        }

        // A share whose token was cancelled — its timer, its budget, or the caller — may be running
        // its callbacks on this very thread, inside that Cancel(): on these paths it is disposed
        // later, on the pool. (The success and failure paths dispose their share directly.)
        private static void ReleaseShare(GuardedDeadline share)
            => ThreadPool.UnsafeQueueUserWorkItem(static deadline => deadline.Dispose(), share, preferLocal: false);

        // Record one outcome and make its memory write; the last outcome closes the delivery. False
        // when it was already closed (the outcome is then discarded and nothing is written:
        // OnBudgetEnded recorded this message).
        private bool Complete(int index, DeliveredOutcome outcome, MemoryWrite write)
        {
            lock (_gate)
            {
                if (_ending != Ending.Open)
                    return false;
                _outcomes[index] = outcome;
                if (++_done == _order.Length)
                    _ending = Ending.AllEnded;
                _writing++;
            }
            try
            {
                if (write == MemoryWrite.Strike)
                    _owner.Memory.Strike(_scope, _messages[index].Id);
                else if (write == MemoryWrite.Forget)
                    _owner.Memory.Forget(_scope, _messages[index].Id);
            }
            finally
            {
                Interlocked.Increment(ref _owner._settled);
                Written(); // the result is set even if the host's clock threw in the write
            }
            return true;
        }

        // The budget ran out, the caller cancelled or the owner shut down (the budget is linked to
        // both tokens): every message without an outcome is out of time, started or not. When the
        // budget itself ended the delivery, each message still running that was slow (I-9) is struck,
        // after the lock (the memory's lock is a leaf). Host calls still running keep their
        // reservations until they end. After a cancellation or the shutdown the result is cancelled.
        private void OnBudgetEnded()
        {
            // A clock that throws here strikes nothing (fail open) and never keeps the result unset (I-5).
            long ended;
            try
            {
                ended = _owner.Time.GetTimestamp();
            }
            catch
            {
                ended = long.MinValue;
            }
            List<string?>? slow = null;
            lock (_gate)
            {
                if (_ending != Ending.Open)
                    return;
                _ending = _ct.IsCancellationRequested ? Ending.Caller
                    : _owner.Shutdown.IsCancellationRequested ? Ending.Shutdown
                    : Ending.Budget;
                foreach (var index in _order)
                {
                    if (_outcomes[index] is not null)
                        continue;
                    _outcomes[index] = _owner.OutOfTime(_requestId, started: _calls[index] is not null);
                    if (_ending == Ending.Budget && _calls[index] is { Ran: true } && _owner.WasSlow(_budgetStart, _startedAt[index], ended))
                        (slow ??= new List<string?>()).Add(_messages[index].Id);
                }
                _writing++;
            }
            try
            {
                if (slow is not null)
                {
                    foreach (var id in slow)
                        _owner.Memory.Strike(_scope, id);
                }
            }
            finally
            {
                Written();
            }
        }

        // One recorder has made its write. The last one, once the delivery is closed, sets the
        // result, so whoever sees the result sees every write of the delivery.
        private void Written()
        {
            Ending ending;
            lock (_gate)
            {
                if (--_writing > 0 || _ending == Ending.Open || _finished)
                    return;
                _finished = true;
                ending = _ending;
            }
            if (ending == Ending.AllEnded)
            {
                // Every share was disposed before its outcome was recorded; the budget goes before
                // the result is set, so whoever sees the result sees no timer of this delivery left.
                _budget.Dispose();
                _result.TrySetResult(_outcomes);
            }
            else
            {
                if (ending == Ending.Caller)
                    _result.TrySetCanceled(_ct);
                else if (ending == Ending.Shutdown)
                    _result.TrySetCanceled(_owner.Shutdown);
                else
                    _result.TrySetResult(_outcomes);
                // Possibly inside the Cancel() that ran OnBudgetEnded: disposed later, on the pool.
                ThreadPool.UnsafeQueueUserWorkItem(static budget => budget.Dispose(), _budget, preferLocal: false);
            }
            Interlocked.Decrement(ref _owner._opening);
        }
    }
}
