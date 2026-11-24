using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Owner decision A1 for #111, "cut only if others wait": a message is cut at its share only while
/// another message of its delivery is not yet taken and could still start, or when a take was
/// refused for its partition or for the container since it was reserved. Otherwise it keeps the rest
/// of the budget, until a later refusal for its partition or for the container cuts it. Every timing
/// is driven by the injected <see cref="ManualTimeProvider"/>; real time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackCutIfOthersWaitTests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);
    private static readonly DeliveryScope Carol = new(FakePickupExchange.M, "did:example:carol");
    private const string SamKid = "did:example:sam#key-x25519-1";

    /// <summary>
    /// A message whose share ends with nothing else of its delivery waiting keeps running. When a
    /// later delivery to the same mailbox is refused a reservation for the same sender, that refusal
    /// cuts it: it fails as out of its share, is remembered as slow, and its worker moves on.
    /// </summary>
    [Fact]
    public async Task An_extended_message_is_cut_when_a_take_for_its_partition_is_refused()
    {
        var (unpacker, time, calls, never) = Create();
        try
        {
            var first = await StartExtendedAsync(unpacker, time, calls, "sam-0", TestJwe.Authcrypt(SamKid, "sam-0"));
            unpacker.Reservations.Extended.Should().Be(1);

            var second = unpacker.UnpackAllAsync(Bob, Enumerable.Range(1, 4).Select(i => Message(TestJwe.Authcrypt(SamKid, "sam-" + i), "sam-" + i)).ToArray(), "request-2", default);
            var outcomes = await first.WaitAsync(RealTime);

            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            TokenOf(calls, "sam-0").IsCancellationRequested.Should().BeTrue();
            unpacker.Memory.HeldBack(Bob, new[] { "sam-0" }).Single().Should().BeTrue("it ran 10 s and did not open");
            unpacker.Reservations.Extended.Should().Be(0);
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
    /// The container's variant: with an extended message among 64 reservations, a take in another
    /// mailbox refused for the container cuts the extended message.
    /// </summary>
    [Fact]
    public async Task An_extended_message_is_cut_when_a_take_is_refused_for_the_container()
    {
        var (unpacker, time, calls, never) = Create();
        try
        {
            var first = await StartExtendedAsync(unpacker, time, calls, "x-0", TestJwe.Anoncrypt("x-0"));
            // Then 63 lookups that ignore their token, in other mailboxes (15 deliveries of four and
            // one of three): the container is full.
            var others = Enumerable.Range(0, 16)
                .Select(m => unpacker.UnpackAllAsync(new DeliveryScope(FakePickupExchange.M, $"did:example:tenant-{m}"),
                    Enumerable.Range(0, m == 15 ? 3 : 4).Select(i => Message(TestJwe.Anoncrypt($"junk-{m}-{i}"), $"junk-{m}-{i}")).ToArray(), "request-t" + m, default))
                .ToArray();
            await calls.WaitForAsync(64, RealTime);
            unpacker.Reserved.Should().Be(UnpackReservations.MaxReserved);
            TokenOf(calls, "x-0").IsCancellationRequested.Should().BeFalse("no take was refused yet");

            var refused = await unpacker.UnpackAllAsync(Carol, new[] { Message(TestJwe.Anoncrypt("c-0"), "c-0") }, "request-c", default).WaitAsync(RealTime);
            var outcomes = await first.WaitAsync(RealTime);

            refused.Single().Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("all mailboxes together");
            outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            unpacker.Memory.HeldBack(Bob, new[] { "x-0" }).Single().Should().BeTrue();
            time.Advance(Budget);
            await Task.WhenAll(others).WaitAsync(RealTime);
        }
        finally
        {
            never.TrySetCanceled();
        }
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// An extended message that never opens fails when the budget ends, is remembered as slow, and
    /// the delivery's result is set by the budget (the delivery used its whole budget: the cost of
    /// A1).
    /// </summary>
    [Fact]
    public async Task An_extended_message_that_never_opens_is_struck_when_the_budget_ends()
    {
        var (unpacker, time, calls, never) = Create();
        var first = await StartExtendedAsync(unpacker, time, calls, "sam-0", TestJwe.Authcrypt(SamKid, "sam-0"));

        time.Advance(Budget - DeliveredMessageUnpacker.ShareOf(Budget, 5));
        var outcomes = await first.WaitAsync(RealTime);

        outcomes[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
        unpacker.Memory.HeldBack(Bob, new[] { "sam-0" }).Single().Should().BeTrue();
        never.TrySetCanceled();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Reservations.Extended == 0, RealTime).Should().BeTrue("the registration ends with the reservation");
    }

    /// <summary>
    /// (b): a take refused for a message's partition before its share ends counts as waiting. The
    /// message is cut when its share ends although nothing of its own delivery waits.
    /// </summary>
    [Fact]
    public async Task A_message_whose_partition_refused_a_take_since_it_was_reserved_is_cut_at_its_share()
    {
        var (unpacker, time, calls, never) = Create();
        try
        {
            var first = unpacker.UnpackAllAsync(Bob, FiveWithFastAfter("sam-0", TestJwe.Authcrypt(SamKid, "sam-0")), "request-1", default);
            await calls.WaitForAsync(1, RealTime);
            SpinWait.SpinUntil(() => unpacker.Settled == 4, RealTime).Should().BeTrue("the four behind it failed at once");
            time.Advance(TimeSpan.FromSeconds(5));
            var second = unpacker.UnpackAllAsync(Bob, Enumerable.Range(1, 4).Select(i => Message(TestJwe.Authcrypt(SamKid, "sam-" + i), "sam-" + i)).ToArray(), "request-2", default);
            await calls.WaitForAsync(4, RealTime);

            time.Advance(TimeSpan.FromSeconds(5)); // sam-0's share ends

            TokenOf(calls, "sam-0").IsCancellationRequested.Should().BeTrue("a take for its partition was refused while it ran");
            (await first.WaitAsync(RealTime))[0].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("its share");
            unpacker.Reservations.Extended.Should().Be(0);
            time.Advance(Budget);
            await second.WaitAsync(RealTime);
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    /// <summary>
    /// (a) with the start rule: a message not yet taken that could no longer start (less than the
    /// floor left) does not count as waiting. Seventeen stalling messages; the one started at 12 s
    /// has its share end at 17 s with the seventeenth still not taken: it keeps running until the
    /// budget ends, and the seventeenth fails before its turn.
    /// </summary>
    [Fact]
    public async Task A_message_not_taken_that_could_no_longer_start_does_not_count_as_waiting()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (text, ct) =>
        {
            if (text == "s-8")
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(2), time, ct);
                using (calls.Enter(text, ct))
                    await delay;
                throw new CryptoException("JWE could not be decrypted.");
            }
            using (calls.Enter(text, ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var payloads = Enumerable.Range(0, 17).Select(i => (ReadOnlyMemory<byte>)System.Text.Encoding.UTF8.GetBytes("s-" + i)).ToArray();

        var batch = unpacker.UnpackAllAsync(payloads, "request-1", default);
        foreach (var (calledBefore, step) in new[] { (4, 5.0), (8, 5.0), (12, 2.0), (13, 3.0), (16, 2.0) })
        {
            await calls.WaitForAsync(calledBefore, RealTime);
            time.Advance(TimeSpan.FromSeconds(step)); // ends at 17 s, when s-12's share (5 s from 12 s) ends
        }

        TokenOf(calls, "s-12").IsCancellationRequested.Should().BeFalse("the only message not taken can no longer start, so nothing waits");
        calls.Count.Should().Be(16);
        time.Advance(TimeSpan.FromSeconds(3));
        var outcomes = await batch.WaitAsync(RealTime);
        outcomes[12].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("ran out while it was being opened");
        outcomes[16].Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static DeliveredInput Message(string packed, string id) => new(id, TestJwe.Bytes(packed));

    // One message that stalls (honouring its token), then four that fail at once: when its share ends
    // (10 s, half the budget) nothing else of the delivery waits.
    private static DeliveredInput[] FiveWithFastAfter(string id, string packed)
        => new[] { Message(packed, id) }.Concat(Enumerable.Range(1, 4).Select(i => Message(TestJwe.Anoncrypt($"fast-{id}-{i}"), $"fast-{id}-{i}"))).ToArray();

    // Start a delivery of FiveWithFastAfter and run its clock to the first message's share: it is
    // then extended (nothing waits, nothing was refused).
    private static async Task<Task<IReadOnlyList<DeliveredOutcome>>> StartExtendedAsync(DeliveredMessageUnpacker unpacker, ManualTimeProvider time, HostCallLog calls, string id, string packed)
    {
        var settled = unpacker.Settled;
        var entered = calls.Count;
        var delivery = unpacker.UnpackAllAsync(Bob, FiveWithFastAfter(id, packed), "request-1", default);
        await calls.WaitForAsync(entered + 1, RealTime);
        SpinWait.SpinUntil(() => unpacker.Settled == settled + 4, RealTime).Should().BeTrue("the four behind it failed at once");
        time.Advance(DeliveredMessageUnpacker.ShareOf(Budget, 5));
        TokenOf(calls, id).IsCancellationRequested.Should().BeFalse("nothing waits and nothing was refused, so it keeps running");
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
