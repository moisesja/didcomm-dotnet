using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT2-A for #111 (owner decision A1, "cut only if others wait", with owner decision C): how many
/// messages are cut when their shares end, against how many messages of the delivery wait for a
/// worker. Every timing is driven by the injected <see cref="ManualTimeProvider"/> and rendezvous on
/// host calls; real time is only a hang guard.
/// </summary>
public sealed class ShareCutsRt2ATests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>
    /// Two messages whose shares end at the same instant while one message of their delivery waits
    /// for a worker: one cut frees the worker the waiting message needs, so only one is cut. The
    /// other has nothing waiting for it and keeps the rest of the budget (A1): it opens, and it is
    /// not remembered.
    /// </summary>
    /// <remarks>
    /// Six messages, so the first two of the four taken at once have half the budget (10 s) and the
    /// other two all of it. The third fails at once and its worker takes the fifth, so when the first
    /// two shares end only the sixth waits. The first ignores its token; the second needs 12 s and
    /// opens. The second share's timer runs before the first cut's worker has taken the sixth, as
    /// when the timers that come due together fire in one batch.
    /// </remarks>
    [Fact(Skip = "RT2 defect: two shares that end together with one message waiting are both cut")]
    public async Task Two_shares_that_end_together_with_one_message_waiting_cut_only_one()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var register = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            switch (label)
            {
                case "x-0":
                    using (calls.Enter(label, ct))
                    {
                        // Registered after its worker began to wait, so it runs first when the
                        // token is cancelled: the other share due at this instant fires now.
                        await register.Task.ConfigureAwait(false);
                        ct.UnsafeRegister(static state => ((ManualTimeProvider)state!).Advance(TimeSpan.Zero), time);
                        registered.TrySetResult();
                        return await never.Task.ConfigureAwait(false); // ignores its token
                    }
                case "slow-1":
                    var lookup = Task.Delay(TimeSpan.FromSeconds(12), time, ct); // honours its token
                    using (calls.Enter(label, ct))
                        await lookup.ConfigureAwait(false);
                    return UnpackFakes.Opened(label);
                case "fast-2":
                    throw new CryptoException("JWE could not be decrypted.");
                case "open-5":
                    using (calls.Enter(label, ct))
                        return UnpackFakes.Opened(label);
                default:
                    using (calls.Enter(label, ct))
                        return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
            }
        }, Budget, time);
        var labels = new[] { "x-0", "slow-1", "fast-2", "stall-3", "stall-4", "open-5" };
        var messages = labels.Select(l => new DeliveredInput(l, TestJwe.Bytes(TestJwe.Anoncrypt(l)))).ToArray();

        Task<IReadOnlyList<DeliveredOutcome>> batch;
        try
        {
            batch = unpacker.UnpackAllAsync(Bob, messages, "request-1", default); // the four workers wait on their calls
            register.TrySetResult();
            await registered.Task.WaitAsync(RealTime);
            await calls.WaitForAsync(4, RealTime); // x-0, slow-1, stall-3, and stall-4 taken after fast-2 failed
            calls.Labels.Should().Contain(new[] { "slow-1", "stall-3", "stall-4" });
            calls.Labels.Should().NotContain("open-5", "only the sixth waits for a worker");

            time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, labels.Length)); // 10 s: the first two shares end
            TokenOf(calls, "x-0").IsCancellationRequested.Should().BeTrue("x-0's share ended while the sixth waited");
            await calls.WaitForAsync(5, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled >= 3, RealTime).Should().BeTrue("fast-2, x-0's cut and the sixth were recorded");
            TokenOf(calls, "slow-1").IsCancellationRequested.Should().BeFalse("x-0's cut already freed the worker the one waiting message needed");

            time.Advance(TimeSpan.FromSeconds(2)); // 12 s: slow-1's lookup ends
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("slow-1 opened");
            time.Advance(Budget); // the budget's end, for stall-3 and stall-4
            var outcomes = await batch.WaitAsync(RealTime);

            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            outcomes[5].Succeeded.Should().BeTrue("the sixth got the worker x-0's cut freed");
            outcomes[1].Succeeded.Should().BeTrue("nothing else waited for slow-1's worker, so it kept the rest of the budget");
            unpacker.Memory.HeldBack(Bob, new[] { "slow-1" }).Single().Should().BeFalse("a message that opened is not remembered");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Extended == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// I-9 with A1: a message whose code started more than the start allowance after its share was
    /// given, and which is let run past its share because nothing waits, is remembered when the
    /// budget ends it: measured from its code's start it ran more than its share.
    /// </summary>
    [Fact]
    public async Task A_late_starting_message_let_run_past_its_share_is_remembered_at_the_budgets_end()
    {
        var manual = new ManualTimeProvider();
        var late = TimeSpan.FromSeconds(9); // of its 10 s share, more than the 2.5 s allowance
        var time = new StepAfterTimer(manual, timerNumber: 2, step: late); // timer 1 is the budget, 2 the first share
        var (unpacker, calls, never) = Create(time);
        try
        {
            var delivery = unpacker.UnpackAllAsync(Bob, FiveWithFastAfter("stall-0", TestJwe.Anoncrypt("stall-0")), "request-1", default);
            await calls.WaitForAsync(1, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("the four behind it failed at once");
            late.Should().BeGreaterThan(unpacker.StartAllowance);

            manual.Advance(TimeSpan.FromSeconds(1)); // 10 s: its share ends, nothing waits
            TokenOf(calls, "stall-0").IsCancellationRequested.Should().BeFalse("nothing waits, so it keeps running");
            unpacker.Reservations.Extended.Should().Be(1);
            manual.Advance(Budget);
            var outcomes = await delivery.WaitAsync(RealTime);

            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
            unpacker.Memory.HeldBack(Bob, new[] { "stall-0" }).Single().Should().BeTrue("its code ran 11 s of the budget, more than its 10 s share");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Extended == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// A1's contention cut is per partition: a take refused for another sender's partition in the
    /// same mailbox does not cut a message let run past its share. A later take refused for its own
    /// partition does, once; the registration ends with it.
    /// </summary>
    [Fact]
    public async Task A_refusal_for_another_partition_leaves_an_extended_message_running_and_one_for_its_own_cuts_it()
    {
        var (unpacker, calls, never) = Create(new ManualTimeProvider());
        var time = (ManualTimeProvider)unpacker.Time;
        try
        {
            var first = unpacker.UnpackAllAsync(Bob, FiveWithFastAfter("sam-0", TestJwe.Authcrypt(SamKid, "sam-0")), "request-1", default);
            await calls.WaitForAsync(1, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue();
            time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, 5));
            unpacker.Reservations.Extended.Should().Be(1, "nothing waits for sam-0's worker");

            // Tom's partition is full, and a fifth take for it is refused.
            var tom = unpacker.UnpackAllAsync(Bob, Enumerable.Range(1, 4).Select(i => Message(TestJwe.Authcrypt(TomKid, "tom-" + i), "tom-" + i)).ToArray(), "request-2", default);
            await calls.WaitForAsync(5, RealTime);
            var refusedTom = await unpacker.UnpackAllAsync(Bob, new[] { Message(TestJwe.Authcrypt(TomKid, "tom-5"), "tom-5") }, "request-3", default).WaitAsync(RealTime);
            refusedTom.Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("for its sender");
            // A refusal takes the registrations it cuts under the reservations' lock, before it returns.
            unpacker.Reservations.Extended.Should().Be(1, "the refusal was for another partition, so sam-0 is still let run");

            // Sam's partition: sam-0 and three more are held, and a fifth take is refused.
            var sam = unpacker.UnpackAllAsync(Bob, Enumerable.Range(1, 4).Select(i => Message(TestJwe.Authcrypt(SamKid, "sam-" + i), "sam-" + i)).ToArray(), "request-4", default);
            var outcomes = await first.WaitAsync(RealTime);
            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            unpacker.Memory.HeldBack(Bob, new[] { "sam-0" }).Single().Should().BeTrue("it ran past its 10 s share");
            unpacker.Reservations.Extended.Should().Be(0);

            time.Advance(Budget);
            await Task.WhenAll(tom, sam).WaitAsync(RealTime);
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Extended == 0 && unpacker.Reservations.Partitions == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// A message let run past its share that opens on its own before the budget ends is a success,
    /// is not remembered, and leaves no registration or reservation behind.
    /// </summary>
    [Fact]
    public async Task An_extended_message_that_opens_on_its_own_leaves_nothing_registered()
    {
        var (unpacker, calls, never) = Create(new ManualTimeProvider());
        var time = (ManualTimeProvider)unpacker.Time;
        var delivery = unpacker.UnpackAllAsync(Bob, FiveWithFastAfter("open12-0", TestJwe.Anoncrypt("open12-0")), "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue();

        time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, 5));
        unpacker.Reservations.Extended.Should().Be(1);
        time.Advance(TimeSpan.FromSeconds(2));
        var outcomes = await delivery.WaitAsync(RealTime);

        outcomes[0].Succeeded.Should().BeTrue("it kept the rest of the budget and opened at 12 s");
        unpacker.Memory.HeldBack(Bob, new[] { "open12-0" }).Single().Should().BeFalse();
        unpacker.Reserved.Should().Be(0);
        unpacker.Reservations.Extended.Should().Be(0);
        never.TrySetCanceled();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private const string SamKid = "did:example:sam#key-x25519-1";
    private const string TomKid = "did:example:tom#key-x25519-1";

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    // One message, then four that fail at once: when its share ends (10 s) nothing else waits.
    private static DeliveredInput[] FiveWithFastAfter(string id, string packed)
        => new[] { Message(packed, id) }.Concat(Enumerable.Range(1, 4).Select(i => Message(TestJwe.Anoncrypt($"fast-{id}-{i}"), $"fast-{id}-{i}"))).ToArray();

    // "fast" fails at once; "junk" ignores its token and never ends; "open12" opens after 12 s; the
    // rest stall until their token is cancelled.
    private static (DeliveredMessageUnpacker Unpacker, HostCallLog Calls, TaskCompletionSource<DeliveredUnpack> Never) Create(TimeProvider time)
    {
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("fast", StringComparison.Ordinal))
                throw new CryptoException("JWE could not be decrypted.");
            if (label.StartsWith("open12", StringComparison.Ordinal))
            {
                var lookup = Task.Delay(TimeSpan.FromSeconds(12), time, ct);
                using (calls.Enter(label, ct))
                    await lookup.ConfigureAwait(false);
                return UnpackFakes.Opened(label);
            }
            using (calls.Enter(label, ct))
            {
                if (label.StartsWith("junk", StringComparison.Ordinal))
                    return await never.Task.ConfigureAwait(false);
                return await UnpackFakes.StallUntilCancelled(ct).ConfigureAwait(false);
            }
        }, Budget, time);
        return (unpacker, calls, never);
    }

    private static CancellationToken TokenOf(HostCallLog calls, string label)
        => calls.Tokens[calls.Labels.ToList().IndexOf(label)];

    // Moves the clock by `step` right after the `timerNumber`-th timer is created: the work item of
    // the message whose share that timer is starts that much later.
    private sealed class StepAfterTimer(ManualTimeProvider inner, int timerNumber, TimeSpan step) : TimeProvider
    {
        private int _created;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (Interlocked.Increment(ref _created) == timerNumber)
                inner.Advance(step);
            return timer;
        }
    }
}
