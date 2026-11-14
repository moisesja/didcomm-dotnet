using System.Globalization;
using System.Text;
using DidComm.Exceptions;
using DidComm.Facade;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// Opens the messages of one delivery under the bounds of design D-19 / §4.9 as decided on the
/// L-063 stop (owner decision 2-A): <strong>one budget</strong> (<see cref="Budget"/>, a
/// <see cref="GuardedDeadline"/> on the injected <see cref="TimeProvider"/>, never a
/// <c>CancelAfter</c> token handed to host code) for all of a delivery's messages, every re-wrap
/// layer included, of which each message gets an equal share (<see cref="ShareOf"/>: the budget
/// divided by the rounds the workers need), so a message slow to open cannot keep the messages
/// behind it from their turn; at most <see cref="FetchConcurrency"/> opened at a time; and a check
/// against <see cref="MaxAbandoned"/> abandoned unpacks of this unpacker (one per channel, so one
/// per container) still running, made when a message starts: at or beyond it the message fails at
/// once without starting (a check, not a reservation: see the known limits below). A message its
/// share or the budget runs out on — still being opened, or (only as a backstop) still waiting for
/// its turn — is a failure
/// (<see cref="TimeoutException"/> naming the delivery's request id when there is one), so a
/// delivery ends within its budget, well inside the mediator's lease (ADV2-1). An unpack that does
/// not honour cancellation keeps its abandoned slot until it ends (L-059). A bad message is a
/// failure outcome, never an exception; only the caller's cancellation and the owner's shutdown
/// throw. Owner: engineer A.
/// </summary>
/// <remarks>
/// <para>
/// The outcome of a delivery is decided by whichever comes first: its last message ending, or its
/// budget. The budget is linked to the caller's token and to the owner's shutdown (the channel's,
/// cancelled when the container is disposed: ADV3-7), so its own callback runs on any of the three:
/// it fills in every message that has no outcome yet, lets go of the unpacks still running and
/// completes the result, synchronously; it never waits for host code to notice its cancellation,
/// and whatever such code does afterwards is discarded. After the caller's cancellation or the
/// shutdown the result is cancelled, and the host code still running sees its token cancelled. The
/// result's continuations run asynchronously, never inside the timer's callback.
/// </para>
/// <para>
/// <strong>Host code never runs on the caller's stack (ADV3-4).</strong> Every unpack (host code:
/// the DID and secrets resolvers behind it) runs on a thread-pool work item of its own
/// (<see cref="Task.Run{TResult}(Func{Task{TResult}})"/>), never on the stack of the caller of
/// <see cref="UnpackAllAsync(IReadOnlyList{ReadOnlyMemory{byte}}, string?, CancellationToken)"/> or of
/// a share's timer. So the caller always gets its task back, and the budget decides the outcome,
/// even when a resolver blocks its thread. Blocking host code cannot be bounded: the blocked thread
/// and its work are the host's until it returns. Its task exists from the start, so while it runs
/// past its share or the budget it keeps an abandoned slot like any other unpack.
/// </para>
/// <para>
/// <strong>Known limits, deferred to #111</strong>
/// (<see href="https://github.com/moisesja/didcomm-dotnet/issues/111">slow-sender hardening</see>):
/// a message's share is fixed from the delivery's size before any message runs, so messages that
/// fail at once leave an honest one only its share (ADV3-3); the bound of
/// <see cref="MaxAbandoned"/> is checked when a message starts, not reserved, so deliveries opened at
/// the same moment can together leave more abandoned unpacks running than the bound (ADV3-2); and
/// the slots are shared by the whole container, so a host resolver that ignores cancellation can
/// hold all of them for every tenant (ADV2-2). Nothing remembers a slow message from one delivery to
/// the next (ADV3-6).
/// </para>
/// </remarks>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>How many messages of one delivery are opened at the same time.</summary>
    internal const int FetchConcurrency = 4;

    /// <summary>The bound on abandoned unpacks (budget passed, work still running) before new unpacks fail fast.</summary>
    internal const int MaxAbandoned = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private int _abandoned;
    private long _settled;
    private int _opening;

    /// <summary>Create an unpacker.</summary>
    /// <param name="unpack">The unpack step: <see cref="DidCommClient.UnpackDeliveredAsync"/> in production, a gated fake in tests.</param>
    /// <param name="budget">The budget of one delivery (<see cref="MediatorClientOptions.UnpackTimeout"/>).</param>
    /// <param name="time">The clock of the budget timers.</param>
    /// <param name="shutdown">The owner's shutdown (the channel's, cancelled when the container is disposed): it ends every delivery being opened, as the caller's cancellation does; none by default.</param>
    internal DeliveredMessageUnpacker(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, TimeSpan budget, TimeProvider time, CancellationToken shutdown = default)
    {
        ArgumentNullException.ThrowIfNull(unpack);
        ArgumentNullException.ThrowIfNull(time);
        Unpack = unpack;
        Budget = budget;
        Time = time;
        Shutdown = shutdown;
    }

    /// <summary>The unpack step.</summary>
    internal Func<string, CancellationToken, Task<DeliveredUnpack>> Unpack { get; }

    /// <summary>The budget of one delivery: how long opening all of its messages may take.</summary>
    internal TimeSpan Budget { get; }

    /// <summary>The clock of the budget timers.</summary>
    internal TimeProvider Time { get; }

    /// <summary>The owner's shutdown: cancelling it ends every delivery being opened (ADV3-7).</summary>
    internal CancellationToken Shutdown { get; }

    /// <summary>How many abandoned unpacks are still running now (test seam).</summary>
    internal int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>
    /// How many messages so far had their outcome recorded by their own end — opened, failed, or
    /// out of their share — rather than by the budget's (test seam: unpacks run on the thread pool,
    /// so a test waits for this before it moves the clock past messages that open at once).
    /// </summary>
    internal long Settled => Interlocked.Read(ref _settled);

    /// <summary>
    /// How many deliveries are being opened now (test seam: a delivery whose unpacks are still
    /// queued on the thread pool is being opened though no host code runs yet).
    /// </summary>
    internal int Opening => Volatile.Read(ref _opening);

    /// <summary>Open a delivery of one message (UTF-8 packed bytes) under its own budget.</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled.</exception>
    internal async Task<DeliveredOutcome> UnpackAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => (await UnpackAllAsync(new[] { payload }, requestId: null, ct).ConfigureAwait(false))[0];

    /// <summary>Open the messages of one delivery under one budget, with no request id to name (a live push).</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled.</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => UnpackAllAsync(payloads, requestId: null, ct);

    /// <summary>
    /// Open the messages of one delivery under one budget, at most <see cref="FetchConcurrency"/> at
    /// a time; the outcomes are in the order of <paramref name="payloads"/>. The returned task is
    /// complete when the last message has ended or, at the latest, when the budget runs out; it is
    /// returned before any host code runs.
    /// </summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="requestId">Our id of the request the delivery answers, named by the timeout failures; <c>null</c> for a live push.</param>
    /// <param name="ct">The caller's cancellation; it and <see cref="Shutdown"/> alone make the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> or <see cref="Shutdown"/> was cancelled (the task is cancelled; the channel reports the shutdown as <see cref="ObjectDisposedException"/>).</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeliveredOutcome>>(ct);
        if (Shutdown.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeliveredOutcome>>(Shutdown);
        if (payloads.Count == 0)
            return Task.FromResult<IReadOnlyList<DeliveredOutcome>>(Array.Empty<DeliveredOutcome>());
        return new Delivery(this, payloads, requestId, ct).Start();
    }

    // The unpack's wait passed the budget: its outcome is observed whenever it comes, and while it
    // still runs it keeps an abandoned slot (one that already ended gives the slot back at once).
    private void Abandon(Task work)
    {
        Interlocked.Increment(ref _abandoned);
        work.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // observed: a fault after the budget is not an unobserved exception
                Interlocked.Decrement(ref ((DeliveredMessageUnpacker)state!)._abandoned);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private DeliveredOutcome OutOfTime(string? requestId, bool started)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not unpacked: the delivery's budget of {Budget} (MediatorClientOptions.UnpackTimeout) ran out {(started ? "while it was being opened" : "before its turn came")}. It was not acknowledged.")));

    private static DeliveredOutcome OutOfShare(string? requestId, TimeSpan share)
        => DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"The delivered message{ForRequest(requestId)} was not unpacked: it used up its share of {share} of the delivery's budget (MediatorClientOptions.UnpackTimeout) while it was being opened. It was not acknowledged.")));

    /// <summary>
    /// Each message's share of a delivery's budget: the budget divided by the number of rounds the
    /// workers need for the delivery (<c>ceil(count / workers)</c>), so a message that is slow to open
    /// — its sender's DID resolves slowly — gives up its worker after its share and cannot keep the
    /// messages behind it from their turn (same-delivery starvation). A delivery of up to
    /// <see cref="FetchConcurrency"/> messages gives each the whole budget.
    /// </summary>
    /// <param name="budget">The delivery's budget.</param>
    /// <param name="count">The number of messages in the delivery (at least 1).</param>
    internal static TimeSpan ShareOf(TimeSpan budget, int count)
    {
        var workers = Math.Min(FetchConcurrency, Math.Max(1, count));
        var rounds = (Math.Max(1, count) + workers - 1) / workers;
        return TimeSpan.FromTicks(Math.Max(1, budget.Ticks / rounds));
    }

    private static string ForRequest(string? requestId)
        => requestId is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" of request '{requestId}'");

    /// <summary>
    /// One delivery being opened: up to <see cref="FetchConcurrency"/> workers take its messages in
    /// order; the first of "every message ended" and "the budget ended" closes it, under one lock,
    /// and completes the result. After it is closed nothing is started and nothing is recorded.
    /// </summary>
    private sealed class Delivery
    {
        private readonly DeliveredMessageUnpacker _owner;
        private readonly IReadOnlyList<ReadOnlyMemory<byte>> _payloads;
        private readonly string? _requestId;
        private readonly CancellationToken _ct;
        private readonly GuardedDeadline _budget;
        private readonly CancellationToken _budgetToken;
        private readonly DeliveredOutcome[] _outcomes;
        private readonly Task?[] _running;
        private readonly bool[] _started;
        private readonly TaskCompletionSource<IReadOnlyList<DeliveredOutcome>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly TimeSpan _share;
        private int _next;
        private int _done;
        private bool _closed;

        internal Delivery(DeliveredMessageUnpacker owner, IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
        {
            _owner = owner;
            _payloads = payloads;
            _requestId = requestId;
            _ct = ct;
            _outcomes = new DeliveredOutcome[payloads.Count];
            _running = new Task?[payloads.Count];
            _started = new bool[payloads.Count];
            _share = ShareOf(owner.Budget, payloads.Count);
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
            var workers = Math.Min(FetchConcurrency, _outcomes.Length);
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
                int index;
                lock (_gate)
                {
                    if (_closed || _next >= _outcomes.Length)
                        return;
                    index = _next++;
                }

                // The bound is checked before any work starts (L-055, L-059).
                if (Volatile.Read(ref _owner._abandoned) >= MaxAbandoned)
                {
                    Complete(index, DeliveredOutcome.Failure(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"The delivered message{ForRequest(_requestId)} was not unpacked: {MaxAbandoned} earlier unpacks passed their deadline and are still running (unpack capacity exhausted). It was not acknowledged."))));
                    continue;
                }
                string packed;
                try
                {
                    packed = StrictUtf8.GetString(_payloads[index].Span);
                }
                catch (DecoderFallbackException ex)
                {
                    Complete(index, DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is not valid UTF-8.", ex)));
                    continue;
                }
                if (packed.Length == 0)
                {
                    Complete(index, DeliveredOutcome.Failure(new MalformedMessageException("The delivered message is empty.")));
                    continue;
                }

                lock (_gate)
                {
                    if (_closed)
                        return; // recorded as "not started" by OnBudgetEnded
                    _started[index] = true;
                }
                // This message's share of the budget (ShareOf): its own guarded deadline, linked to the
                // budget, so a slow message gives up its worker and the messages behind it get their turn.
                var slot = new GuardedDeadline(_owner.Time, _share, _budgetToken);
                var slotToken = slot.Token;
                // The unpack is host code (the DID and secrets resolvers behind it), so it runs on a
                // thread-pool work item of its own, never on this worker's stack (ADV3-4): not on the
                // caller of UnpackAllAsync, which gets its task back even when a resolver blocks its
                // thread, nor on a share's timer. Its task exists at once, so an unpack that blocks
                // past its share or the budget keeps an abandoned slot like any other. Whatever it
                // throws, synchronously or not, is the task's: the wait below sorts it out.
                var work = Task.Run(() => _owner.Unpack(packed, slotToken));
                bool lateForIt;
                lock (_gate)
                {
                    // Closed between the start and here: OnBudgetEnded recorded it but could not let
                    // the work go, so this worker does.
                    lateForIt = _closed;
                    if (!lateForIt)
                        _running[index] = work;
                }
                if (lateForIt)
                {
                    ReleaseSlot(slot);
                    _owner.Abandon(work);
                    return;
                }

                DeliveredOutcome outcome;
                try
                {
                    // Through the message's share (linked to the budget), not on the work itself:
                    // when either ends this worker moves on, so an abandoned unpack never keeps the
                    // delivery (its bytes and the messages opened so far) alive (L-062).
                    var unpacked = await work.WaitAsync(slotToken).ConfigureAwait(false);
                    outcome = DeliveredOutcome.Success(unpacked.Result, unpacked.LayersOpened, DuplicateFilter.KeyOf(unpacked.Result, unpacked.InnermostPacked));
                }
                catch (OperationCanceledException) when (_budgetToken.IsCancellationRequested)
                {
                    ReleaseSlot(slot);
                    return; // the budget ended (or the caller cancelled): OnBudgetEnded records it
                }
                catch (OperationCanceledException) when (slotToken.IsCancellationRequested)
                {
                    // The message's share ran out before the budget. Let its work go (it keeps an
                    // abandoned slot until it ends) unless the delivery was closed meanwhile, in which
                    // case OnBudgetEnded recorded it and let the work go itself.
                    ReleaseSlot(slot);
                    bool closed;
                    lock (_gate)
                    {
                        closed = _closed;
                        if (!closed)
                            _running[index] = null;
                    }
                    if (closed)
                        return;
                    _owner.Abandon(work);
                    if (!Complete(index, OutOfShare(_requestId, _share)))
                        return;
                    // Never take the next message on the share timer's own thread.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    continue;
                }
                catch (Exception ex)
                {
                    outcome = DeliveredOutcome.Failure(ex);
                }
                // Disposed here, on whatever thread completed the unpack. When host code completed
                // it from a callback on the share's own token, that thread is inside the share's
                // Cancel(), and the share is disposed there: .NET tolerates it (the rest of the
                // callbacks still run; pinned by DeliveredUnpackAdv3Tests).
                slot.Dispose();
                if (!Complete(index, outcome))
                    return;
            }
        }

        // A share whose token was cancelled — its timer, its budget, or the caller — may be running
        // its callbacks on this very thread, inside that Cancel(): on these paths it is disposed
        // later, on the pool. (The success path disposes its share directly; see above.)
        private static void ReleaseSlot(GuardedDeadline slot)
            => ThreadPool.UnsafeQueueUserWorkItem(static deadline => deadline.Dispose(), slot, preferLocal: false);

        // Record one outcome; the last one closes the delivery. False when it was already closed (the
        // outcome is then discarded: OnBudgetEnded recorded this message and let its work go).
        private bool Complete(int index, DeliveredOutcome outcome)
        {
            bool last;
            lock (_gate)
            {
                if (_closed)
                    return false;
                _outcomes[index] = outcome;
                _running[index] = null;
                last = ++_done == _outcomes.Length;
                if (last)
                    _closed = true;
            }
            Interlocked.Increment(ref _owner._settled);
            if (last)
            {
                // Every share was disposed before its outcome was recorded; the budget goes before
                // the result is set, so whoever sees the result sees no timer of this delivery left.
                _budget.Dispose();
                _result.TrySetResult(_outcomes);
                Interlocked.Decrement(ref _owner._opening);
            }
            return true;
        }

        // The budget ran out, the caller cancelled or the owner shut down (the budget is linked to
        // both tokens): every message without an outcome is out of time — started or not — and the
        // unpacks still running are let go, keeping their abandoned slots until they end. After a
        // cancellation or the shutdown the result is cancelled instead.
        private void OnBudgetEnded()
        {
            List<Task>? running = null;
            bool cancelled;
            bool shutDown;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                cancelled = _ct.IsCancellationRequested;
                shutDown = _owner.Shutdown.IsCancellationRequested;
                for (var i = 0; i < _outcomes.Length; i++)
                {
                    if (_outcomes[i] is not null)
                        continue;
                    if (_running[i] is { } work)
                        (running ??= new List<Task>()).Add(work);
                    _outcomes[i] = _owner.OutOfTime(_requestId, _started[i]);
                }
            }
            if (running is not null)
            {
                foreach (var work in running)
                    _owner.Abandon(work);
            }
            if (cancelled)
                _result.TrySetCanceled(_ct);
            else if (shutDown)
                _result.TrySetCanceled(_owner.Shutdown);
            else
                _result.TrySetResult(_outcomes);
            Interlocked.Decrement(ref _owner._opening);
            // Not inside the Cancel() that is running this callback: later, on the pool.
            ThreadPool.UnsafeQueueUserWorkItem(static budget => budget.Dispose(), _budget, preferLocal: false);
        }
    }
}
