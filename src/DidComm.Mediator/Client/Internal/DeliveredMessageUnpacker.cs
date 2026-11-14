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
/// behind it from their turn; at most <see cref="FetchConcurrency"/> opened at a time; and at most
/// <see cref="MaxAbandoned"/> abandoned unpacks of this unpacker (one per channel, so one per
/// container) still running, beyond which a message fails at once without starting. A message its
/// share or the budget runs out on — still being opened, or (only as a backstop) still waiting for
/// its turn — is a failure
/// (<see cref="TimeoutException"/> naming the delivery's request id when there is one), so a
/// delivery ends within its budget, well inside the mediator's lease (ADV2-1). An unpack that does
/// not honour cancellation keeps its abandoned slot until it ends (L-059; ADV2-2 is the documented
/// residual: a host resolver that ignores cancellation can hold all 16 slots). A bad message is a
/// failure outcome, never an exception; only the caller's cancellation throws. Owner: engineer A.
/// </summary>
/// <remarks>
/// The outcome of a delivery is decided by whichever comes first: its last message ending, or its
/// budget. The budget's own callback (on the budget's token, so also the caller's cancellation)
/// fills in every message that has no outcome yet, lets go of the unpacks still running and
/// completes the result, synchronously; it never waits for host code to notice its cancellation,
/// and whatever such code does afterwards is discarded. The result's continuations run
/// asynchronously, never inside the timer's callback.
/// </remarks>
internal sealed class DeliveredMessageUnpacker
{
    /// <summary>How many messages of one delivery are opened at the same time.</summary>
    internal const int FetchConcurrency = 4;

    /// <summary>The bound on abandoned unpacks (budget passed, work still running) before new unpacks fail fast.</summary>
    internal const int MaxAbandoned = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private int _abandoned;

    /// <summary>Create an unpacker.</summary>
    /// <param name="unpack">The unpack step: <see cref="DidCommClient.UnpackDeliveredAsync"/> in production, a gated fake in tests.</param>
    /// <param name="budget">The budget of one delivery (<see cref="MediatorClientOptions.UnpackTimeout"/>).</param>
    /// <param name="time">The clock of the budget timers.</param>
    internal DeliveredMessageUnpacker(Func<string, CancellationToken, Task<DeliveredUnpack>> unpack, TimeSpan budget, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(unpack);
        ArgumentNullException.ThrowIfNull(time);
        Unpack = unpack;
        Budget = budget;
        Time = time;
    }

    /// <summary>The unpack step.</summary>
    internal Func<string, CancellationToken, Task<DeliveredUnpack>> Unpack { get; }

    /// <summary>The budget of one delivery: how long opening all of its messages may take.</summary>
    internal TimeSpan Budget { get; }

    /// <summary>The clock of the budget timers.</summary>
    internal TimeProvider Time { get; }

    /// <summary>How many abandoned unpacks are still running now (test seam).</summary>
    internal int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>Open a delivery of one message (UTF-8 packed bytes) under its own budget.</summary>
    /// <param name="payload">The delivered bytes.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal async Task<DeliveredOutcome> UnpackAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => (await UnpackAllAsync(new[] { payload }, requestId: null, ct).ConfigureAwait(false))[0];

    /// <summary>Open the messages of one delivery under one budget, with no request id to name (a live push).</summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
        => UnpackAllAsync(payloads, requestId: null, ct);

    /// <summary>
    /// Open the messages of one delivery under one budget, at most <see cref="FetchConcurrency"/> at
    /// a time; the outcomes are in the order of <paramref name="payloads"/>. The returned task is
    /// complete when the last message has ended or, at the latest, when the budget runs out.
    /// </summary>
    /// <param name="payloads">The delivered bytes, in delivery order.</param>
    /// <param name="requestId">Our id of the request the delivery answers, named by the timeout failures; <c>null</c> for a live push.</param>
    /// <param name="ct">The caller's cancellation; it alone makes the call throw.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal Task<IReadOnlyList<DeliveredOutcome>> UnpackAllAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<IReadOnlyList<DeliveredOutcome>>(ct);
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
            _budget = new GuardedDeadline(owner.Time, owner.Budget, ct);
            // Read once: the token stays readable after the deadline is disposed, its source does not.
            _budgetToken = _budget.Token;
        }

        internal Task<IReadOnlyList<DeliveredOutcome>> Start()
        {
            // Runs inside the budget's (or the caller's) Cancel(): ours only, it never throws, waits
            // for nothing and runs no continuation inline (the result's run asynchronously).
            _budgetToken.UnsafeRegister(static state => ((Delivery)state!).OnBudgetEnded(), this);
            var workers = Math.Min(FetchConcurrency, _outcomes.Length);
            for (var w = 0; w < workers; w++)
                GuardedDeadline.ObserveAbandoned(RunWorkerAsync());
            return _result.Task;
        }

        // Take the next message, open it, record its outcome; until there is none or the delivery is
        // closed. No exception leaves it: a bad message is an outcome, the budget's end and the
        // caller's cancellation are OnBudgetEnded's to record.
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
                Task<DeliveredUnpack> work;
                try
                {
                    work = _owner.Unpack(packed, slotToken);
                }
                catch (OperationCanceledException) when (_budgetToken.IsCancellationRequested)
                {
                    ReleaseSlot(slot);
                    return; // the budget ended (or the caller cancelled): OnBudgetEnded records it
                }
                catch (OperationCanceledException) when (slotToken.IsCancellationRequested)
                {
                    ReleaseSlot(slot);
                    if (!Complete(index, OutOfShare(_requestId, _share)))
                        return;
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    continue;
                }
                catch (Exception ex)
                {
                    slot.Dispose();
                    Complete(index, DeliveredOutcome.Failure(ex));
                    continue;
                }
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
                    // Never start the next message on the share timer's own thread.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    continue;
                }
                catch (Exception ex)
                {
                    outcome = DeliveredOutcome.Failure(ex);
                }
                slot.Dispose();
                if (!Complete(index, outcome))
                    return;
            }
        }

        // A share deadline that fired (or whose budget fired) may be running its own callbacks on
        // this very thread: it is disposed later, on the pool, never inside its own Cancel().
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
            if (last)
            {
                _result.TrySetResult(_outcomes);
                _budget.Dispose();
            }
            return true;
        }

        // The budget ran out, or the caller cancelled (the budget is linked to its token): every
        // message without an outcome is out of time — started or not — and the unpacks still
        // running are let go, keeping their abandoned slots until they end.
        private void OnBudgetEnded()
        {
            List<Task>? running = null;
            bool cancelled;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                cancelled = _ct.IsCancellationRequested;
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
            else
                _result.TrySetResult(_outcomes);
            // Not inside the Cancel() that is running this callback: later, on the pool.
            ThreadPool.UnsafeQueueUserWorkItem(static budget => budget.Dispose(), _budget, preferLocal: false);
        }
    }
}
