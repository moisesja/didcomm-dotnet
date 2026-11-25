using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Which outcomes of the unpacker are inconclusive (#111, <see cref="DeliveredOutcome.Inconclusive"/>,
/// the source of <see cref="PickupItem.Inconclusive"/>; the coordinator's call): a failed message
/// that this delivery did not count against. The flag says what the memory says: it is true exactly
/// when the message is not malformed, was not held back, and no strike was taken for it. One row
/// per kind of outcome the unpacker makes; each row builds its kind on the injected clock, checks by
/// the failure's type and text that it built that kind, reads the flag, and reads the memory. Then:
/// that an inconclusive message is started again at the next delivery, that the decision at the
/// budget's end runs no host code under the delivery's lock and publishes no outcome before it is
/// made, what a host clock that throws there leaves, and the one window in which a message is
/// inconclusive although its host code is then still called. Real time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackInconclusiveTests
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private const string MalloryKid = "did:example:mallory#key-x25519-1";
    private const string Target = "target";
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>The kinds of outcome the unpacker makes for one delivered message.</summary>
    public enum Kind
    {
        /// <summary>It opened.</summary>
        Opened,

        /// <summary>Its host call failed while the delivery was not being ended (S1).</summary>
        FailedOnItsOwn,

        /// <summary>Its work item failed before its code started: the host's clock threw as the work item read it.</summary>
        FailedBeforeItsCodeStarted,

        /// <summary>Its host call failed on its own, and the host's clock threw in the memory before the strike was made.</summary>
        FailedOnItsOwnAndTheClockThrewInTheMemory,

        /// <summary>Its code started with the budget and was running when the budget ended; it ended with its token (S2).</summary>
        OutOfTimeHavingHadTheWholeBudget,

        /// <summary>Its code started with the budget and was running when the budget ended; it ignores its token (S2).</summary>
        OutOfTimeHavingHadTheWholeBudgetItsLookupIgnoringItsToken,

        /// <summary>Its code started exactly the start allowance into the budget and was running when the budget ended (S2, at its boundary).</summary>
        OutOfTimeHavingStartedAtTheStartAllowance,

        /// <summary>Its code started one tick later than the start allowance and was running when the budget ended.</summary>
        OutOfTimeHavingStartedOneTickAfterTheStartAllowance,

        /// <summary>Its code started fifteen seconds into the budget, when the messages in front of it had failed on their own, and was running when the budget ended.</summary>
        OutOfTimeHavingStartedLateBehindOthers,

        /// <summary>Its code was running when the budget ended, and its start reading is below the budget's (the host's clock stepped back).</summary>
        OutOfTimeItsStartReadBelowTheBudgetsStart,

        /// <summary>Its host call was created, and the budget ended on the pool thread before the call's code started: the unpack step was never called for it.</summary>
        OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted,

        /// <summary>A worker had taken it, and the budget ended before its host call was created.</summary>
        OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated,

        /// <summary>No worker took it: the messages in front of it kept every worker until the budget ended.</summary>
        OutOfTimeBeforeAnyWorkerTookIt,

        /// <summary>A worker took it with less than the floor of the budget left (the start rule).</summary>
        TakenWithLessThanTheFloorLeft,

        /// <summary>Refused: its sender's host calls in this mailbox were at their bound.</summary>
        RefusedForItsSendersBound,

        /// <summary>Refused: the host calls of this mailbox's messages that name no sender were at their bound.</summary>
        RefusedForTheBoundOfMessagesNamingNoSender,

        /// <summary>Refused: the container's host calls were at their bound.</summary>
        RefusedForTheContainersBound,

        /// <summary>Held back: it did not open when it was delivered before.</summary>
        HeldBack,

        /// <summary>Its bytes are not UTF-8.</summary>
        NotUtf8,

        /// <summary>It has no bytes.</summary>
        Empty,
    }

    /// <summary>
    /// For every kind of outcome: inconclusive when the budget ran out before the message's turn (no
    /// worker took it; the start rule turned it away; the delivery ended before its host call was
    /// created, or before that call's code started), when it was refused for capacity, when its code
    /// started later than the start allowance (or below the budget's start) and was cut at the
    /// budget's end, and when the host's clock threw where its failure would have been counted.
    /// Conclusive for every other kind: opened, failed on its own, cut having had the whole budget,
    /// held back, malformed. Each failed message that is not held back or malformed is struck in the
    /// memory exactly when its outcome is conclusive (invariant a), and a message out of time says
    /// "while it was being opened" exactly when the unpack step was called for it, "before its turn
    /// came" otherwise (invariant e).
    /// </summary>
    /// <remarks>
    /// The row <see cref="Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted"/> pinned
    /// <c>false</c> and "while it was being opened" in fix round 3, when a message counted as tried
    /// from the moment its host call was created. Since the coordinator's call that the flag follows
    /// the memory, a call whose code had not started when the budget ended reads as "before its turn
    /// came" and is inconclusive: no host code ran for it.
    /// </remarks>
    [Theory]
    [InlineData(Kind.Opened, false)]
    [InlineData(Kind.FailedOnItsOwn, false)]
    [InlineData(Kind.FailedBeforeItsCodeStarted, true)]
    [InlineData(Kind.FailedOnItsOwnAndTheClockThrewInTheMemory, true)]
    [InlineData(Kind.OutOfTimeHavingHadTheWholeBudget, false)]
    [InlineData(Kind.OutOfTimeHavingHadTheWholeBudgetItsLookupIgnoringItsToken, false)]
    [InlineData(Kind.OutOfTimeHavingStartedAtTheStartAllowance, false)]
    [InlineData(Kind.OutOfTimeHavingStartedOneTickAfterTheStartAllowance, true)]
    [InlineData(Kind.OutOfTimeHavingStartedLateBehindOthers, true)]
    [InlineData(Kind.OutOfTimeItsStartReadBelowTheBudgetsStart, true)]
    [InlineData(Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted, true)]
    [InlineData(Kind.OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated, true)]
    [InlineData(Kind.OutOfTimeBeforeAnyWorkerTookIt, true)]
    [InlineData(Kind.TakenWithLessThanTheFloorLeft, true)]
    [InlineData(Kind.RefusedForItsSendersBound, true)]
    [InlineData(Kind.RefusedForTheBoundOfMessagesNamingNoSender, true)]
    [InlineData(Kind.RefusedForTheContainersBound, true)]
    [InlineData(Kind.HeldBack, false)]
    [InlineData(Kind.NotUtf8, false)]
    [InlineData(Kind.Empty, false)]
    public async Task An_outcome_is_inconclusive_exactly_when_this_delivery_did_not_count_against_its_message(Kind kind, bool inconclusive)
    {
        var scenario = await BuildAsync(kind);
        try
        {
            var outcome = scenario.Outcome;

            outcome.Inconclusive.Should().Be(inconclusive, "{0}", kind);
            if (kind is not (Kind.Opened or Kind.HeldBack or Kind.NotUtf8 or Kind.Empty))
            {
                scenario.Unpacker.Memory.HeldBack(Bob, new[] { Target }).Single().Should().Be(!inconclusive,
                    "a strike was taken for the message exactly when its outcome is conclusive ({0})", kind);
            }
            if (outcome.Error is TimeoutException outOfTime && outOfTime.Message.Contains("ran out"))
            {
                var called = scenario.Calls.Labels.Contains(Target);
                outOfTime.Message.Contains("while it was being opened").Should().Be(called, "the text says whether the unpack step was called for it ({0})", kind);
                outOfTime.Message.Contains("before its turn came").Should().Be(!called, "{0}", kind);
            }
        }
        finally
        {
            scenario.End();
        }
    }

    /// <summary>
    /// Invariant (b), for every inconclusive kind: once what was in the message's way is gone (the
    /// lookups that held a bound have ended), the next delivery starts it: the unpack step is called
    /// for it, and it is not held back.
    /// </summary>
    [Theory]
    [InlineData(Kind.FailedBeforeItsCodeStarted)]
    [InlineData(Kind.FailedOnItsOwnAndTheClockThrewInTheMemory)]
    [InlineData(Kind.OutOfTimeHavingStartedOneTickAfterTheStartAllowance)]
    [InlineData(Kind.OutOfTimeHavingStartedLateBehindOthers)]
    [InlineData(Kind.OutOfTimeItsStartReadBelowTheBudgetsStart)]
    [InlineData(Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted)]
    [InlineData(Kind.OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated)]
    [InlineData(Kind.OutOfTimeBeforeAnyWorkerTookIt)]
    [InlineData(Kind.TakenWithLessThanTheFloorLeft)]
    [InlineData(Kind.RefusedForItsSendersBound)]
    [InlineData(Kind.RefusedForTheBoundOfMessagesNamingNoSender)]
    [InlineData(Kind.RefusedForTheContainersBound)]
    public async Task An_inconclusive_message_is_started_again_at_the_next_delivery_once_what_was_in_its_way_is_gone(Kind kind)
    {
        var scenario = await BuildAsync(kind);
        try
        {
            scenario.Outcome.Inconclusive.Should().BeTrue("{0}", kind);
            scenario.Free();
            SpinWait.SpinUntil(() => scenario.Unpacker.Reserved == 0, RealTime).Should().BeTrue("every lookup of the first delivery has ended");
            var before = scenario.Calls.Labels.Count(l => l == Target);

            var next = scenario.Unpacker.UnpackAllAsync(Bob, new[] { scenario.TargetMessage }, "request-next", default);

            SpinWait.SpinUntil(() => scenario.Calls.Labels.Count(l => l == Target) == before + 1, RealTime).Should().BeTrue("the unpack step is called for it at the next delivery ({0})", kind);
            if (!next.IsCompleted)
                scenario.Time.Advance(Budget);
            var again = (await next.WaitAsync(RealTime)).Single();
            (again.Error?.Message ?? string.Empty).Should().NotContain("did not open when it was delivered before", "it was not held back");
        }
        finally
        {
            scenario.End();
        }
    }

    // ---- the decision at the budget's end ---------------------------------------------------------

    /// <summary>
    /// Invariant (c): while the budget's end decides the outcomes, every reading of the host's clock
    /// (the arithmetic of the start allowance asks the clock for its frequency; the memory reads its
    /// timestamp) is made outside the delivery's lock. A second message's worker is held at its own
    /// start, and let go from inside each such reading in turn; it then needs the delivery's lock to
    /// find the delivery closed, and it gets it while the reading is still in progress.
    /// </summary>
    [Fact]
    public async Task While_the_budgets_end_decides_the_outcomes_every_reading_of_the_hosts_clock_is_made_outside_the_deliverys_lock()
    {
        var readings = 0;
        for (var reading = 1; reading <= 8; reading++)
        {
            var (reached, lockWasFree, targetCalled) = await EndWithAWorkerWaitingForTheLockAsync(reading);
            if (!reached)
                break;
            lockWasFree.Should().BeTrue("the worker took the delivery's lock while reading {0} of the host's clock was in progress", reading);
            targetCalled.Should().BeFalse("the delivery was closed when the worker reached it");
            readings++;
        }

        readings.Should().BeGreaterThanOrEqualTo(2, "the budget's end reads the clock's frequency and its timestamp for the message that had the whole budget");
    }

    // One delivery of two messages on a thread of its own. The first starts with the budget and
    // never opens. The second's worker is parked in its start-rule reading (the reading itself was
    // taken before, so the start rule lets it through). The budget is ended on another thread; at
    // that thread's `reading`-th reading of the host's clock the worker is let go, and the reading
    // waits (bounded) until the call that opened the delivery has returned, which the worker's pass
    // through the delivery's lock precedes.
    private static async Task<(bool Reached, bool LockWasFree, bool TargetCalled)> EndWithAWorkerWaitingForTheLockAsync(int reading)
    {
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);
        using var workerParked = new ManualResetEventSlim();
        using var workerLetGo = new ManualResetEventSlim();
        using var opened = new ManualResetEventSlim();
        int openingThread = -1, endingThread = -1, timerCreated = 0, readsWhileOpening = 0, readingsWhileEnding = 0;
        var reached = false;
        var lockWasFree = false;
        clock.AfterTimerCreated = n =>
        {
            if (n == 1)
                Volatile.Write(ref timerCreated, 1);
        };
        void ReadingWhileEnding()
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref endingThread) || Interlocked.Increment(ref readingsWhileEnding) != reading)
                return;
            reached = true;
            workerLetGo.Set();
            lockWasFree = opened.Wait(TimeSpan.FromSeconds(5));
        }
        clock.OnFrequency = ReadingWhileEnding;
        clock.AfterTimestampRead = () =>
        {
            // The opening thread's second reading after the budget's timer exists is the second
            // message's start rule.
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref openingThread) && Volatile.Read(ref timerCreated) == 1 && Interlocked.Increment(ref readsWhileOpening) == 2)
            {
                workerParked.Set();
                workerLetGo.Wait(RealTime);
                return;
            }
            ReadingWhileEnding();
        };
        Task<IReadOnlyList<DeliveredOutcome>>? delivery = null;
        var opening = OnAThread(() =>
        {
            Volatile.Write(ref openingThread, Environment.CurrentManagedThreadId);
            delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon("front"), From(MalloryKid, Target) }, "request-1", default);
            opened.Set();
        });
        workerParked.Wait(RealTime).Should().BeTrue("the second message's worker is at its start rule");
        await calls.WaitForAsync(1, RealTime);

        var ending = OnAThread(() =>
        {
            Volatile.Write(ref endingThread, Environment.CurrentManagedThreadId);
            inner.Advance(Budget);
        });
        ending.Join(RealTime).Should().BeTrue("the budget's end returned");
        workerLetGo.Set(); // when the reading was not reached
        opening.Join(RealTime).Should().BeTrue("the call that opened the delivery returned");
        var outcomes = await delivery!.WaitAsync(RealTime);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();

        outcomes.Should().HaveCount(2);
        return (reached, lockWasFree, calls.Labels.Contains(Target));
    }

    /// <summary>
    /// No outcome can be read before its flag is final: an outcome is created with its flag, and the
    /// delivery's result is not set while the budget's end is still deciding. The decision is held
    /// inside a reading of the host's clock (the first message's, the second's, the last one's
    /// strike), and the delivery's task is not complete then; once let go, every outcome has the flag
    /// the memory agrees with.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task The_deliverys_result_is_not_set_while_the_budgets_end_is_still_deciding_its_outcomes(int heldAtReading)
    {
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);
        using var held = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();
        int endingThread = -1, readings = 0;
        void ReadingWhileEnding()
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref endingThread) && Interlocked.Increment(ref readings) == heldAtReading)
            {
                held.Set();
                letGo.Wait(RealTime);
            }
        }
        clock.OnFrequency = ReadingWhileEnding;
        clock.AfterTimestampRead = ReadingWhileEnding;
        var ids = new[] { "a", "b", "c" };

        var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(Anon).ToArray(), "request-1", default);
        await calls.WaitForAsync(3, RealTime);
        var ending = OnAThread(() =>
        {
            Volatile.Write(ref endingThread, Environment.CurrentManagedThreadId);
            inner.Advance(Budget);
        });
        held.Wait(RealTime).Should().BeTrue("the budget's end is inside reading {0} of the host's clock", heldAtReading);

        delivery.IsCompleted.Should().BeFalse("the result is not set before every outcome has been decided");
        letGo.Set();
        ending.Join(RealTime).Should().BeTrue();
        var outcomes = await delivery.WaitAsync(RealTime);

        Volatile.Read(ref readings).Should().Be(6, "two readings for each of the three: the frequency for the start allowance, and the memory's timestamp");
        outcomes.Should().HaveCount(3).And.OnlyContain(o => !o.Inconclusive && o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
        clock.OnFrequency = null;
        clock.AfterTimestampRead = null;
        unpacker.Memory.HeldBack(Bob, ids).Should().Equal(true, true, true);
    }

    /// <summary>
    /// Invariant (d): a host clock that throws while the budget's end is deciding. Three messages
    /// had the whole budget. The first is struck. The clock then throws at the second one's decision
    /// (in the arithmetic of the start allowance, or in the memory before the strike is made): the
    /// second and the third are inconclusive, nothing more is read from the clock or struck, and the
    /// delivery's result is still set.
    /// </summary>
    [Theory]
    [InlineData(3, "the arithmetic of the start allowance")]
    [InlineData(4, "the memory, before the strike is made")]
    public async Task A_host_clock_that_throws_while_the_budgets_end_is_deciding_leaves_that_message_and_those_after_it_inconclusive_and_the_result_is_still_set(int throwsFromReading, string where)
    {
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);
        int endingThread = -1, readings = 0;
        void ReadingWhileEnding()
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref endingThread) && Interlocked.Increment(ref readings) >= throwsFromReading)
                throw new InvalidOperationException("the host's clock failed");
        }
        clock.OnFrequency = ReadingWhileEnding;
        clock.AfterTimestampRead = ReadingWhileEnding;
        var ids = new[] { "a", "b", "c" };

        var delivery = unpacker.UnpackAllAsync(Bob, ids.Select(Anon).ToArray(), "request-1", default);
        await calls.WaitForAsync(3, RealTime);
        var ending = OnAThread(() =>
        {
            Volatile.Write(ref endingThread, Environment.CurrentManagedThreadId);
            inner.Advance(Budget);
        });
        ending.Join(RealTime).Should().BeTrue();
        var outcomes = await delivery.WaitAsync(RealTime);

        Volatile.Read(ref readings).Should().Be(throwsFromReading, "the clock threw in {0}, and it was not read again", where);
        outcomes.Should().HaveCount(3).And.OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
        outcomes.Select(o => o.Inconclusive).Should().Equal(new[] { false, true, true }, "the first was struck; the clock threw at the second, and nothing was tried for the third");
        clock.OnFrequency = null;
        clock.AfterTimestampRead = null;
        unpacker.Memory.HeldBack(Bob, ids).Should().Equal(true, false, false);
        unpacker.Opening.Should().Be(0);
    }

    /// <summary>
    /// The one window in which a message is inconclusive although its host code is then still
    /// called: its work item had passed its token check and had not yet noted that it ran when the
    /// budget ended. The work item is held in the reading of the host's clock that lies between the
    /// two, the budget is ended, and the work item is let go from inside the budget's end, after the
    /// delivery was closed and while the first message is being struck. What is measured: the
    /// message is out of time "before its turn came", inconclusive, and not remembered; the unpack
    /// step is called for it exactly once, after the delivery closed, with a token that is already
    /// cancelled; the message beside it, which had the whole budget, is struck.
    /// </summary>
    [Fact]
    public async Task A_work_item_past_its_token_check_that_had_not_started_when_the_budget_ended_is_inconclusive_and_not_remembered_and_its_host_code_is_then_called_once_with_its_token_cancelled()
    {
        using var floor = new PoolFloor(parked: 1); // the target's work item holds a pool thread
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var cancelledAtEntry = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            cancelledAtEntry[label] = ct.IsCancellationRequested;
            using (calls.Enter(label, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, clock);
        using var workItemHeld = new ManualResetEventSlim();
        using var workItemLetGo = new ManualResetEventSlim();
        int openingThread = Environment.CurrentManagedThreadId, endingThread = -1, timerCreated = 0, readsWhileOpening = 0, opening = 1, heldOnce = 0, strikes = 0;
        var calledBeforeTheStrikeReturned = false;
        clock.AfterTimerCreated = n =>
        {
            if (n == 1)
                Volatile.Write(ref timerCreated, 1);
        };
        clock.AfterTimestampRead = () =>
        {
            if (Marked.Value == Target)
            {
                // The target's work item: the reading between its token check and its note that it ran.
                if (Interlocked.Exchange(ref heldOnce, 1) == 0)
                {
                    workItemHeld.Set();
                    workItemLetGo.Wait(RealTime);
                }
                return;
            }
            if (Volatile.Read(ref opening) == 1 && Environment.CurrentManagedThreadId == openingThread && Volatile.Read(ref timerCreated) == 1 && Interlocked.Increment(ref readsWhileOpening) == 2)
            {
                Marked.Value = Target; // the target's start rule: flows into its work item only
                return;
            }
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref endingThread) && Interlocked.Increment(ref strikes) == 1)
            {
                // The memory's reading for the first message's strike: the delivery is closed.
                workItemLetGo.Set();
                calledBeforeTheStrikeReturned = SpinWait.SpinUntil(() => calls.Labels.Contains(Target), TimeSpan.FromSeconds(10));
            }
        };

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon("front"), Anon(Target) }, "request-1", default);
        Volatile.Write(ref opening, 0);
        Marked.Value = null;
        workItemHeld.Wait(RealTime).Should().BeTrue("the target's work item passed its token check and is reading the clock");
        await calls.WaitForAsync(1, RealTime);
        calls.Labels.Should().Equal("front");
        var ending = OnAThread(() =>
        {
            Volatile.Write(ref endingThread, Environment.CurrentManagedThreadId);
            inner.Advance(Budget);
        });
        ending.Join(RealTime).Should().BeTrue();
        var outcomes = await delivery.WaitAsync(RealTime);
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("the target's host call ended and gave its reservation back");
        clock.AfterTimestampRead = null;

        calledBeforeTheStrikeReturned.Should().BeTrue("the target's host code was called while the budget's end was still deciding");
        outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        outcomes[0].Inconclusive.Should().BeFalse("the first message had the whole budget");
        outcomes[1].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        outcomes[1].Inconclusive.Should().BeTrue("its code had not started when the delivery closed");
        calls.Labels.Count(l => l == Target).Should().Be(1, "its host code is called once");
        cancelledAtEntry[Target].Should().BeTrue("with a token that is already cancelled");
        cancelledAtEntry["front"].Should().BeFalse();
        unpacker.Memory.HeldBack(Bob, new[] { "front", Target }).Should().Equal(new[] { true, false }, "the target is not struck, though its start reading is within the start allowance");
    }

    // ---- the kinds ------------------------------------------------------------------------------

    private static Task<Scenario> BuildAsync(Kind kind)
        => kind switch
        {
            Kind.Opened => OpenedAsync(),
            Kind.FailedOnItsOwn => FailedOnItsOwnAsync(),
            Kind.FailedBeforeItsCodeStarted => FailedBeforeItsCodeStartedAsync(),
            Kind.FailedOnItsOwnAndTheClockThrewInTheMemory => FailedOnItsOwnAndTheClockThrewInTheMemoryAsync(),
            Kind.OutOfTimeHavingHadTheWholeBudget => OutOfTimeHavingHadTheWholeBudgetAsync(ignoresItsToken: false),
            Kind.OutOfTimeHavingHadTheWholeBudgetItsLookupIgnoringItsToken => OutOfTimeHavingHadTheWholeBudgetAsync(ignoresItsToken: true),
            Kind.OutOfTimeHavingStartedAtTheStartAllowance => StartedIntoTheBudgetAsync(afterTheAllowance: TimeSpan.Zero),
            Kind.OutOfTimeHavingStartedOneTickAfterTheStartAllowance => StartedIntoTheBudgetAsync(afterTheAllowance: TimeSpan.FromTicks(1)),
            Kind.OutOfTimeHavingStartedLateBehindOthers => StartedLateBehindOthersAsync(),
            Kind.OutOfTimeItsStartReadBelowTheBudgetsStart => StartReadBelowTheBudgetsStartAsync(),
            Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted => EndedAtTheTargetsStartAsync(afterItsCallWasCreated: true),
            Kind.OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated => EndedAtTheTargetsStartAsync(afterItsCallWasCreated: false),
            Kind.OutOfTimeBeforeAnyWorkerTookIt => NoWorkerTookItAsync(),
            Kind.TakenWithLessThanTheFloorLeft => TakenWithLessThanTheFloorLeftAsync(),
            Kind.RefusedForItsSendersBound => RefusedAsync(Kind.RefusedForItsSendersBound),
            Kind.RefusedForTheBoundOfMessagesNamingNoSender => RefusedAsync(Kind.RefusedForTheBoundOfMessagesNamingNoSender),
            Kind.RefusedForTheContainersBound => RefusedAsync(Kind.RefusedForTheContainersBound),
            Kind.HeldBack => HeldBackAsync(),
            Kind.NotUtf8 => MalformedAsync(new byte[] { 0xC3, 0x28 }, "not valid UTF-8"),
            Kind.Empty => MalformedAsync(Array.Empty<byte>(), "is empty"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static async Task<Scenario> OpenedAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            return Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed)));
        }, Budget, time);

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();

        outcome.Succeeded.Should().BeTrue();
        return new Scenario(outcome, unpacker, calls, time, Anon(Target));
    }

    private static async Task<Scenario> FailedOnItsOwnAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time);

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();

        outcome.Error.Should().BeOfType<CryptoException>();
        return new Scenario(outcome, unpacker, calls, time, Anon(Target));
    }

    // The host's clock throws as the target's work item reads it, before the unpack step is called:
    // a failure, not of the message's own. (The reading is the first one made in the flow of the
    // target's work item; the mark is set at the target's start rule and flows into that item only.)
    private static async Task<Scenario> FailedBeforeItsCodeStartedAsync()
    {
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            return Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed)));
        }, Budget, clock);
        var timerCreated = 0;
        clock.AfterTimerCreated = n =>
        {
            if (n == 1)
                Volatile.Write(ref timerCreated, 1);
        };
        clock.AfterTimestampRead = () =>
        {
            if (Marked.Value == Target)
                throw new InvalidOperationException("the host's clock failed");
            if (Volatile.Read(ref timerCreated) == 1)
                Marked.Value = Target; // the target's start rule, alone in its delivery
        };
        DeliveredOutcome outcome;
        try
        {
            outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();
        }
        finally
        {
            clock.AfterTimestampRead = null;
            clock.AfterTimerCreated = null;
            Marked.Value = null;
        }

        calls.Count.Should().Be(0, "the unpack step was never called");
        outcome.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("the host's clock failed");
        return new Scenario(outcome, unpacker, calls, inner, Anon(Target));
    }

    // The target fails on its own, and the host's clock throws at the next reading, which is the
    // memory's, before the strike is made.
    private static async Task<Scenario> FailedOnItsOwnAndTheClockThrewInTheMemoryAsync()
    {
        var inner = new ManualTimeProvider();
        var clock = new ProbeClock(inner);
        var calls = new HostCallLog();
        var throwsOnce = 1;
        var throws = 0;
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            if (Interlocked.Exchange(ref throwsOnce, 0) == 1)
                Volatile.Write(ref throws, 1);
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, clock);
        clock.AfterTimestampRead = () =>
        {
            if (Interlocked.Exchange(ref throws, 0) == 1)
                throw new InvalidOperationException("the host's clock failed");
        };

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();

        calls.Labels.Should().Equal(Target);
        Volatile.Read(ref throws).Should().Be(0, "the clock threw at the memory's reading");
        outcome.Error.Should().BeOfType<CryptoException>("the failure is still the message's own");
        unpacker.Memory.Count.Should().Be(0, "the strike was not made");
        unpacker.Opening.Should().Be(0, "the result was set");
        return new Scenario(outcome, unpacker, calls, inner, Anon(Target));
    }

    // Alone in its delivery, its code started with the budget and running when the budget ends.
    private static async Task<Scenario> OutOfTimeHavingHadTheWholeBudgetAsync(bool ignoresItsToken)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(NeverOpens.Lookup(ignoresItsToken ? WhenCancelled.RunsOn : WhenCancelled.Ends, calls, new RegistrationGate(), never.Task), Budget, time);
        var scenario = new Scenario(null!, unpacker, calls, time, Anon(Target)) { Free = () => never.TrySetException(new CryptoException("JWE could not be decrypted.")) };
        try
        {
            var delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default);
            await calls.WaitForAsync(1, RealTime);
            time.Advance(Budget);
            var outcome = (await delivery.WaitAsync(RealTime)).Single();

            calls.Labels.Should().Equal(Target);
            outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
            return scenario.With(outcome);
        }
        catch
        {
            scenario.End();
            throw;
        }
    }

    // Alone in its delivery, its code starting the start allowance (plus `afterTheAllowance`) into
    // the budget: the clock moves on between the budget's start and the start of its host call, as
    // when the call waits for a thread. Running when the budget ends.
    private static async Task<Scenario> StartedIntoTheBudgetAsync(TimeSpan afterTheAllowance)
    {
        var allowance = TimeSpan.FromSeconds(2.5);
        var inner = new ManualTimeProvider();
        var clock = UnpackClock.SteppingAfterTheFirstBudget(inner, allowance + afterTheAllowance);
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);
        unpacker.StartAllowance.Should().Be(allowance, "half the floor");
        unpacker.StartAllowance.Should().Be(Budget / 8, "an eighth of the budget");

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        inner.Advance(Budget - allowance - afterTheAllowance);
        var outcome = (await delivery.WaitAsync(RealTime)).Single();

        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        return new Scenario(outcome, unpacker, calls, inner, Anon(Target));
    }

    // Behind as many messages as are opened at once, each failing on its own fifteen seconds into
    // the budget: the target starts then, later than the start allowance, and is running when the
    // budget ends five seconds later. (The failures are timed by timers of the manual clock.)
    private static async Task<Scenario> StartedLateBehindOthersAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var lateStart = TimeSpan.FromSeconds(15);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("front-", StringComparison.Ordinal))
            {
                var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = time.CreateTimer(_ => due.TrySetResult(), null, lateStart, Timeout.InfiniteTimeSpan);
                using (calls.Enter(label, ct))
                    await due.Task;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(label, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var fronts = Enumerable.Range(0, AtOnce).Select(i => $"front-{i}").ToArray();
        var messages = fronts.Select((id, i) => From($"did:example:sender-{i}#key-1", id)).Append(Anon(Target)).ToArray();
        lateStart.Should().BeGreaterThan(unpacker.StartAllowance).And.BeLessThanOrEqualTo(Budget - unpacker.Floor);

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(AtOnce, RealTime);
        time.Advance(lateStart);
        SpinWait.SpinUntil(() => unpacker.Settled == AtOnce, RealTime).Should().BeTrue("each message in front failed on its own, and its worker recorded that");
        await calls.WaitForAsync(AtOnce + 1, RealTime);
        calls.Labels[^1].Should().Be(Target, "it started when the messages in front had failed");
        time.Advance(Budget - lateStart);
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes.Take(AtOnce).Should().OnlyContain(o => o.Error is CryptoException && !o.Inconclusive, "the messages in front failed on their own");
        unpacker.Memory.HeldBack(Bob, fronts).Should().OnlyContain(h => h, "and were struck");
        outcomes[AtOnce].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        return new Scenario(outcomes[AtOnce], unpacker, calls, time, Anon(Target));
    }

    // Alone in its delivery; the host's clock steps back one second between the budget's start and
    // the start of its host call, so its start reading is below the budget's. Running when the
    // budget ends.
    private static async Task<Scenario> StartReadBelowTheBudgetsStartAsync()
    {
        var inner = new ManualTimeProvider();
        var clock = new UnpackClock(inner);
        clock.AfterTimerCreated = n =>
        {
            if (n == 1)
                clock.Shift(-TimeSpan.FromSeconds(1));
        };
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        inner.Advance(Budget);
        var outcome = (await delivery.WaitAsync(RealTime)).Single();

        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        return new Scenario(outcome, unpacker, calls, inner, Anon(Target));
    }

    // The boundary between "its code had started" and "it had not": the budget ends at the target's
    // start, alone in its delivery, in one of two windows. Before its call is created: while its
    // worker reads the clock for the start rule, which is the first reading after the budget's timer
    // was created. After its call is created: on the pool thread that runs its work item, after the
    // task's own cancellation check and before the call's code starts. In both the unpack step is
    // never called for it.
    private static async Task<Scenario> EndedAtTheTargetsStartAsync(bool afterItsCallWasCreated)
    {
        var clock = new CountingTimeProvider(new ManualTimeProvider());
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, clock);
        var ended = 0;
        Exception? endError = null;
        void EndTheBudget()
        {
            try
            {
                clock.Inner.Advance(Budget);
            }
            catch (Exception ex)
            {
                endError = ex;
            }
            Volatile.Write(ref ended, 1);
        }

        var atTheTargetsTake = 0;
        clock.OnCreate = _ => Interlocked.CompareExchange(ref atTheTargetsTake, 1, 0);
        clock.OnRead = () =>
        {
            if (Interlocked.CompareExchange(ref atTheTargetsTake, 2, 1) != 1)
                return;
            if (afterItsCallWasCreated)
                Marked.Value = Target; // flows into the target's work item only
            else
                EndTheBudget();
        };
        if (afterItsCallWasCreated)
            Volatile.Write(ref _atNextRestore, EndTheBudget);
        Task<IReadOnlyList<DeliveredOutcome>> delivery;
        try
        {
            delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default);
        }
        finally
        {
            clock.OnCreate = null;
            clock.OnRead = null;
            Marked.Value = null;
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, RealTime).Should().BeTrue("the budget ended in its window");
        var outcome = (await delivery.WaitAsync(RealTime)).Single();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0, RealTime).Should().BeTrue("every reservation came back");

        endError.Should().BeNull();
        Volatile.Read(ref atTheTargetsTake).Should().Be(2, "the window was reached at the target's take");
        Volatile.Read(ref _atNextRestore).Should().BeNull("the armed ending was used");
        calls.Count.Should().Be(0, "the unpack step was never called for it, in either window");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came", "no host code ran for it, in either window");
        return new Scenario(outcome, unpacker, calls, clock.Inner, Anon(Target));
    }

    // Behind as many messages that never open as are opened at once: no worker takes it.
    private static async Task<Scenario> NoWorkerTookItAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(StallUntilCancelled(calls), Budget, time);
        var messages = Enumerable.Range(0, AtOnce).Select(i => From($"did:example:sender-{i}#key-1", $"front-{i}")).Append(Anon(Target)).ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(AtOnce, RealTime);
        time.Advance(Budget);
        var outcome = (await delivery.WaitAsync(RealTime))[AtOnce];

        calls.Labels.Should().NotContain(Target);
        unpacker.Settled.Should().Be(0, "the delivery's end recorded every outcome, the target's included");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        return new Scenario(outcome, unpacker, calls, time, Anon(Target));
    }

    // Behind as many messages as are opened at once, the first of which fails on its own one tick
    // after the last instant a message may start: its worker takes the target then, and the start
    // rule turns it away. (The failure is timed by a timer of the manual clock: Task.Delay rounds to
    // milliseconds.)
    private static async Task<Scenario> TakenWithLessThanTheFloorLeftAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        DeliveredMessageUnpacker? unpacker = null;
        unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label == "front-0")
            {
                var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var timer = time.CreateTimer(_ => due.TrySetResult(), null, Budget - unpacker!.Floor + TimeSpan.FromTicks(1), Timeout.InfiniteTimeSpan);
                using (calls.Enter(label, ct))
                    await due.Task;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(label, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var messages = Enumerable.Range(0, AtOnce).Select(i => From($"did:example:sender-{i}#key-1", $"front-{i}")).Append(Anon(Target)).ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(AtOnce, RealTime);
        time.Advance(Budget - unpacker.Floor + TimeSpan.FromTicks(1));
        SpinWait.SpinUntil(() => unpacker.Settled == 2, RealTime).Should().BeTrue("the first message failed on its own, and its worker recorded the target's outcome when it took it");
        calls.Labels.Should().NotContain(Target);
        time.Advance(unpacker.Floor);
        var outcome = (await delivery.WaitAsync(RealTime))[AtOnce];

        calls.Count.Should().Be(AtOnce, "the target's host code never started");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        return new Scenario(outcome, unpacker, calls, time, Anon(Target));
    }

    // Lookups of earlier deliveries that ignore their tokens hold a bound's reservations after their
    // deliveries ended; the target, which would open, comes in a delivery of its own and is refused.
    private static async Task<Scenario> RefusedAsync(Kind kind)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            return label == Target ? Task.FromResult(UnpackFakes.Opened(label)) : never.Task;
        }, Budget, time);
        var scenario = new Scenario(null!, unpacker, calls, time, Anon(Target)) { Free = () => never.TrySetException(new CryptoException("JWE could not be decrypted.")) };
        try
        {
            DeliveredInput target;
            string expected;
            switch (kind)
            {
                case Kind.RefusedForItsSendersBound:
                    await HoldAsync(unpacker, time, calls, new[] { Enumerable.Range(0, UnpackReservations.MaxPerSender).Select(i => From(MalloryKid, $"held-{i}")).ToArray() });
                    target = From(MalloryKid, Target);
                    expected = "for its sender";
                    break;
                case Kind.RefusedForTheBoundOfMessagesNamingNoSender:
                    await HoldAsync(unpacker, time, calls, new[] { Enumerable.Range(0, UnpackReservations.MaxPerAnonymous).Select(i => Anon($"held-{i}")).ToArray() });
                    target = Anon(Target);
                    expected = "without a named sender";
                    break;
                default:
                    // The container's bound, filled by as many deliveries as it takes, each of as many
                    // messages as are opened at once, every message from a sender of its own.
                    await HoldAsync(unpacker, time, calls, Enumerable.Range(0, UnpackReservations.MaxReserved / AtOnce)
                        .Select(d => Enumerable.Range(0, AtOnce).Select(i => From($"did:example:sender-{d}-{i}#key-1", $"held-{d}-{i}")).ToArray()).ToArray());
                    unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved);
                    target = From(MalloryKid, Target);
                    expected = "all mailboxes together";
                    break;
            }

            var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { target }, "request-2", default).WaitAsync(RealTime)).Single();

            calls.Labels.Should().NotContain(Target, "a refused message's host code does not start");
            outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted").And.Contain(expected);
            return scenario.With(outcome, target);
        }
        catch
        {
            scenario.End();
            throw;
        }
    }

    // Open the deliveries together and end them at their budget; their lookups ignore their tokens,
    // so each keeps its reservation.
    private static async Task HoldAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, HostCallLog calls, DeliveredInput[][] deliveries)
    {
        var held = deliveries.Sum(d => d.Length);
        var opening = deliveries.Select(d => unpacker.UnpackAllAsync(Bob, d, "request-1", default)).ToArray();
        await calls.WaitForAsync(held, RealTime);
        time.Advance(Budget);
        var outcomes = (await Task.WhenAll(opening).WaitAsync(RealTime)).SelectMany(o => o).ToArray();

        outcomes.Should().HaveCount(held).And.OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened"));
        unpacker.Reserved.Should().Be(held, "a lookup that ignores its token keeps its reservation after its delivery ended");
    }

    private static async Task<Scenario> HeldBackAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, time);
        (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single().Error.Should().BeOfType<CryptoException>();

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-2", default).WaitAsync(RealTime)).Single();

        calls.Count.Should().Be(1, "it is not opened again while it is held back");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("did not open when it was delivered before");
        return new Scenario(outcome, unpacker, calls, time, Anon(Target));
    }

    private static async Task<Scenario> MalformedAsync(byte[] bytes, string expected)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(packed, ct);
            return Task.FromResult(UnpackFakes.Opened(packed));
        }, Budget, time);

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { new DeliveredInput(Target, bytes) }, "request-1", default).WaitAsync(RealTime)).Single();

        calls.Count.Should().Be(0);
        outcome.Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain(expected);
        return new Scenario(outcome, unpacker, calls, time, new DeliveredInput(Target, bytes));
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>One built kind: the target's outcome, and what the next delivery of the target needs.</summary>
    private sealed class Scenario(DeliveredOutcome outcome, DeliveredMessageUnpacker unpacker, HostCallLog calls, ManualTimeProvider time, DeliveredInput targetMessage)
    {
        public DeliveredOutcome Outcome { get; } = outcome;

        public DeliveredMessageUnpacker Unpacker { get; } = unpacker;

        public HostCallLog Calls { get; } = calls;

        /// <summary>The manual clock the unpacker's timers run on.</summary>
        public ManualTimeProvider Time { get; } = time;

        public DeliveredInput TargetMessage { get; } = targetMessage;

        /// <summary>Ends what the kind left in the target's way: the lookups that ignore their tokens.</summary>
        public Action Free { get; init; } = static () => { };

        public Scenario With(DeliveredOutcome outcome, DeliveredInput? targetMessage = null)
            => new(outcome, Unpacker, Calls, Time, targetMessage ?? TargetMessage) { Free = Free };

        /// <summary>Lets every lookup end and waits for the reservations.</summary>
        public void End()
        {
            Free();
            if (Time.ScheduledTimers > 0)
                Time.Advance(Budget);
            SpinWait.SpinUntil(() => Unpacker.Reserved == 0, RealTime).Should().BeTrue("the lookups ended and gave their reservations back");
        }
    }

    /// <summary>
    /// A host clock over a manual one whose members a test can hold or fail: a hook runs after each
    /// timestamp was read (the reading is what it was before the hook), when the frequency is asked
    /// for (the arithmetic of elapsed time), and after a timer was created.
    /// </summary>
    private sealed class ProbeClock(ManualTimeProvider inner) : TimeProvider
    {
        private int _timers;

        public Action? AfterTimestampRead { get; set; }

        public Action? OnFrequency { get; set; }

        public Action<int>? AfterTimerCreated { get; set; }

        public override long TimestampFrequency
        {
            get
            {
                OnFrequency?.Invoke();
                return inner.TimestampFrequency;
            }
        }

        public override long GetTimestamp()
        {
            var reading = inner.GetTimestamp();
            AfterTimestampRead?.Invoke();
            return reading;
        }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            AfterTimerCreated?.Invoke(Interlocked.Increment(ref _timers));
            return timer;
        }
    }

    private static Func<string, CancellationToken, Task<DeliveredUnpack>> StallUntilCancelled(HostCallLog calls)
        => async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        };

    private static Thread OnAThread(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true };
        thread.Start();
        return thread;
    }

    private static DeliveredInput Anon(string label) => new(label, TestJwe.Bytes(TestJwe.Anoncrypt(label)));

    private static DeliveredInput From(string kid, string label) => new(label, TestJwe.Bytes(TestJwe.Authcrypt(kid, label)));

    private static readonly AsyncLocal<string?> Marked = new(OnMarkedContextRestored);
    private static Action? _atNextRestore;

    // Runs when a thread switches to an execution context in which Marked is the target: for the
    // target's host work item, on its pool thread, after the task's own cancellation check and
    // before its delegate runs. The armed action runs once; nothing escapes (an exception here ends
    // the process).
    private static void OnMarkedContextRestored(AsyncLocalValueChangedArgs<string?> args)
    {
        if (!args.ThreadContextChanged || args.CurrentValue != Target)
            return;
        var action = Interlocked.Exchange(ref _atNextRestore, null);
        try
        {
            action?.Invoke();
        }
        catch
        {
            // Recorded by the action itself; never thrown into the runtime's context switch.
        }
    }
}
