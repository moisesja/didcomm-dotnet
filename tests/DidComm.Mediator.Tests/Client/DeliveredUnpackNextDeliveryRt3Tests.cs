using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// #111, the delivery that follows one whose budget ended, while the host calls of the first are
/// still ending (the documented residual R-13, restated and re-measured with sixteen opened at once
/// and sixteen per sender). A reservation is held until its host call ends, and a lookup that
/// honours its token may end a moment after its delivery's result is set. The set-up: messages of
/// one partition (one named sender's, or the mailbox's messages that name no sender) are opened in a
/// first delivery and never open; their lookups stop when their token is cancelled and end only when
/// the test opens a gate. The budget ends, the first delivery's result is set, and a second delivery
/// with more messages of that partition and, behind them, another sender's message arrives before
/// the gate is opened. What is checked: what the second delivery does with each message, what is
/// remembered, and what a third delivery does once the gate is open. The manual clock; real time is
/// only a hang guard.
/// </summary>
public sealed class DeliveredUnpackNextDeliveryRt3Tests(ITestOutputHelper output)
{
    private const string SamKid = "did:example:sam#key-x25519-1";
    private const string AliceKid = "did:example:alice#key-x25519-1";
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>
    /// What holds in the second delivery when the first filled the sender's bound (sixteen): the
    /// other sender's message opens at once, without the clock moving; none of the first sender's
    /// messages in it reaches host code or is remembered; the first delivery's sixteen are
    /// remembered. Once the gate is open every reservation comes back, and a third delivery starts
    /// the first sender's next messages.
    /// </summary>
    [Fact]
    public async Task While_one_senders_lookups_of_the_delivery_before_are_still_ending_another_senders_message_opens_and_nothing_more_is_remembered()
    {
        var bound = UnpackReservations.MaxPerSender;
        var run = await RunAsync(named: true, first: bound, second: bound);

        run.First.Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"));
        run.RememberedOfFirst.Should().Be(bound, "each of the first delivery's messages had the whole budget");
        run.ReservedAfterFirst.Should().Be(bound, "a lookup still ending keeps its reservation");
        run.Second[^1].Succeeded.Should().BeTrue("another sender's message is not counted against the first sender's bound");
        run.SecondEndedWithoutTheClock.Should().BeTrue("nothing of the second delivery waited for its budget");
        run.HostCallsOfSecond.Should().Equal(new[] { "alice-1" }, "none of the first sender's messages in the second delivery reached host code");
        run.RememberedOfSecond.Should().Be(0, "a message that did not start is not remembered");
        run.ReservedOnceTheGateIsOpen.Should().Be(0, "every reservation comes back when its lookup ends");
        run.HostCallsOfThird.Should().Be(bound, "the sender's next messages start once its lookups have ended");
    }

    /// <summary>
    /// The residual, pinned (R-13 at the budget's end): while the lookups of the delivery before are
    /// still ending, the next delivery starts only as many messages of the same partition as its
    /// bound of sixteen leaves free, and refuses the rest at once ("unpack capacity exhausted", for
    /// the partition): they reach no host code, are not remembered, and take no time. The bound is
    /// the same for a named sender and for the mailbox's messages that name no sender.
    /// <list type="bullet">
    /// <item><description>sixteen still ending, sixteen more: none starts, sixteen are refused, and the delivery ends without the clock;</description></item>
    /// <item><description>ten still ending, ten more (the most this library's mediator delivers at once): six start, four are refused;</description></item>
    /// <item><description>four still ending, four more: all four start (they take four of the sixteen workers), none is refused.</description></item>
    /// </list>
    /// The ones that start never open, so that delivery lasts its whole budget, and they are
    /// remembered at its end. In every row the other sender's message in the second delivery opens,
    /// and once the gate is open every reservation comes back and a third delivery starts the
    /// partition's next messages.
    /// </summary>
    /// <remarks>
    /// Committed skipped by the independent pass on fix round 2, as the question whether a sender's
    /// next messages are refused then (with four opened at once and four per sender: all of them).
    /// Decided: a documented residual. This is its pin, with sixteen opened at once.
    /// </remarks>
    [Theory]
    [InlineData(true, 16, 16, 0, 16)]
    [InlineData(true, 10, 10, 6, 4)]
    [InlineData(true, 4, 4, 4, 0)]
    [InlineData(false, 16, 16, 0, 16)]
    [InlineData(false, 10, 10, 6, 4)]
    [InlineData(false, 4, 4, 4, 0)]
    public async Task While_the_lookups_of_the_delivery_before_are_still_ending_the_next_delivery_starts_what_the_bound_of_sixteen_leaves_free_and_refuses_the_rest(bool named, int first, int second, int started, int refused)
    {
        var bound = named ? UnpackReservations.MaxPerSender : UnpackReservations.MaxPerAnonymous;
        bound.Should().Be(16);
        started.Should().Be(Math.Min(second, bound - first));

        var run = await RunAsync(named, first, second);

        var ofThePartition = run.Second.Take(second).ToArray();
        var wereRefused = ofThePartition.Where(o => o.Error is TimeoutException && o.Error.Message.Contains("unpack capacity exhausted")).ToArray();
        run.ReservedAfterFirst.Should().Be(first, "each lookup of the first delivery still holds its reservation");
        run.RememberedOfFirst.Should().Be(first);
        wereRefused.Should().HaveCount(refused, "the messages beyond what the bound leaves free");
        wereRefused.Should().OnlyContain(o => o.Error!.Message.Contains("its sender in this mailbox"), "refused for the partition's bound, not for the container's");
        ofThePartition.Skip(started).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("unpack capacity exhausted"), "the first ones in delivery order start, the ones behind them are refused");
        run.HostCallsOfSecond.Should().HaveCount(started + 1, "the ones the bound leaves free, and the other sender's message");
        ofThePartition.Take(started).Should().OnlyContain(o => o.Error is TimeoutException && o.Error.Message.Contains("ran out while it was being opened"), "the ones that started never open");
        run.RememberedOfSecond.Should().Be(started, "the ones that had the whole budget are remembered; a refused message is not");
        run.SecondEndedWithoutTheClock.Should().Be(started == 0, "a delivery that starts none of them ends at once; one that starts some lasts its budget");
        run.Second[^1].Succeeded.Should().BeTrue("another sender's message opens");
        run.ReservedOnceTheGateIsOpen.Should().Be(0, "every reservation comes back when its lookup ends");
        run.HostCallsOfThird.Should().Be(Math.Min(bound, second), "the partition's next messages start once its lookups have ended");
    }

    private async Task<Run> RunAsync(bool named, int first, int second)
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
        DeliveredInput OfThePartition(int i) => named ? Message(SamKid, $"sam-{i}") : new DeliveredInput($"sam-{i}", TestJwe.Bytes(TestJwe.Anoncrypt($"sam-{i}")));
        var firstDelivery = Enumerable.Range(1, first).Select(OfThePartition).ToArray();
        var secondDelivery = Enumerable.Range(first + 1, second).Select(OfThePartition).Append(Message(AliceKid, "alice-1")).ToArray();
        var thirdDelivery = Enumerable.Range(first + second + 1, second).Select(OfThePartition).ToArray();
        try
        {
            var delivery1 = unpacker.UnpackAllAsync(Bob, firstDelivery, "request-1", default);
            await calls.WaitForAsync(first, RealTime);
            await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            var outcomes1 = await delivery1.WaitAsync(RealTime);
            await stopped.WaitForAsync(first, RealTime); // every lookup saw its cancellation; none has ended
            var reservedAfterFirst = unpacker.Reserved;
            var rememberedOfFirst = unpacker.Memory.HeldBack(Bob, firstDelivery.Select(m => m.Id).ToArray()).Count(h => h);

            // The second delivery arrives now. It is left alone until each of its messages has an
            // outcome of its own (refused, or opened) or has entered host code; only then, if any
            // is still running, does the clock move to its budget's end.
            var settledBefore = unpacker.Settled;
            var delivery2 = unpacker.UnpackAllAsync(Bob, secondDelivery, "request-2", default);
            int StallsEntered() => calls.Labels.Skip(first).Count(l => !l.StartsWith("alice-", StringComparison.Ordinal));
            SpinWait.SpinUntil(() => unpacker.Settled - settledBefore + StallsEntered() == secondDelivery.Length, RealTime)
                .Should().BeTrue("every message of the second delivery gets an outcome of its own or enters host code");
            // With none of them running the delivery ends on its own; with some running it cannot
            // end before its budget does.
            var endedWithoutTheClock = StallsEntered() == 0;
            if (!endedWithoutTheClock)
            {
                delivery2.IsCompleted.Should().BeFalse("the messages that started never open");
                await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            }
            var outcomes2 = await delivery2.WaitAsync(RealTime);
            var hostCallsOfSecond = calls.Labels.Skip(first).ToArray();
            var rememberedOfSecond = unpacker.Memory.HeldBack(Bob, secondDelivery.Select(m => m.Id).ToArray()).Count(h => h);

            gate.TrySetResult(true);
            var reservedOnceOpen = SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime) ? 0 : unpacker.Reserved;
            var before = calls.Count;
            var expectedOfThird = Math.Min(named ? UnpackReservations.MaxPerSender : UnpackReservations.MaxPerAnonymous, thirdDelivery.Length);
            var delivery3 = unpacker.UnpackAllAsync(Bob, thirdDelivery, "request-3", default);
            SpinWait.SpinUntil(() => delivery3.IsCompleted || calls.Count - before >= expectedOfThird, RealTime);
            var hostCallsOfThird = calls.Count - before;
            await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
            await delivery3.WaitAsync(RealTime);

            output.WriteLine($"R-13 | {(named ? "one named sender" : "no named sender")} | first delivery {first}: {Describe(outcomes1)}; reserved after it {reservedAfterFirst}, remembered {rememberedOfFirst} | " +
                $"second delivery {second} + 1 of another sender: {Describe(outcomes2)}; ended without the clock: {endedWithoutTheClock}; {hostCallsOfSecond.Length} host calls; remembered {rememberedOfSecond} | " +
                $"reserved once the gate is open {reservedOnceOpen}; third delivery started {hostCallsOfThird} host calls (opened at once {DeliveredMessageUnpacker.FetchConcurrency}, per sender {UnpackReservations.MaxPerSender}, without a sender {UnpackReservations.MaxPerAnonymous})");
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
