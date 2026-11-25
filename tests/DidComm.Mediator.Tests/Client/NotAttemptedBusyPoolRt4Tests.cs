using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The tests of this collection park every thread-pool thread of the test process for a moment, so
/// they never run beside another test class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BusyPoolRt4Collection
{
    public const string Name = "RT4: the thread pool has no free thread";
}

/// <summary>
/// RT4 (#111): a delivery whose host calls get no thread before its budget ends. A host call is a
/// thread-pool work item; here every pool thread is parked and more parked work is queued in front
/// of it than the pool has threads, so the call's code does not start while the test holds the
/// pool. The budget is ended on the manual clock, from a thread of the test's own. What is checked:
/// the delivery's result, its reservations, and what <see cref="PickupItem.NotAttempted"/> says of
/// a message for which no host code ran. Real time is only a hang guard.
/// </summary>
[Collection(BusyPoolRt4Collection.Name)]
public sealed class NotAttemptedBusyPoolRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    /// <summary>
    /// Held: with no pool thread free, a delivery of sixteen messages still hands its task back to
    /// its caller, the budget's end (on the manual clock, moved from the test's own thread) sets the
    /// result with every message out of time, no host code has run by then or runs afterwards,
    /// nothing is remembered, and every reservation is back once the pool is free again.
    /// </summary>
    [Fact]
    public void With_no_pool_thread_free_a_delivery_of_sixteen_ends_at_its_budget_runs_no_host_code_and_its_reservations_come_back_once_the_pool_is_free()
    {
        OnAThreadOfItsOwn(() =>
        {
            var time = new DidComm.Mediator.Tests.Pipeline.ManualTimeProvider();
            var calls = new HostCallLog();
            var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
            {
                calls.Enter(TestJwe.LabelOf(packed), ct);
                return Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed)));
            }, Qa1.Budget, time);
            var messages = Enumerable.Range(0, AtOnce).Select(i => Qa1.Message(Qa1.From($"did:example:sender-{i}#key-1", $"m-{i}"), $"m-{i}")).ToArray();

            Task<IReadOnlyList<DeliveredOutcome>> delivery;
            int reservedWhileOpen, hostCalls, parkedStarted, parkedQueued;
            using (var pool = new ParkedPool())
            {
                delivery = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request-1", default);
                reservedWhileOpen = unpacker.Reserved;
                time.Advance(Qa1.Budget);
                delivery.Wait(RealTime).Should().BeTrue("the budget's end sets the result, whatever the pool does");
                hostCalls = calls.Count;
                parkedStarted = pool.Started;
                parkedQueued = pool.Queued;
            }
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every reservation comes back once the pool is free");

            output.WriteLine($"parked work queued in front: {parkedQueued}, of which started: {parkedStarted}; reserved while open: {reservedWhileOpen}; host calls when the budget ended: {hostCalls}, in all: {calls.Count}");
            parkedStarted.Should().BeLessThan(parkedQueued, "parked work was still queued in front of the host calls");
            hostCalls.Should().Be(0, "no host call got a thread");
            calls.Count.Should().Be(0, "a call whose token is cancelled before it got a thread never runs");
            reservedWhileOpen.Should().Be(AtOnce);
            unpacker.Opening.Should().Be(0);
            unpacker.Memory.Count.Should().Be(0, "a message whose code never started is not remembered");
            delivery.Result.Should().HaveCount(messages.Length).And.OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("while it was being opened") && !o.NotAttempted);
        });
    }

    /// <summary>
    /// Reproduction (fails on a415f6d): sixteen messages of one sender whose host calls got no
    /// thread before their delivery's budget ended, so that none of that sender's host code runs or
    /// will run. Expected, from <see cref="PickupItem.NotAttempted"/>'s remarks ("a message refused
    /// for capacity is tried once the openings that were in its way have ended"): nothing is
    /// reserved once the delivery has ended, and the sender's next message, in a delivery of its
    /// own, is not refused for capacity. Observed: the sixteen reservations stay held until the pool
    /// reaches the cancelled work items, and the next message is refused for its sender's bound
    /// meanwhile.
    /// </summary>
    [Fact(Skip = "RT4: whether reservations are held after a delivery ended for host calls that never got a thread; observed: sixteen stay held until the pool reaches the cancelled work items, and the sender's next message is refused for capacity meanwhile")]
    public void Once_a_delivery_has_ended_and_none_of_its_host_calls_ever_ran_nothing_is_reserved_and_its_senders_next_message_is_not_refused()
    {
        OnAThreadOfItsOwn(() =>
        {
            const string kid = "did:example:carol#key-x25519-1";
            var time = new DidComm.Mediator.Tests.Pipeline.ManualTimeProvider();
            var calls = new HostCallLog();
            var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
            {
                calls.Enter(TestJwe.LabelOf(packed), ct);
                return Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed)));
            }, Qa1.Budget, time);
            var messages = Enumerable.Range(0, UnpackReservations.MaxPerSender).Select(i => Qa1.Message(Qa1.From(kid, $"m-{i}"), $"m-{i}")).ToArray();

            Task<IReadOnlyList<DeliveredOutcome>> first, second;
            int reservedAfterTheFirst, hostCalls, parkedStarted, parkedQueued;
            using (var pool = new ParkedPool())
            {
                first = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request-1", default);
                time.Advance(Qa1.Budget);
                first.Wait(RealTime).Should().BeTrue("the budget's end sets the result");
                reservedAfterTheFirst = unpacker.Reserved;
                hostCalls = calls.Count;
                second = unpacker.UnpackAllAsync(Qa1.Bob, new[] { Qa1.Message(Qa1.From(kid, "next"), "next") }, "request-2", default);
                if (!second.IsCompleted)
                    time.Advance(Qa1.Budget);
                second.Wait(RealTime).Should().BeTrue();
                parkedStarted = pool.Started;
                parkedQueued = pool.Queued;
            }
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every reservation comes back once the pool is free");
            var next = second.Result.Single();

            output.WriteLine($"parked work queued in front: {parkedQueued}, of which started: {parkedStarted}; host calls when the first delivery ended: {hostCalls}, in all: {calls.Count}; reserved after the first delivery ended: {reservedAfterTheFirst}; the sender's next message: {next.Error?.Message}; NotAttempted {next.NotAttempted}");
            parkedStarted.Should().BeLessThan(parkedQueued, "parked work was still queued in front of the host calls");
            hostCalls.Should().Be(0, "none of the sender's host code ran");
            calls.Count.Should().Be(0, "and none runs later");
            reservedAfterTheFirst.Should().Be(0, "no host call of the delivery is running or will run");
            (next.Error?.Message ?? string.Empty).Should().NotContain("capacity exhausted", "no opening of its sender is in its way");
        });
    }

    /// <summary>
    /// Reproduction (fails on a415f6d): one message that opens at once, alone in its fetch, while no
    /// pool thread is free until the budget has ended. No host code runs for it. Expected, from
    /// <see cref="PickupItem.NotAttempted"/>'s summary ("the client did not try to open this message
    /// in this delivery, and so knows nothing about it"): the item says not attempted, and the host
    /// that discards a failed item unless that flag is true leaves it queued. Observed: the item is
    /// a failed item, "ran out while it was being opened", with the flag false; that host discards
    /// it, and the message is gone from the mediator's queue without any host code having run for
    /// it.
    /// </summary>
    [Fact(Skip = "RT4: whether an item for which no host code ran says not attempted, and whether a host that discards only what was tried keeps it; observed: the flag is false and that host discards it")]
    public void A_message_for_which_no_host_code_ran_before_the_budget_ended_is_handed_over_as_not_attempted_and_a_host_that_discards_only_what_was_tried_keeps_it()
    {
        OnAThreadOfItsOwn(() =>
        {
            var (exchange, mediator) = SlowSenderFetchQa1Tests.Setup(_ => Qa1Step.Open, Lease);
            mediator.Enqueue("honest", Qa1.From("did:example:alice#key-x25519-1", "honest"));
            var client = exchange.Client();

            Task<PickupBatch> fetch;
            int hostCallsAtTheEnd, parkedStarted, parkedQueued;
            using (var pool = new ParkedPool())
            {
                fetch = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10);
                SpinWait.SpinUntil(() => exchange.Deliveries.Count == 1 && exchange.Deliveries[0].Task is not null, RealTime).Should().BeTrue("the fetch handed its delivery to the unpacker on this thread");
                exchange.Clock.Inner.Advance(Qa1.Budget);
                exchange.Deliveries[0].Task!.Wait(RealTime).Should().BeTrue("the budget's end sets the delivery's result");
                hostCallsAtTheEnd = exchange.Host.Calls.Count;
                parkedStarted = pool.Started;
                parkedQueued = pool.Queued;
            }
            fetch.Wait(RealTime).Should().BeTrue("the fetch returns once the pool is free again");
            var item = fetch.Result.Items.Should().ContainSingle().Which;
            var discarded = Qa1.ToDiscard(fetch.Result.Items, Qa1Discard.WhatWasTried);
            if (discarded.Length > 0)
                client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, discarded).Wait(RealTime).Should().BeTrue();

            output.WriteLine($"parked work queued in front: {parkedQueued}, of which started: {parkedStarted}; host calls when the budget ended: {hostCallsAtTheEnd}, in all: {exchange.Host.Calls.Count}; the item: {item.Error?.GetType().Name} \"{(item.Error?.Message.Contains("while it was being opened") == true ? "while it was being opened" : item.Error?.Message.Contains("before its turn came") == true ? "before its turn came" : "other")}\", NotAttempted {item.NotAttempted}; discarded by the host: {discarded.Length}; still queued at the mediator: {mediator.Holds("honest")}; remembered: {exchange.Unpacker.Memory.Count}");
            parkedStarted.Should().BeLessThan(parkedQueued, "parked work was still queued in front of the host call");
            hostCallsAtTheEnd.Should().Be(0, "no host code ran for it before the budget ended");
            exchange.Host.Calls.Count.Should().Be(0, "and none after: a call whose token is cancelled before it got a thread never runs");
            item.Unpacked.Should().BeNull();
            item.NotAttempted.Should().BeTrue("no host code ran for it: the client knows nothing about it");
            mediator.Holds("honest").Should().BeTrue("a host that discards only what was tried leaves it queued");
        });
    }

    // Runs the body on a thread that is not the pool's, so parking the pool cannot park the test,
    // and so the work a delivery queues from it goes to the pool's shared queue, behind the parked
    // work.
    private static void OnAThreadOfItsOwn(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "RT4 busy pool" };
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(120)).Should().BeTrue("the test's own thread ended");
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    /// Parks the pool: queues far more work items than the pool has (or can add) threads while the
    /// test runs, each parking its thread until this is disposed. The pool's shared queue is first
    /// in, first out, so work queued after these from a thread that is not the pool's does not start
    /// while some of these are still queued (<see cref="Started"/> below <see cref="Queued"/>).
    /// </summary>
    private sealed class ParkedPool : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _started;
        private int _left;

        public ParkedPool()
        {
            ThreadPool.GetMinThreads(out var floor, out _);
            Queued = Math.Max(2048, 4 * (floor + ThreadPool.ThreadCount));
            for (var i = 0; i < Queued; i++)
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((ParkedPool)state!).Park(), this, preferLocal: false);
        }

        public int Queued { get; }

        public int Started => Volatile.Read(ref _started);

        public void Dispose()
        {
            _release.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref _left) == Queued, TimeSpan.FromSeconds(60));
        }

        private void Park()
        {
            Interlocked.Increment(ref _started);
            _release.Wait(TimeSpan.FromSeconds(60)); // bounded, so a red run cannot hang the process
            Interlocked.Increment(ref _left);
        }
    }
}
