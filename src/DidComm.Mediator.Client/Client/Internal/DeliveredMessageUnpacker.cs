using System.Globalization;
using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Opens the messages of one delivery (#96 design D-19, owner decision 2-A; #111): one budget
/// (<see cref="Budget"/>, a <see cref="GuardedDeadline"/> on the injected <see cref="TimeProvider"/>,
/// never a <c>CancelAfter</c> token handed to host code) for all of a delivery's messages, every
/// re-wrap layer included; at most <see cref="FetchConcurrency"/> opened at a time, each until it
/// opens, fails or the budget ends (no time limit of its own: #111, owner decision E); messages
/// remembered as not opening (<see cref="Memory"/>) held back without being opened; and every host
/// unpack call reserved (<see cref="Reservations"/>) before it starts. A bad message is a failure
/// outcome, never an exception; only the caller's cancellation and the owner's shutdown throw. One
/// unpacker per channel, so one per container.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Held back (I-13).</strong> Before any message is opened, the memory is asked once which
/// of the delivery's attachment ids are in their pause. Each such message fails at once (a
/// <see cref="TimeoutException"/> naming only our request id), takes no reservation, runs no host
/// code and is not acknowledged. A delivery made only of held-back messages returns at once, with no
/// timer.
/// </para>
/// <para>
/// <strong>Each message a worker takes</strong>, in delivery order, with B the budget, F the floor
/// (<see cref="Floor"/>) and e the time since the budget started (read once, clamped to [0, B]):
/// </para>
/// <list type="number">
/// <item><description>It is decoded as strict UTF-8; one that is not, or is empty, fails as malformed.</description></item>
/// <item><description>It starts only if e ≤ B − F (I-6); otherwise it fails as out of time before its turn.</description></item>
/// <item><description>Its sender is read from the outer envelope (<see cref="SenderPartition"/>) and one host call is reserved for it (I-1, I-2). A refusal fails it at once ("unpack capacity exhausted").</description></item>
/// <item><description>Under the delivery's lock, and only while the delivery is open, its host call is created as a thread-pool work item holding only the unpack step, the packed text, the budget's token and the clock (I-3, I-4). A call whose token is cancelled before it runs never runs.</description></item>
/// <item><description>The worker waits for the call through the budget's token. There is no other limit on a message: it runs until it opens, fails, or the delivery ends.</description></item>
/// </list>
/// <para>
/// <strong>The outcome of a call</strong> is one of three, decided by whether the delivery is being
/// ended when the worker observes it, never by the type of what was thrown:
/// </para>
/// <list type="bullet">
/// <item><description><em>Opened:</em> its reservation is given back, the success is recorded and the message is forgotten.</description></item>
/// <item><description><em>Not opened, and the delivery is being ended</em> (its token is cancelled, or the caller's token or the owner's shutdown is): the worker records nothing and takes no further message; the delivery's end records the message. Host code may report its cancellation as a failure of its own (a resolver that maps every error to "not found"), and may do so from a callback that runs before the delivery's; that is not the message's failure.</description></item>
/// <item><description><em>Failed while the delivery is not being ended:</em> the message's own failure. Its reservation is given back, the failure is recorded, and the message is remembered (S1, below).</description></item>
/// </list>
/// <para>
/// A worker takes no further message once the delivery is being ended. The cost of having no limit
/// per message: messages at the front that never open keep all <see cref="FetchConcurrency"/>
/// workers for one whole budget, and the messages behind them fail as out of time before their turn;
/// those at the front are remembered then, so the next delivery holds them back.
/// </para>
/// <para>
/// <strong>The end of a delivery (I-5).</strong> The first of "every message ended" and "the budget
/// ended" closes the delivery, under one lock. The budget is linked to the caller's token and to the
/// owner's shutdown (the channel's, cancelled when the container is disposed: ADV3-7), so its own
/// callback runs on any of the three: it fills in every message that has no outcome yet as out of
/// time and completes the result, never waiting for host code; whatever such code does afterwards is
/// discarded. After the caller's cancellation or the shutdown the result is cancelled. Host code
/// still running sees its token cancelled, and keeps its reservation until it ends. The result's
/// continuations run asynchronously.
/// </para>
/// <para>
/// <strong>What is remembered (I-9; owner decision C, "every failure, fair to late starts", under
/// owner decision E).</strong> A message whose host call ran (its code started:
/// <c>HostCall.Run</c> records the clock's reading as its first action) and did not open is struck
/// in two cases only:
/// </para>
/// <list type="bullet">
/// <item><description><em>S1, it failed on its own:</em> its failure was observed while the delivery was not being ended. Whatever the duration, and however late it started.</description></item>
/// <item><description><em>S2, it had the whole budget (the coordinator's call):</em> the budget's own timer ended the delivery (not the caller, not the shutdown), the message had no outcome yet, and its code started within <see cref="StartAllowance"/> of the budget's start, both readings taken from the injected clock. A message whose code started later (others were in front of it, or it waited for a thread) is not struck: it is opened again at the next delivery, where the messages that were in front of it are held back or gone, so it starts earlier.</description></item>
/// </list>
/// <para>
/// Nothing else is written: not for a refusal, a held-back message, a malformed message, a message
/// that did not start, a call whose token was cancelled before its work item ran (it never runs), a
/// message still running when the caller cancelled or the owner shut down, or a start reading below
/// the budget's (a clock that stepped back); and an entry still inside its pause gets no further
/// strike (RT1B-2). A message that opens is forgotten. Whoever records an outcome makes its write,
/// and every write is made before the delivery's result is set.
/// </para>
/// <para>
/// <strong>Host code never runs on the caller's stack (ADV3-4).</strong> Every host unpack call (the
/// DID and secrets resolvers behind it) runs on a thread-pool work item of its own, never on the
/// stack of the caller of
/// <see cref="UnpackAllAsync(DeliveryScope, IReadOnlyList{DeliveredInput}, string?, CancellationToken)"/>
/// or of a timer. So the caller always gets its task back, and the budget decides the outcome, even
/// when a resolver blocks its thread. The lookup is host code and may outlive its delivery: its
/// reservation is held until it returns, so at most <see cref="UnpackReservations.MaxReserved"/> such
/// calls run per container (R-8).
/// </para>
/// <para>
/// <strong>What host code can reach (I-4).</strong> Host code holds the budget's token. While the
/// delivery is open that token's source references the delivery: the delivery's own registration
/// on it, and its workers' waits. The workers' waits end with the delivery, and the delivery's
/// registration holds a link that is cleared when the delivery ends: host code that still runs, or
/// still holds a registration of its own on the token, then keeps nothing of the delivery reachable.
/// </para>
/// </remarks>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>
    /// How many messages of one delivery are opened at the same time: 16 (#111, owner decision G).
    /// Each message taken reserves one host call in its sender's partition, so a delivery can ask
    /// one partition for this many at once: <see cref="UnpackReservations.MaxPerSender"/> and
    /// <see cref="UnpackReservations.MaxPerAnonymous"/> are not below it, and the two change
    /// together. With a lower bound, messages of one sender are refused inside one delivery.
    /// </summary>
    internal const int FetchConcurrency = 16;

    // T0 = 30·B (D-2) and Cap = Q·B (QA1-1): with the longest pause as long as Q budgets, the
    // memory can hold a whole mailbox's Q entries paused at once, even when a fetch of one learns
    // one message per budget.
    private const int FirstPauseInBudgets = 30;
    private const int LongestPauseInBudgets = SlowMemory.EntriesPerPartition;

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
        StartAllowance = TimeSpan.FromTicks(Floor.Ticks / 2);
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
    /// F = max(1 tick, B / 4): how much of the budget must be left for a message to start at all
    /// (I-6).
    /// </summary>
    internal TimeSpan Floor { get; }

    /// <summary>
    /// F / 2 (the coordinator's call): how long after the budget's start a message's code may start
    /// and the message still count as having had the whole budget when the budget ends it (S2, I-9).
    /// A host call needs a thread before its code runs, so its code always starts a moment after the
    /// budget did; one that started later than this is not remembered at the budget's end.
    /// </summary>
    internal TimeSpan StartAllowance { get; }

    /// <summary>The messages remembered as not opening, per (mediator, mailbox); pauses of 30·B doubling up to Q·B = 256·B (D-2, QA1-1).</summary>
    internal SlowMemory Memory { get; }

    /// <summary>The reservations of host unpack calls: per container, and per (mediator, mailbox, sender).</summary>
    internal UnpackReservations Reservations { get; }

    /// <summary>How many host unpack calls are started and not yet ended now (test seam: <see cref="UnpackReservations.Reserved"/>).</summary>
    internal int Reserved => Reservations.Reserved;

    /// <summary>
    /// How many messages so far had their outcome recorded by their own end — opened, failed, refused,
    /// malformed or out of time before their turn — rather than by the delivery's (test seam: unpacks
    /// run on the thread pool, so a test waits for this before it moves the clock past messages that
    /// open at once).
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

    // S2 (I-9): whether a call still without an outcome at the budget's end had the whole budget.
    // Its code started (Ran), and started within StartAllowance of the budget's start:
    // 0 ≤ call.StartedAt − budgetStart ≤ StartAllowance, both readings from the injected clock. A
    // start reading below the budget's (a clock that stepped back) is not one: nothing is written
    // (fail open).
    private bool HadTheWholeBudget(HostCall call, long budgetStart)
    {
        if (!call.Ran)
            return false;
        var started = call.StartedAt;
        return started >= budgetStart && Time.GetElapsedTime(budgetStart, started) <= StartAllowance;
    }

    // Which failures say "not attempted" (DeliveredOutcome.NotAttempted, PickupItem.NotAttempted): the
    // two below that are made before a message's host call exists, and no other. A message that ran
    // out of time before its turn (started: false: the start rule refused it, no worker took it, or
    // the delivery ended before its call was created) and one refused for capacity were not tried in
    // this delivery, and nothing was learned about them. One whose call was created is an attempt,
    // also when the budget ended before its code started; a held-back message failed when it was
    // tried before.
    private DeliveredOutcome OutOfTime(string? requestId, bool started)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not unpacked: the delivery's budget of {Budget} (MediatorClientOptions.UnpackTimeout) ran out {(started ? "while it was being opened" : "before its turn came")}. It was not acknowledged.")),
            notAttempted: !started);

    private static DeliveredOutcome HeldBackFailure(string? requestId)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not opened: it did not open when it was delivered before, and it is not opened again until its pause ends. It was not acknowledged.")));

    private static DeliveredOutcome CapacityExhausted(string? requestId, UnpackReservations.Refusal refusal)
        => DeliveredOutcome.Failure(new TimeoutException(refusal == UnpackReservations.Refusal.Container
            ? string.Create(CultureInfo.InvariantCulture,
                $"The delivered message{ForRequest(requestId)} was not unpacked: {UnpackReservations.MaxReserved} unpacks are still running, the bound for all mailboxes together (unpack capacity exhausted). It was not acknowledged.")
            : string.Create(CultureInfo.InvariantCulture,
                $"The delivered message{ForRequest(requestId)} was not unpacked: the unpacks still running for its sender in this mailbox (or, without a named sender, for every such message in it) are at their bound (unpack capacity exhausted). It was not acknowledged.")),
            notAttempted: true);

    private static string ForRequest(string? requestId)
        => requestId is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" of request '{requestId}'");

    // The host call's work item (I-4): it references exactly the unpack step, the packed text, the
    // budget's token and the unpacker's clock. As its first action it reads the clock (when its code
    // started) and notes that it ran; one whose token was cancelled before the work item ran never
    // runs, and is not remembered (I-9).
    private sealed class HostCall(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, string packed, CancellationToken token, TimeProvider clock)
    {
        private int _ran;
        private long _startedAt;

        internal bool Ran => Volatile.Read(ref _ran) != 0;

        // The clock's reading when its code started; meaningful once Ran.
        internal long StartedAt => Volatile.Read(ref _startedAt);

        internal Task<DeliveredUnpack> Run()
        {
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref _startedAt, clock.GetTimestamp());
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
        private readonly HostCall?[] _calls;
        private readonly TaskCompletionSource<IReadOnlyList<DeliveredOutcome>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly EndLink _link;
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
            _calls = new HostCall?[messages.Count];
            _link = new EndLink(this);
            _budgetStart = owner.Time.GetTimestamp();
            // Linked to the caller and to the owner's shutdown, so the container's disposal ends
            // the delivery at once and every host lookup's token is cancelled (ADV3-7).
            _budget = new GuardedDeadline(owner.Time, owner.Budget, ct, owner.Shutdown);
            // Read once: the token stays readable after the deadline is disposed, its source does not.
            _budgetToken = _budget.Token;
        }

        // Whether the delivery is being ended from outside: its token is cancelled, or the caller's
        // token or the owner's shutdown is. Each of those two cancels the delivery's token, but a
        // callback host code registered on them after this delivery started runs before ours does,
        // so they are read too. From then on a worker records no failure and takes no message;
        // OnBudgetEnded records what has no outcome.
        private bool BeingEnded
            => _budgetToken.IsCancellationRequested || _ct.IsCancellationRequested || _owner.Shutdown.IsCancellationRequested;

        internal Task<IReadOnlyList<DeliveredOutcome>> Start()
        {
            Interlocked.Increment(ref _owner._opening); // given back once, when the result is set
            // Runs inside the budget's (or the caller's) Cancel(): ours only, it never throws, waits
            // for nothing and runs no continuation inline (the result's run asynchronously). Host
            // code registers on the same token later, so its callbacks run before this one. The
            // registration holds a link, not the delivery: Written() clears it (I-4).
            _budgetToken.UnsafeRegister(static state => ((EndLink)state!).Ended(), _link);
            var workers = Math.Min(FetchConcurrency, _order.Length);
            for (var w = 0; w < workers; w++)
                GuardedDeadline.ObserveAbandoned(RunWorkerAsync());
            return _result.Task;
        }

        // Take the next message, open it, record its outcome; until there is none, or the delivery
        // is closed or being ended. No exception leaves it: a bad message is an outcome, the budget's
        // end, the caller's cancellation and the owner's shutdown are OnBudgetEnded's to record.
        private async Task RunWorkerAsync()
        {
            while (true)
            {
                int position;
                lock (_gate)
                {
                    if (_ending != Ending.Open || _next >= _order.Length || BeingEnded)
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

                // The start rule (I-6): too little of the budget left; not started, not remembered.
                if (Elapsed() > _owner.Budget - _owner.Floor)
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

                // The host call is created under the lock, only while the delivery is open, with the
                // budget's token (I-3): if the token is cancelled before the work item runs, the
                // unpack never runs. The work item holds only (unpack, text, token, clock) (I-4). It
                // runs on the thread pool, never on this worker's stack (ADV3-4). No host code runs
                // between the take above and here, so nothing can throw with the reservation held.
                HostCall? call = null;
                Task<DeliveredUnpack>? work = null;
                lock (_gate)
                {
                    if (_ending == Ending.Open)
                    {
                        call = new HostCall(_owner.Unpack, packed, _budgetToken, _owner.Time);
                        _calls[index] = call;
                        work = Task.Factory.StartNew(
                            static state => ((HostCall)state!).Run(),
                            call,
                            _budgetToken,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default).Unwrap();
                    }
                }
                if (work is null || call is null)
                {
                    // Closed before the call was created: OnBudgetEnded recorded it as not started.
                    reservation.Release();
                    return;
                }
                // Whenever the call ends, its reservation is given back (once, whoever is first).
                reservation.ReleaseWhen(work);

                DeliveredOutcome outcome;
                MemoryWrite write;
                try
                {
                    // Through the budget's token, not on the work itself: when the delivery is ended
                    // this worker stops waiting, whatever the host code does.
                    var unpacked = await work.WaitAsync(_budgetToken).ConfigureAwait(false);
                    // Opened. D-13: the call ended while this worker waited for it, so its
                    // reservation is given back before the next take, whatever order the
                    // continuations run in.
                    reservation.Release();
                    outcome = DeliveredOutcome.Success(unpacked.Result, unpacked.LayersOpened, DuplicateFilter.KeyOf(unpacked.Result, unpacked.InnermostPacked));
                    write = MemoryWrite.Forget;
                }
                catch (Exception) when (BeingEnded)
                {
                    // Not opened, and the delivery is being ended. Whatever was thrown, a
                    // cancellation or a failure host code made of its cancellation, it is not the
                    // message's own: the token's state decides, never the exception's type. Nothing
                    // is recorded here and nothing more is taken; OnBudgetEnded records the message.
                    // The call keeps its reservation until it ends (ReleaseWhen).
                    return;
                }
                catch (Exception ex)
                {
                    // Failed while the delivery is not being ended: the message's own failure (S1),
                    // struck whatever the duration, provided its code ran.
                    reservation.Release(); // D-13, as on success
                    outcome = DeliveredOutcome.Failure(ex);
                    write = call.Ran ? MemoryWrite.Strike : MemoryWrite.None;
                }
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
        // both tokens): every message without an outcome is out of time, started or not. Host calls
        // still running keep their reservations until they end. After a cancellation or the shutdown
        // nothing is struck and the result is cancelled. When the budget's own timer ended the
        // delivery, each message whose call was created and that had the whole budget is struck (S2),
        // after the lock (the memory's lock is a leaf, and the host's clock is read there).
        private void OnBudgetEnded()
        {
            List<int>? running = null;
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
                    if (_ending == Ending.Budget && _calls[index] is not null)
                        (running ??= new List<int>()).Add(index);
                }
                _writing++;
            }
            try
            {
                // _calls is written only while the delivery is open, so it is read here as it was
                // left. A host clock that throws strikes nothing more and never keeps the result
                // unset (I-5).
                if (running is not null)
                {
                    foreach (var index in running)
                    {
                        if (_owner.HadTheWholeBudget(_calls[index]!, _budgetStart))
                            _owner.Memory.Strike(_scope, _messages[index].Id);
                    }
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
            // From here on the budget's token, which host code holds, references nothing of this
            // delivery (I-4).
            _link.Clear();
            if (ending == Ending.AllEnded)
            {
                // The budget goes before the result is set, so whoever sees the result sees no timer
                // of this delivery left. This thread may be inside the budget's own Cancel() (host
                // code that opened the last message from a callback on the token): .NET tolerates
                // the disposal there, and the rest of the callbacks still run (pinned by
                // DeliveredUnpackTokenStateTests).
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

        // What the budget's token references of a delivery (I-4): the delivery while it is open,
        // nothing once it has ended. Host code holds that token, and a registration made on a token
        // stays reachable from the registrations made after it, also once it has run or its source
        // is disposed. So the delivery registers this link instead of itself, and clears it when it
        // ends.
        private sealed class EndLink(Delivery delivery)
        {
            private Delivery? _delivery = delivery;

            internal void Ended() => Volatile.Read(ref _delivery)?.OnBudgetEnded();

            internal void Clear() => Volatile.Write(ref _delivery, null);
        }
    }
}
