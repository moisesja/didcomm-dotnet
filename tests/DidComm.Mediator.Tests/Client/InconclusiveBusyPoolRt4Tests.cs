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
/// the delivery's result, its reservations, and what <see cref="PickupItem.Inconclusive"/> says of
/// a message for which no host code ran: it is inconclusive, out of time "before its turn came",
/// and not remembered (the coordinator's call: the flag follows the memory). Real time is only a
/// hang guard.
/// </summary>
[Collection(BusyPoolRt4Collection.Name)]
public sealed class InconclusiveBusyPoolRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency; // sixteen
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Lease = new MessagePickupOptions().LeaseTimeout; // 60 s

    /// <summary>
    /// With no pool thread free, a delivery of sixteen messages still hands its task back to its
    /// caller, the budget's end (on the manual clock, moved from the test's own thread) sets the
    /// result with every message out of time before its turn and inconclusive, no host code has run
    /// by then or runs afterwards, nothing is remembered, and every reservation is back once the
    /// pool is free again. The clock's readings count from zero, so that the start reading of a
    /// call that never ran (never taken, so zero) is not below the budget's start: such a call is
    /// not struck because its code did not start, not because of its reading.
    /// </summary>
    /// <remarks>
    /// In fix round 3 these items read "while it was being opened" and were not inconclusive: a
    /// message counted as tried once its host call was created. Since the coordinator's call that
    /// the flag follows the memory, a call whose code had not started when the budget ended is out
    /// of time before its turn, and inconclusive.
    /// </remarks>
    [Fact]
    public void With_no_pool_thread_free_a_delivery_of_sixteen_ends_at_its_budget_runs_no_host_code_and_its_reservations_come_back_once_the_pool_is_free()
    {
        OnAThreadOfItsOwn(() =>
        {
            var time = new CountingTimeProvider(new DidComm.Mediator.Tests.Pipeline.ManualTimeProvider(), readingsFromZero: true);
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
                time.Inner.Advance(Qa1.Budget);
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
            delivery.Result.Should().HaveCount(messages.Length).And.OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("before its turn came") && o.Inconclusive, "no host code ran for any of them");
        });
    }

    /// <summary>
    /// A pin of documented behaviour (the coordinator's call: a residual, described with R-13, no
    /// change): sixteen messages of one sender whose host calls got no thread before their
    /// delivery's budget ended, so that none of that sender's host code runs or will run. A call
    /// that never got a thread keeps its reservation until the pool reaches its cancelled work
    /// item: the sixteen reservations are still held when the delivery has ended, and the sender's
    /// next message, in a delivery of its own, is refused for its sender's bound meanwhile, as an
    /// inconclusive item. Once the pool is free every reservation is back, and the delivery after
    /// that starts the message (invariant b).
    /// </summary>
    [Fact]
    public void A_host_call_that_never_got_a_thread_keeps_its_reservation_until_the_pool_reaches_it_and_its_senders_next_message_is_refused_meanwhile_and_started_after()
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
            var nextMessage = Qa1.Message(Qa1.From(kid, "next"), "next");

            Task<IReadOnlyList<DeliveredOutcome>> first, second;
            int reservedAfterTheFirst, hostCalls, parkedStarted, parkedQueued;
            using (var pool = new ParkedPool())
            {
                first = unpacker.UnpackAllAsync(Qa1.Bob, messages, "request-1", default);
                time.Advance(Qa1.Budget);
                first.Wait(RealTime).Should().BeTrue("the budget's end sets the result");
                reservedAfterTheFirst = unpacker.Reserved;
                hostCalls = calls.Count;
                second = unpacker.UnpackAllAsync(Qa1.Bob, new[] { nextMessage }, "request-2", default);
                if (!second.IsCompleted)
                    time.Advance(Qa1.Budget);
                second.Wait(RealTime).Should().BeTrue();
                parkedStarted = pool.Started;
                parkedQueued = pool.Queued;
            }
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every reservation comes back once the pool is free");
            var next = second.Result.Single();
            var hostCallsBeforeTheThird = calls.Count;
            var third = unpacker.UnpackAllAsync(Qa1.Bob, new[] { nextMessage }, "request-3", default);
            third.Wait(RealTime).Should().BeTrue("with the pool free the message opens at once");

            output.WriteLine($"parked work queued in front: {parkedQueued}, of which started: {parkedStarted}; host calls when the first delivery ended: {hostCalls}; reserved after the first delivery ended: {reservedAfterTheFirst}; the sender's next message: {next.Error?.Message}; Inconclusive {next.Inconclusive}; host calls before the third delivery: {hostCallsBeforeTheThird}, after: {calls.Count}");
            parkedStarted.Should().BeLessThan(parkedQueued, "parked work was still queued in front of the host calls");
            hostCalls.Should().Be(0, "none of the sender's host code ran");
            hostCallsBeforeTheThird.Should().Be(0, "and none ran for the cancelled work items when the pool reached them");
            first.Result.Should().OnlyContain(o => o.Inconclusive && o.Error is TimeoutException && o.Error.Message.Contains("before its turn came"));
            reservedAfterTheFirst.Should().Be(UnpackReservations.MaxPerSender, "each call keeps its reservation until the pool reaches its cancelled work item");
            next.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("capacity exhausted", "its sender's bound was still held");
            next.Inconclusive.Should().BeTrue("a refusal for capacity does not count against the message");
            unpacker.Memory.Count.Should().Be(0);
            third.Result.Single().Succeeded.Should().BeTrue("once the reservations are back, the next delivery starts it");
            calls.Labels.Should().Equal("next");
        });
    }

    /// <summary>
    /// One message that opens at once, alone in its fetch, while no pool thread is free until the
    /// budget has ended. No host code runs for it. The item is inconclusive and reads "before its
    /// turn came", nothing is remembered, and the host that discards a failed item unless it is
    /// inconclusive leaves it queued; the first fetch after its lease, with the pool free, returns
    /// it opened.
    /// </summary>
    /// <remarks>
    /// RT4's reproduction, red on 98a32ac ("Expected item.NotAttempted to be True ... but found
    /// False": the item read "while it was being opened", the flag was false, and that host
    /// discarded a message no host code had run for).
    /// </remarks>
    [Fact]
    public void A_message_for_which_no_host_code_ran_before_the_budget_ended_is_inconclusive_and_a_host_that_discards_a_failed_item_unless_it_is_inconclusive_keeps_it_and_receives_it()
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
            var discarded = Qa1.ToDiscard(fetch.Result.Items, Qa1Discard.UnlessInconclusive);
            if (discarded.Length > 0)
                client.AckAsync(FakePickupExchange.M, FakePickupExchange.L, discarded).Wait(RealTime).Should().BeTrue();

            output.WriteLine($"parked work queued in front: {parkedQueued}, of which started: {parkedStarted}; host calls when the budget ended: {hostCallsAtTheEnd}, in all: {exchange.Host.Calls.Count}; the item: {item.Error?.GetType().Name} \"{(item.Error?.Message.Contains("while it was being opened") == true ? "while it was being opened" : item.Error?.Message.Contains("before its turn came") == true ? "before its turn came" : "other")}\", Inconclusive {item.Inconclusive}; discarded by the host: {discarded.Length}; still queued at the mediator: {mediator.Holds("honest")}; remembered: {exchange.Unpacker.Memory.Count}");
            parkedStarted.Should().BeLessThan(parkedQueued, "parked work was still queued in front of the host call");
            hostCallsAtTheEnd.Should().Be(0, "no host code ran for it before the budget ended");
            exchange.Host.Calls.Count.Should().Be(0, "and none after: a call whose token is cancelled before it got a thread never runs");
            item.Unpacked.Should().BeNull();
            item.Inconclusive.Should().BeTrue("no host code ran for it: this delivery did not count against it");
            item.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
            exchange.Unpacker.Memory.Count.Should().Be(0, "nothing was struck");
            mediator.Holds("honest").Should().BeTrue("a host that discards a failed item unless it is inconclusive leaves it queued");

            // With the pool free again: the first fetch after the lease starts it, and it opens.
            Qa1.LookupsEnded(exchange.Unpacker);
            exchange.Clock.Inner.Advance(mediator.NextLeaseEnd()!.Value - exchange.Clock.TrueNow);
            var again = client.FetchAsync(FakePickupExchange.M, FakePickupExchange.L, limit: 10);
            again.Wait(RealTime).Should().BeTrue();
            again.Result.Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull("left queued, it is started at the next delivery");
            exchange.Host.Calls.Labels.Should().Equal("honest");
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
