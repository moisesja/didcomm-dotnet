using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Which outcomes of the unpacker say that the message was not attempted (#111,
/// <see cref="DeliveredOutcome.NotAttempted"/>, the source of <see cref="PickupItem.NotAttempted"/>):
/// one row per kind of outcome the unpacker makes. The flag is set in two cases only, both before a
/// message's host call exists: the delivery's budget ran out before the message's turn came, and the
/// message was refused for capacity. Each row builds its kind on the injected clock, checks by the
/// failure's type and text that it built that kind, and then reads the flag. Real time is only a
/// hang guard.
/// </summary>
public sealed class DeliveredUnpackNotAttemptedTests
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

        /// <summary>Its host call failed while the delivery was not being ended.</summary>
        FailedOnItsOwn,

        /// <summary>Its host call was running when the budget ended, and ended with its token.</summary>
        OutOfTimeWhileItWasBeingOpened,

        /// <summary>Its host call was running when the budget ended, and ignores its token.</summary>
        OutOfTimeWhileItsLookupIgnoredItsToken,

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
    /// For every kind of outcome: the flag is true for a message that ran out of budget before its
    /// turn (no worker took it; a worker took it with less than the floor left; a worker took it and
    /// the delivery ended before its host call was created) and for one refused for capacity (its
    /// sender's bound, the bound of messages naming no sender, the container's); it is false for
    /// every other kind, the one whose call was created and whose code never started included.
    /// </summary>
    [Theory]
    [InlineData(Kind.Opened, false)]
    [InlineData(Kind.FailedOnItsOwn, false)]
    [InlineData(Kind.OutOfTimeWhileItWasBeingOpened, false)]
    [InlineData(Kind.OutOfTimeWhileItsLookupIgnoredItsToken, false)]
    [InlineData(Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted, false)]
    [InlineData(Kind.OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated, true)]
    [InlineData(Kind.OutOfTimeBeforeAnyWorkerTookIt, true)]
    [InlineData(Kind.TakenWithLessThanTheFloorLeft, true)]
    [InlineData(Kind.RefusedForItsSendersBound, true)]
    [InlineData(Kind.RefusedForTheBoundOfMessagesNamingNoSender, true)]
    [InlineData(Kind.RefusedForTheContainersBound, true)]
    [InlineData(Kind.HeldBack, false)]
    [InlineData(Kind.NotUtf8, false)]
    [InlineData(Kind.Empty, false)]
    public async Task An_outcome_says_not_attempted_only_when_the_budget_ran_out_before_the_messages_turn_or_it_was_refused_for_capacity(Kind kind, bool notAttempted)
    {
        var outcome = kind switch
        {
            Kind.Opened => await OpenedAsync(),
            Kind.FailedOnItsOwn => await FailedOnItsOwnAsync(),
            Kind.OutOfTimeWhileItWasBeingOpened => await OutOfTimeWhileBeingOpenedAsync(ignoresItsToken: false),
            Kind.OutOfTimeWhileItsLookupIgnoredItsToken => await OutOfTimeWhileBeingOpenedAsync(ignoresItsToken: true),
            Kind.OutOfTimeAfterItsCallWasCreatedAndBeforeItsCodeStarted => await EndedAtTheTargetsStartAsync(afterItsCallWasCreated: true),
            Kind.OutOfTimeAfterAWorkerTookItAndBeforeItsCallWasCreated => await EndedAtTheTargetsStartAsync(afterItsCallWasCreated: false),
            Kind.OutOfTimeBeforeAnyWorkerTookIt => await NoWorkerTookItAsync(),
            Kind.TakenWithLessThanTheFloorLeft => await TakenWithLessThanTheFloorLeftAsync(),
            Kind.RefusedForItsSendersBound => await RefusedAsync(Kind.RefusedForItsSendersBound),
            Kind.RefusedForTheBoundOfMessagesNamingNoSender => await RefusedAsync(Kind.RefusedForTheBoundOfMessagesNamingNoSender),
            Kind.RefusedForTheContainersBound => await RefusedAsync(Kind.RefusedForTheContainersBound),
            Kind.HeldBack => await HeldBackAsync(),
            Kind.NotUtf8 => await MalformedAsync(new byte[] { 0xC3, 0x28 }, "not valid UTF-8"),
            Kind.Empty => await MalformedAsync(Array.Empty<byte>(), "is empty"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        outcome.NotAttempted.Should().Be(notAttempted, "{0}", kind);
    }

    // ---- the kinds ------------------------------------------------------------------------------

    private static async Task<DeliveredOutcome> OpenedAsync()
    {
        var unpacker = new DeliveredMessageUnpacker((packed, _) => Task.FromResult(UnpackFakes.Opened(TestJwe.LabelOf(packed))), Budget, new ManualTimeProvider());

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();

        outcome.Succeeded.Should().BeTrue();
        return outcome;
    }

    private static async Task<DeliveredOutcome> FailedOnItsOwnAsync()
    {
        var unpacker = new DeliveredMessageUnpacker((_, _) => throw new CryptoException("JWE could not be decrypted."), Budget, new ManualTimeProvider());

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single();

        outcome.Error.Should().BeOfType<CryptoException>();
        return outcome;
    }

    // Alone in its delivery, its host call running when the budget ends.
    private static async Task<DeliveredOutcome> OutOfTimeWhileBeingOpenedAsync(bool ignoresItsToken)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var never = new TaskCompletionSource<DeliveredUnpack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unpacker = new DeliveredMessageUnpacker(NeverOpens.Lookup(ignoresItsToken ? WhenCancelled.RunsOn : WhenCancelled.Ends, calls, new RegistrationGate(), never.Task), Budget, time);
        try
        {
            var delivery = unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default);
            await calls.WaitForAsync(1, RealTime);
            time.Advance(Budget);
            var outcome = (await delivery.WaitAsync(RealTime)).Single();

            calls.Labels.Should().Equal(Target);
            outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("while it was being opened");
            return outcome;
        }
        finally
        {
            never.TrySetException(new CryptoException("JWE could not be decrypted."));
        }
    }

    // The boundary between "its call was created" and "it was not": the budget ends at the target's
    // start, alone in its delivery, in one of two windows. Before its call is created: while its
    // worker reads the clock for the start rule, which is the first reading after the budget's timer
    // was created. After its call is created: on the pool thread that runs its work item, after the
    // task's own cancellation check and before the call's code starts. In both the unpack step is
    // never called for it.
    private static async Task<DeliveredOutcome> EndedAtTheTargetsStartAsync(bool afterItsCallWasCreated)
    {
        var clock = new CountingTimeProvider(new ManualTimeProvider());
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, clock);
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
        }

        SpinWait.SpinUntil(() => Volatile.Read(ref ended) == 1, RealTime).Should().BeTrue("the budget ended in its window");
        var outcome = (await delivery.WaitAsync(RealTime)).Single();
        SpinWait.SpinUntil(() => unpacker.Reserved == 0 && unpacker.Opening == 0, RealTime).Should().BeTrue("every reservation came back");

        endError.Should().BeNull();
        Volatile.Read(ref atTheTargetsTake).Should().Be(2, "the window was reached at the target's take");
        Volatile.Read(ref _atNextRestore).Should().BeNull("the armed ending was used");
        calls.Count.Should().Be(0, "the unpack step was never called for it, in either window");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(afterItsCallWasCreated ? "while it was being opened" : "before its turn came");
        return outcome;
    }

    // Behind as many messages that never open as are opened at once: no worker takes it.
    private static async Task<DeliveredOutcome> NoWorkerTookItAsync()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker(async (packed, ct) =>
        {
            using (calls.Enter(TestJwe.LabelOf(packed), ct))
                return await UnpackFakes.StallUntilCancelled(ct);
        }, Budget, time);
        var messages = Enumerable.Range(0, AtOnce).Select(i => From($"did:example:sender-{i}#key-1", $"front-{i}")).Append(Anon(Target)).ToArray();

        var delivery = unpacker.UnpackAllAsync(Bob, messages, "request-1", default);
        await calls.WaitForAsync(AtOnce, RealTime);
        time.Advance(Budget);
        var outcome = (await delivery.WaitAsync(RealTime))[AtOnce];

        calls.Labels.Should().NotContain(Target);
        unpacker.Settled.Should().Be(0, "the delivery's end recorded every outcome, the target's included");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("before its turn came");
        return outcome;
    }

    // Behind as many messages as are opened at once, the first of which fails on its own one tick
    // after the last instant a message may start: its worker takes the target then, and the start
    // rule refuses it. (The failure is timed by a timer of the manual clock: Task.Delay rounds to
    // milliseconds.)
    private static async Task<DeliveredOutcome> TakenWithLessThanTheFloorLeftAsync()
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
        return outcome;
    }

    // Lookups of earlier deliveries that ignore their tokens hold a bound's reservations after their
    // deliveries ended; the target, which would open, comes in a delivery of its own and is refused.
    private static async Task<DeliveredOutcome> RefusedAsync(Kind kind)
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
            return outcome;
        }
        finally
        {
            never.TrySetException(new CryptoException("JWE could not be decrypted."));
            SpinWait.SpinUntil(() => unpacker.Reserved == 0, RealTime).Should().BeTrue("the lookups ended and gave their reservations back");
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

    private static async Task<DeliveredOutcome> HeldBackAsync()
    {
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(TestJwe.LabelOf(packed), ct);
            throw new CryptoException("JWE could not be decrypted.");
        }, Budget, new ManualTimeProvider());
        (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-1", default).WaitAsync(RealTime)).Single().Error.Should().BeOfType<CryptoException>();

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { Anon(Target) }, "request-2", default).WaitAsync(RealTime)).Single();

        calls.Count.Should().Be(1, "it is not opened again while it is held back");
        outcome.Error.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("did not open when it was delivered before");
        return outcome;
    }

    private static async Task<DeliveredOutcome> MalformedAsync(byte[] bytes, string expected)
    {
        var calls = new HostCallLog();
        var unpacker = new DeliveredMessageUnpacker((packed, ct) =>
        {
            calls.Enter(packed, ct);
            return Task.FromResult(UnpackFakes.Opened(packed));
        }, Budget, new ManualTimeProvider());

        var outcome = (await unpacker.UnpackAllAsync(Bob, new[] { new DeliveredInput(Target, bytes) }, "request-1", default).WaitAsync(RealTime)).Single();

        calls.Count.Should().Be(0);
        outcome.Error.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain(expected);
        return outcome;
    }

    // ---- helpers --------------------------------------------------------------------------------

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
