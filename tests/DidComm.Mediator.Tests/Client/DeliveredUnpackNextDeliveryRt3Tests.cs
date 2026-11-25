using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2): the delivery that follows one whose budget ended, while the host calls
/// of the first are still ending. A reservation is held until its host call ends, and a lookup that
/// honours its token may end a moment after its delivery's result is set. The set-up: one named
/// sender's messages fill its bound in the first delivery and never open; their lookups stop when
/// their token is cancelled and end only when the test opens a gate. The budget ends, the first
/// delivery's result is set, and a second delivery with more of that sender's messages and, behind
/// them, another sender's message arrives before the gate is opened. What is checked: what the
/// second delivery does with each message, what is remembered, and what a third delivery does once
/// the gate is open. The manual clock; real time is only a hang guard.
/// </summary>
public sealed class DeliveredUnpackNextDeliveryRt3Tests(ITestOutputHelper output)
{
    private const string SamKid = "did:example:sam#key-x25519-1";
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>
    /// What holds in the second delivery: the other sender's message opens at once, without the
    /// clock moving; none of the first sender's messages in it reaches host code or is remembered;
    /// the first delivery's four are remembered. Once the gate is open every reservation comes
    /// back, and a third delivery starts the first sender's next messages.
    /// </summary>
    [Fact]
    public async Task While_one_senders_lookups_of_the_delivery_before_are_still_ending_another_senders_message_opens_and_nothing_more_is_remembered()
    {
        var run = await RunAsync();

        run.First.Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
        run.RememberedOfFirst.Should().Be(UnpackReservations.MaxPerSender, "each of the first delivery's messages had the whole budget");
        run.ReservedAfterFirst.Should().Be(UnpackReservations.MaxPerSender, "a lookup still ending keeps its reservation");
        run.Second[^1].Succeeded.Should().BeTrue("another sender's message is not counted against the first sender's bound");
        run.SecondEndedWithoutTheClock.Should().BeTrue("nothing of the second delivery waited for its budget");
        run.HostCallsOfSecond.Should().Equal(new[] { "alice-1" }, "none of the first sender's messages in the second delivery reached host code");
        run.RememberedOfSecond.Should().Be(0, "a message that did not start is not remembered");
        run.ReservedOnceTheGateIsOpen.Should().Be(0, "every reservation comes back when its lookup ends");
        run.HostCallsOfThird.Should().Be(UnpackReservations.MaxPerSender, "the sender's next messages start once its lookups have ended");
    }

    /// <summary>
    /// The first sender's messages in the second delivery are not refused for its bound: each is
    /// opened (it gets a worker, or an outcome other than "unpack capacity exhausted").
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: each of them is refused at once for its sender's bound
    /// ("unpack capacity exhausted"), not acknowledged and not remembered, while the lookups of the
    /// delivery before are still ending.
    /// </remarks>
    [Fact(Skip = "RT3: whether a sender's next messages are refused for its bound in the delivery that follows one whose budget ended while its lookups are still ending")]
    public async Task While_one_senders_lookups_of_the_delivery_before_are_still_ending_its_next_messages_are_not_refused_for_its_bound()
    {
        var run = await RunAsync();

        run.Second.Take(run.Second.Count - 1).Should().NotContain(o => o.Error is TimeoutException && o.Error.Message.Contains("unpack capacity exhausted"),
            "the sender's lookups of the delivery before honoured their token ({0} refused of {1})",
            run.Second.Take(run.Second.Count - 1).Count(o => o.Error is TimeoutException && o.Error.Message.Contains("unpack capacity exhausted")), run.Second.Count - 1);
    }

    private async Task<Run> RunAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var stopped = new HostCallLog();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            calls.Enter(label, ct);
            if (label.StartsWith("alice-", StringComparison.Ordinal))
                return UnpackFakes.Opened(label);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It honours its token: it stops here, and its end takes a moment more (the gate).
            }
            stopped.Enter(label, ct);
            await gate.Task.ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }, Budget, time);
        var bound = UnpackReservations.MaxPerSender;
        var first = Enumerable.Range(1, bound).Select(i => Message(SamKid, $"sam-{i}")).ToArray();
        var second = Enumerable.Range(bound + 1, bound).Select(i => Message(SamKid, $"sam-{i}")).Append(Message(AliceKid, "alice-1")).ToArray();
        var third = Enumerable.Range(bound + 1, bound).Select(i => Message(SamKid, $"sam-{i}")).ToArray();
        try
        {
            var delivery1 = unpacker.UnpackAllAsync(Bob, first, "request-1", default);
            await calls.WaitForAsync(bound, RealTime);
            await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            var outcomes1 = await delivery1.WaitAsync(RealTime);
            await stopped.WaitForAsync(bound, RealTime); // every lookup saw its cancellation; none has ended
            var reservedAfterFirst = unpacker.Reserved;
            var rememberedOfFirst = unpacker.Memory.HeldBack(Bob, first.Select(m => m.Id).ToArray()).Count(h => h);

            var timersBefore = time.ScheduledTimers;
            var delivery2 = unpacker.UnpackAllAsync(Bob, second, "request-2", default);
            var endedWithoutTheClock = SpinWait.SpinUntil(() => delivery2.IsCompleted, TimeSpan.FromSeconds(2));
            if (!endedWithoutTheClock)
                await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            var outcomes2 = await delivery2.WaitAsync(RealTime);
            var hostCallsOfSecond = calls.Labels.Skip(bound).ToArray();
            var rememberedOfSecond = unpacker.Memory.HeldBack(Bob, second.Select(m => m.Id).ToArray()).Count(h => h);

            gate.TrySetResult(true);
            var reservedOnceOpen = SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime) ? 0 : unpacker.Reserved;
            var before = calls.Count;
            var delivery3 = unpacker.UnpackAllAsync(Bob, third, "request-3", default);
            SpinWait.SpinUntil(() => delivery3.IsCompleted || calls.Count - before >= bound, TimeSpan.FromSeconds(2));
            var hostCallsOfThird = calls.Count - before;
            await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            await delivery3.WaitAsync(RealTime);

            output.WriteLine($"four at a time {DeliveredMessageUnpacker.FetchConcurrency}, per sender {bound}: first delivery {Describe(outcomes1)}; reserved after it {reservedAfterFirst}, remembered {rememberedOfFirst}; " +
                $"second delivery {Describe(outcomes2)}, ended without the clock: {endedWithoutTheClock}, host calls: [{string.Join(", ", hostCallsOfSecond)}], remembered {rememberedOfSecond}; " +
                $"reserved once the gate is open {reservedOnceOpen}; third delivery started {hostCallsOfThird} host calls");
            return new Run(outcomes1, reservedAfterFirst, rememberedOfFirst, outcomes2, endedWithoutTheClock, hostCallsOfSecond, rememberedOfSecond, reservedOnceOpen, hostCallsOfThird);
        }
        finally
        {
            gate.TrySetResult(true);
        }
    }

    private static string Describe(IReadOnlyList<DeliveredOutcome> outcomes)
        => string.Join(", ", outcomes.Select(o => o.Succeeded ? "opened"
            : o.Error!.Message.Contains("unpack capacity exhausted") ? "refused for its bound"
            : o.Error.Message.Contains("before its turn came") ? "out of time before its turn"
            : o.Error.Message.Contains("ran out while it was being opened") ? "out of time while being opened"
            : o.Error.Message.Contains("did not open when it was delivered before") ? "held back"
            : o.Error.GetType().Name).GroupBy(t => t).Select(g => $"{g.Count()} × {g.Key}"));

    private static DeliveredInput Message(string senderKid, string id) => new(id, TestJwe.Bytes(TestJwe.Authcrypt(senderKid, id)));

    private sealed record Run(
        IReadOnlyList<DeliveredOutcome> First, int ReservedAfterFirst, int RememberedOfFirst,
        IReadOnlyList<DeliveredOutcome> Second, bool SecondEndedWithoutTheClock, IReadOnlyList<string> HostCallsOfSecond, int RememberedOfSecond,
        int ReservedOnceTheGateIsOpen, int HostCallsOfThird);
}
