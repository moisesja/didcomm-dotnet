using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Owner decision E for #111, "remove per-message limits": a message of a delivery runs until it
/// opens, fails, or the delivery's budget ends. Nothing else ends it: not a time of its own, not
/// another message waiting for a worker, not a take refused for its partition or for the container.
/// It replaces the share of the budget each message had and owner decision A1 ("cut only if others
/// wait"); the tests here are RT2-A's reproduction and A1's tests, restated to what holds now. Every
/// timing is driven by the injected <see cref="ManualTimeProvider"/> and rendezvous on host calls;
/// real time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackNoPerMessageLimitTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private static readonly DeliveryScope Carol = new(FakePickupExchange.M, "did:example:carol");
    private const string SamKid = "did:example:sam#key-x25519-1";

    /// <summary>
    /// RT2A-1, restated. Six messages: the first never ends and ignores its token, the second needs
    /// 12 s and opens, the third fails at once (its worker takes the fifth), the fourth and the fifth
    /// never open, and the sixth waits for a worker. Before, the first two had half the budget each,
    /// their shares ended together at 10 s with the sixth waiting, and both were cut. Now no message
    /// has a limit of its own: at 10 s nothing is cut, the second opens at 12 s, its worker takes
    /// the sixth, which opens, and the three that never open end with the budget.
    /// </summary>
    [Fact]
    public async Task RT2A_1_a_message_that_needs_twelve_seconds_opens_whatever_waits_behind_it()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            switch (label)
            {
                case "x-0":
                    using (calls.Enter(label, ct))
                        return await never.Task.ConfigureAwait(false); // ignores its token
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
        try
        {
            var batch = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
            await calls.WaitForAsync(4, RealTime); // x-0, slow-1, stall-3, and stall-4 taken after fast-2 failed
            calls.Labels.Should().BeEquivalentTo(new[] { "x-0", "slow-1", "stall-3", "stall-4" }, "only the sixth waits for a worker");

            time.Advance(TimeSpan.FromSeconds(10));
            calls.Tokens.Should().OnlyContain(t => !t.IsCancellationRequested, "no message has a time limit of its own: at 10 s, with the sixth waiting, nothing is cut");
            calls.Count.Should().Be(4, "the sixth still waits");

            time.Advance(TimeSpan.FromSeconds(2)); // 12 s: slow-1's lookup ends
            await calls.WaitForAsync(5, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled == 3, RealTime).Should().BeTrue("fast-2 failed, slow-1 opened and the sixth opened");
            time.Advance(Budget); // the budget's end, for x-0, stall-3 and stall-4
            var outcomes = await batch.WaitAsync(RealTime);

            outcomes[1].Succeeded.Should().BeTrue("it needed 12 s of the 20 s budget");
            outcomes[5].Succeeded.Should().BeTrue("it got the worker slow-1 gave back");
            outcomes[2].Error.Should().BeOfType<CryptoException>();
            new[] { outcomes[0], outcomes[3], outcomes[4] }.Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
            unpacker.Memory.HeldBack(Bob, labels).Should().Equal(new[] { true, false, true, true, true, false },
                "the two that opened are not remembered; fast-2 failed on its own; the three that never opened started with the budget");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Partitions == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// A take refused for a running message's partition does not end that message (A1's refusal cut,
    /// restated). Sam's first message never opens and four that fail at once are behind it, so from
    /// then on nothing of its delivery waits. Ten seconds in, a second delivery brings four more
    /// messages naming Sam: three take the rest of Sam's partition and the fourth is refused. Sam's
    /// first message keeps running; it ends with its delivery's budget, and is remembered then.
    /// </summary>
    [Fact]
    public async Task A_take_refused_for_a_running_messages_partition_does_not_end_it()
    {
        var (unpacker, time, calls, never) = Create();
        try
        {
            var first = await StartAloneAsync(unpacker, time, calls, "sam-0", TestJwe.Authcrypt(SamKid, "sam-0"));

            var second = unpacker.UnpackAllAsync(Bob, Enumerable.Range(1, 4).Select(i => Message(TestJwe.Authcrypt(SamKid, "sam-" + i), "sam-" + i)).ToArray(), "request-2", default);
            await calls.WaitForAsync(4, RealTime); // sam-0 and sam-1..3: Sam's partition is full, sam-4 is refused
            SpinWait.SpinUntil(() => unpacker.Settled == 5, RealTime).Should().BeTrue("the four that failed at once, and the refusal");
            TokenOf(calls, "sam-0").IsCancellationRequested.Should().BeFalse("a refusal ends no running message");
            first.IsCompleted.Should().BeFalse();

            time.Advance(Budget - TimeSpan.FromSeconds(10)); // the first delivery's budget ends
            var outcomes = await first.WaitAsync(RealTime);
            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
            unpacker.Memory.HeldBack(Bob, new[] { "sam-0" }).Single().Should().BeTrue("it started with its delivery's budget and had not opened when the budget ended");

            time.Advance(Budget);
            (await second.WaitAsync(RealTime))[3].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("for its sender");
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// The container's variant (A1's refusal cut, restated): with a message running among C
    /// reservations, a take in another mailbox refused for the container does not end it; it ends
    /// with its delivery's budget.
    /// </summary>
    [Fact]
    public async Task A_take_refused_for_the_container_does_not_end_a_running_message()
    {
        var (unpacker, time, calls, never) = Create();
        try
        {
            var first = await StartAloneAsync(unpacker, time, calls, "x-0", TestJwe.Anoncrypt("x-0"));
            // Then C - 1 lookups that ignore their token, in other mailboxes (deliveries of four, the
            // last of three): the container is full.
            const int mailboxes = UnpackReservations.MaxReserved / DeliveredMessageUnpacker.FetchConcurrency;
            var others = Enumerable.Range(0, mailboxes)
                .Select(m => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:tenant-{m}"),
                    Enumerable.Range(0, m == mailboxes - 1 ? 3 : 4).Select(i => Message(TestJwe.Anoncrypt($"junk-{m}-{i}"), $"junk-{m}-{i}")).ToArray(), "request-t" + m, default))
                .ToArray();
            await calls.WaitForAsync(UnpackReservations.MaxReserved, RealTime);
            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved);

            var refused = await unpacker.UnpackAllAsync(Carol, new[] { Message(TestJwe.Anoncrypt("c-0"), "c-0") }, "request-c", default).WaitAsync(RealTime);

            refused.Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("all mailboxes together");
            TokenOf(calls, "x-0").IsCancellationRequested.Should().BeFalse("a refusal ends no running message");
            first.IsCompleted.Should().BeFalse();

            time.Advance(Budget - TimeSpan.FromSeconds(10)); // the first delivery's budget ends
            (await first.WaitAsync(RealTime))[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
            time.Advance(Budget);
            await Task.WhenAll(others).WaitAsync(RealTime);
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    // Start a delivery of one message that stalls (honouring its token) and four that fail at once,
    // and run its clock to 10 s: the message is still being opened, and nothing of its delivery waits.
    private static async Task<Task<IReadOnlyList<DeliveredOutcome>>> StartAloneAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, HostCallLog calls, string id, string packed)
    {
        var messages = new[] { Message(packed, id) }.Concat(Enumerable.Range(1, 4).Select(i => Message(TestJwe.Anoncrypt($"fast-{id}-{i}"), $"fast-{id}-{i}"))).ToArray();
        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("the four behind it failed at once");
        time.Advance(TimeSpan.FromSeconds(10));
        TokenOf(calls, id).IsCancellationRequested.Should().BeFalse("it has no time limit of its own");
        delivery.IsCompleted.Should().BeFalse();
        return delivery;
    }

    private static CancellationToken TokenOf(HostCallLog calls, string label)
        => calls.Tokens[calls.Labels.ToList().IndexOf(label)];

    // "junk" lookups ignore their token and never end; "fast" ones fail at once; the rest stall
    // until their token is cancelled.
    private static (DeliveredMessageUnpacker Unpacker, ManualTimeProvider Time, HostCallLog Calls, TaskCompletionSource<DeliveredUnpack> Never) Create()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            if (label.StartsWith("fast", StringComparison.Ordinal))
                throw new CryptoException("JWE could not be decrypted.");
            using (calls.Enter(label, ct))
            {
                if (label.StartsWith("junk", StringComparison.Ordinal))
                    return await never.Task;
                return await UnpackFakes.StallUntilCancelled(ct);
            }
        }, Budget, time);
        return (unpacker, time, calls, never);
    }
}
