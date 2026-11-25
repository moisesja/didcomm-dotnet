using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT4 (#111): one message that never opens, delivered twice at overlapping times in one mailbox
/// (as a fetch and a live stream on one mailbox can deliver it), and what each delivery's outcome
/// says of it (<see cref="DeliveredOutcome.Inconclusive"/>) against what the memory holds. The first
/// delivery ends at its budget and strikes the message; the second, opened a second later, ends a
/// second after it, while the message's entry is inside its pause, where the memory takes no
/// further strike. The lookups honour their token; the budgets are on the manual clock; real time
/// is only a hang guard.
/// </summary>
public sealed class InconclusiveOverlappingDeliveriesRt4Tests(ITestOutputHelper output)
{
    private const int AtOnce = DeliveredMessageUnpacker.FetchConcurrency;
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(60);
    private static readonly DeliveryScope Bob = new(FakePickupExchange.M, FakePickupExchange.L);

    /// <summary>
    /// Held. When the message has its whole budget in both deliveries, both outcomes are conclusive
    /// ("while it was being opened", not inconclusive), although the memory takes its strike in the
    /// first only: at the second's end the message is already inside its pause. When it is started
    /// late in the second (behind sixteen that fail on their own after three seconds), the second's
    /// outcome is inconclusive, although the memory took a strike for it a second earlier, in the
    /// first. In both rows the message is held back from then on for one first pause, counted from
    /// the first delivery's end, and is started again by the first delivery after that pause.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_message_that_two_overlapping_deliveries_both_cut_is_counted_once_and_each_outcome_says_whether_that_delivery_gave_it_the_whole_budget(bool startedLateInTheSecond)
    {
        using var floor = new PoolFloor(parked: 2);
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            var label = TestJwe.LabelOf(packed);
            using (calls.Enter(label, ct))
            {
                if (label.StartsWith("front-", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), time, ct);
                    throw new CryptoException("JWE could not be decrypted.");
                }
                return await UnpackFakes.StallUntilCancelled(ct);
            }
        }, Budget, time);
        var message = From("did:example:carol#key-x25519-1", "x");
        var front = Enumerable.Range(0, AtOnce).Select(i => From($"did:example:sender-{i}#key-x25519-1", $"front-{i}")).ToArray();

        var first = unpacker.UnpackAllAsync(Bob, new[] { message }, "request-1", default);
        await calls.WaitForAsync(1, RealTime);
        await Task.Run(() => time.Advance(TimeSpan.FromSeconds(1))).WaitAsync(RealTime);
        var second = unpacker.UnpackAllAsync(Bob, startedLateInTheSecond ? front.Append(message).ToArray() : new[] { message }, "request-2", default);
        if (startedLateInTheSecond)
        {
            await calls.WaitForAsync(1 + AtOnce, RealTime);
            await time.WaitForScheduledTimersAsync(2 + AtOnce, RealTime); // both budgets and the sixteen waits
            await Task.Run(() => time.Advance(TimeSpan.FromSeconds(3))).WaitAsync(RealTime);
            await calls.WaitForAsync(2 + AtOnce, RealTime); // the message is started in the second, three seconds into it
            await Task.Run(() => time.Advance(Budget - TimeSpan.FromSeconds(4))).WaitAsync(RealTime);
        }
        else
        {
            await calls.WaitForAsync(2, RealTime);
            await Task.Run(() => time.Advance(Budget - TimeSpan.FromSeconds(1))).WaitAsync(RealTime);
        }
        var ofTheFirst = (await first.WaitAsync(RealTime)).Single();
        var heldAfterTheFirst = unpacker.Memory.HeldBack(Bob, new string?[] { "x" })[0];
        second.IsCompleted.Should().BeFalse("the second delivery has a second of its budget left");
        await Task.Run(() => time.Advance(TimeSpan.FromSeconds(1))).WaitAsync(RealTime);
        var ofTheSecond = (await second.WaitAsync(RealTime))[^1];
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every lookup ended");

        output.WriteLine($"started late in the second: {startedLateInTheSecond}; first: inconclusive {ofTheFirst.Inconclusive}; second: inconclusive {ofTheSecond.Inconclusive}; held back after the first: {heldAfterTheFirst}; remembered: {unpacker.Memory.Count}");
        ofTheFirst.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        ofTheFirst.Inconclusive.Should().BeFalse("it had the whole budget of the first delivery, and was struck");
        heldAfterTheFirst.Should().BeTrue();
        ofTheSecond.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
        ofTheSecond.Inconclusive.Should().Be(startedLateInTheSecond, "the second delivery's outcome says whether the second gave it the whole budget");

        // One strike: the first pause, counted from the first delivery's end (20 s), and no longer.
        var firstPause = unpacker.Memory.FirstPause;
        var sinceTheFirstEnded = TimeSpan.FromSeconds(1);
        await Task.Run(() => time.Advance(firstPause - sinceTheFirstEnded - TimeSpan.FromTicks(1))).WaitAsync(RealTime);
        unpacker.Memory.HeldBack(Bob, new string?[] { "x" })[0].Should().BeTrue("it is held back until one first pause after the first delivery's end");
        await Task.Run(() => time.Advance(TimeSpan.FromTicks(1))).WaitAsync(RealTime);
        unpacker.Memory.HeldBack(Bob, new string?[] { "x" })[0].Should().BeFalse("one strike was taken, so the pause is the first one, not the doubled one");

        var started = calls.Labels.Count(l => l == "x");
        var callsBefore = calls.Count;
        var third = unpacker.UnpackAllAsync(Bob, new[] { message }, "request-3", default);
        await calls.WaitForAsync(callsBefore + 1, RealTime);
        calls.Labels.Count(l => l == "x").Should().Be(started + 1, "after its pause it is started again");
        await Task.Run(() => time.Advance(Budget)).WaitAsync(RealTime);
        (await third.WaitAsync(RealTime)).Single().Inconclusive.Should().BeFalse("it had the whole budget again");
        SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue();
    }

    /// <summary>
    /// Held: the budget's end and the caller's cancellation (or the owner's shutdown) land together
    /// on two threads, forty times, in an order that varies, for a delivery of three messages that
    /// never open: one started at the delivery's start, one started late (behind one that fails on
    /// its own after three seconds), and one refused for its sender's bound. The delivery ends one
    /// way or the other. When its result is cancelled, nothing was remembered. When its result is
    /// set, each failed outcome is inconclusive exactly when the memory does not hold its message
    /// back afterwards.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_budgets_end_races_the_callers_cancellation_or_the_shutdown_the_flags_say_what_the_memory_says_or_the_result_is_cancelled(bool shutdown)
    {
        using var floor = new PoolFloor(parked: 2);
        int cancelled = 0, set = 0;
        for (var round = 0; round < 40; round++)
        {
            var time = new ManualTimeProvider();
            var calls = new HostCallLog();
            using var caller = new CancellationTokenSource();
            using var owner = new CancellationTokenSource();
            var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
            {
                var label = TestJwe.LabelOf(packed);
                using (calls.Enter(label, ct))
                {
                    if (label == "fails")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), time, ct);
                        throw new CryptoException("JWE could not be decrypted.");
                    }
                    return await UnpackFakes.StallUntilCancelled(ct);
                }
            }, Budget, time, owner.Token);
            // Carol's bound is held, so her message is refused; the others each have a sender of their own.
            var held = new List<UnpackReservations.Reservation>();
            for (var i = 0; i < UnpackReservations.MaxPerSender; i++)
            {
                unpacker.Reservations.TryTake(Bob, "did:example:carol", out var reservation, out _).Should().BeTrue();
                held.Add(reservation!);
            }
            // Fifteen that never open and one that fails after three seconds take the sixteen
            // workers; then one that never opens (started late, by the worker the failure frees)
            // and Carol's (refused).
            var messages = Enumerable.Range(0, AtOnce - 1).Select(i => From($"did:example:sender-{i}#key-x25519-1", $"early-{i}"))
                .Append(From("did:example:sender-f#key-x25519-1", "fails"))
                .Append(From("did:example:carol#key-x25519-1", "refused"))
                .Append(From("did:example:sender-l#key-x25519-1", "late"))
                .ToArray();

            var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", caller.Token);
            await calls.WaitForAsync(AtOnce, RealTime);
            await time.WaitForScheduledTimersAsync(2, RealTime); // the budget and the one wait
            await Task.Run(() => time.Advance(TimeSpan.FromSeconds(3))).WaitAsync(RealTime);
            await calls.WaitForAsync(AtOnce + 1, RealTime); // "late" is started
            await Task.Run(() => time.Advance(Budget - TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1))).WaitAsync(RealTime);

            using var go = new ManualResetEventSlim();
            // One of the two spins a little first, a different one and for a different length each
            // round; the cancellation is the quicker of the two, so it is given the longer spins.
            var ending = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(round % 2 == 0 ? round * 50 : 0);
                time.Advance(TimeSpan.FromTicks(1));
            });
            var cancelling = Task.Run(() =>
            {
                go.Wait();
                Thread.SpinWait(round % 2 == 0 ? 0 : round * 4000);
                (shutdown ? owner : caller).Cancel();
            });
            go.Set();
            await ending.WaitAsync(RealTime);
            await cancelling.WaitAsync(RealTime);
            var thrown = await Record.ExceptionAsync(() => delivery.WaitAsync(RealTime));
            delivery.IsCompleted.Should().BeTrue("the delivery ended (round {0})", round);
            foreach (var reservation in held)
                reservation.Release();
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("every lookup ended (round {0})", round);

            var ids = messages.Select(m => m.Id).ToArray();
            var heldBack = unpacker.Memory.HeldBack(Bob, ids);
            if (thrown is not null)
            {
                cancelled++;
                thrown.Should().BeAssignableTo<OperationCanceledException>("round {0}", round);
                // "fails" failed on its own, three seconds in, before anything ended the delivery.
                ids.Where((id, i) => heldBack[i]).Should().Equal(new[] { "fails" }, "a delivery ended by its caller or its owner remembers nothing more (round {0})", round);
            }
            else
            {
                set++;
                var outcomes = await delivery;
                for (var i = 0; i < outcomes.Count; i++)
                {
                    outcomes[i].Succeeded.Should().BeFalse();
                    outcomes[i].Inconclusive.Should().Be(!heldBack[i], "an outcome is inconclusive exactly when the memory does not hold its message back ({0}, round {1})", ids[i], round);
                }
                outcomes[^2].Inconclusive.Should().BeTrue("refused for capacity (round {0})", round);
                outcomes[^1].Inconclusive.Should().BeTrue("started late (round {0})", round);
                outcomes[0].Inconclusive.Should().BeFalse("it had the whole budget (round {0})", round);
            }
        }
        output.WriteLine($"{(shutdown ? "shutdown" : "caller")}: result cancelled in {cancelled} rounds, set in {set}");
        (cancelled + set).Should().Be(40);
    }

    private static DeliveredInput From(string kid, string label) => new(label, TestJwe.Bytes(TestJwe.Authcrypt(kid, label)));
}
