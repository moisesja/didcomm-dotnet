using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// RT3 (#111, fix round 2), decided in fix round 3: how the unpacker tells a message's own failure
/// from the end of its delivery when the token the delivery is opened with is itself cancelled by
/// another token (a live stream enumerated with two tokens hands its deliveries the combination of
/// both), in the orderings <see cref="DeliveredUnpackTokenStateTests"/> does not set up.
/// </summary>
/// <remarks>
/// <para>
/// What the tests show, and why. The unpacker decides by the state of the tokens it can read: the
/// delivery's own (its budget), the one the delivery was opened with, and the owner's shutdown. A
/// token that the one it was given was combined from is not visible to it. Host code that watches
/// such a token, and reports its cancellation as a failure from a callback there, reports it
/// before the combination is cancelled: none of the tokens the unpacker reads is cancelled yet, so
/// the failure is the message's own and the message is remembered. Host code that watches the
/// token it is handed reports the same failure once the delivery is being ended: the delivery ends
/// cancelled and nothing is remembered. This is a documented residual, not a defect to fix here:
/// the client is handed one token, and host code should watch the token it is handed.
/// </para>
/// <para>
/// As in <see cref="DeliveredUnpackTokenStateTests"/>: a lookup registers its callback only once the
/// test has seen the delivery start and the lookup is parked (<see cref="RegistrationGate"/>), so the
/// order of the callbacks is fixed, and every cancellation is made from a thread-pool thread. Real
/// time is only a hang guard.
/// </para>
/// </remarks>
public sealed class DeliveredUnpackTokenStateRt3Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(M, L);

    /// <summary>
    /// A delivery opened with a token that two caller tokens cancel (the combination a compiler
    /// makes for an enumeration given two tokens). The caller cancels one of the two while the
    /// message is being opened, and the lookup reports the cancellation as a resolution failure
    /// from its callback on the token it watches.
    /// <list type="bullet">
    /// <item>
    /// It watches the token it was handed: the delivery ends cancelled and the message is not
    /// remembered, as for any caller's cancellation.
    /// </item>
    /// <item>
    /// It watches the caller's token that was cancelled, one the delivery's token was combined
    /// from: the delivery ends with the lookup's failure as the message's outcome, and the message
    /// is remembered (the residual: see the class remarks).
    /// </item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failure_reported_from_one_of_two_combined_caller_tokens_is_the_messages_own_and_one_from_the_token_handed_ends_the_delivery_cancelled(bool theLookupWatchesTheTokenItWasHanded)
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(first.Token, second.Token);
        var unpacker = new DeliveredMessageUnpacker(
            CallbackLookups.ReportsCancellationAsFailure(calls, register, on: handed => theLookupWatchesTheTokenItWasHanded ? handed : first.Token), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", combined.Token);
        await register.OpenAsync(1, RealTime); // the worker waits on its call: the lookup's callback is registered last
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(first.Cancel).WaitAsync(RealTime);
        await Ended(delivery);

        if (theLookupWatchesTheTokenItWasHanded)
        {
            delivery.Status.Should().Be(TaskStatus.Canceled, "the caller cancelled: what the lookup reported then is not the message's own failure ({0})", Describe(delivery, unpacker));
            unpacker.Memory.Count.Should().Be(0, "a message still running when the caller cancelled is not remembered");
            return;
        }
        delivery.Status.Should().Be(TaskStatus.RanToCompletion, "the failure was reported before any token the unpacker reads was cancelled ({0})", Describe(delivery, unpacker));
        var outcome = (await delivery).Should().ContainSingle().Subject;
        outcome.Error.Should().BeOfType<DidResolutionException>("what the lookup reported is the message's outcome");
        unpacker.Memory.Count.Should().Be(1, "a failure of its own is remembered");
        unpacker.Memory.HeldBack(Bob, new[] { "alice-1" }).Should().Equal(new[] { true }, "and the message is held back when it is delivered again");
    }

    /// <summary>
    /// The same through a live stream: <see cref="PickupClient.ReceiveLiveAsync"/> is given one
    /// token and its enumeration another, a drained message is being opened, and the caller cancels
    /// the first token; the lookup reports the cancellation as a resolution failure from its
    /// callback on the token it watches.
    /// <list type="bullet">
    /// <item>
    /// It watches the token it was handed: the message is not remembered, and a later stream or
    /// fetch of the same container opens it.
    /// </item>
    /// <item>
    /// It watches the token the caller cancelled (the one given to
    /// <see cref="PickupClient.ReceiveLiveAsync"/>): the message is remembered, and held back when
    /// it is delivered again (the residual: see the class remarks).
    /// </item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_message_being_opened_when_one_of_a_streams_two_tokens_is_cancelled_is_remembered_unless_its_lookup_watches_the_token_it_was_handed(bool theLookupWatchesTheTokenItWasHanded)
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.UseBudget(Budget);
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        exchange.Unpacker = async (text, ct) =>
        {
            var ends = new TaskCompletionSource<DeliveredOutcome>(); // its continuations run inline, inside the callback
            await register.Parked();
            (theLookupWatchesTheTokenItWasHanded ? ct : first.Token).UnsafeRegister(_ => ends.TrySetException(new DidResolutionException("did:example:alice", "notFound")), null);
            return await new ParkedAwaitable<DeliveredOutcome>(ends.Task, () => calls.Enter(text, ct));
        };
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));

        var stream = client.ReceiveLiveAsync(M, L, ct: first.Token).GetAsyncEnumerator(second.Token);
        var moving = stream.MoveNextAsync().AsTask();
        await register.OpenAsync(1, RealTime); // the delivery is being opened: its lookup is parked
        await calls.WaitForAsync(1, RealTime);
        await Task.Run(first.Cancel).WaitAsync(RealTime);
        await Ended(moving);
        await stream.DisposeAsync().AsTask().WaitAsync(RealTime);

        var memory = exchange.Budgeted!.Memory;
        if (theLookupWatchesTheTokenItWasHanded)
        {
            memory.Count.Should().Be(0, "a message still being opened when the caller cancelled is not remembered");
            memory.HeldBack(Bob, new[] { "q1" }).Single().Should().BeFalse("the next stream or fetch opens it");
            return;
        }
        memory.Count.Should().Be(1, "the failure was reported before any token the unpacker reads was cancelled: it is the message's own");
        memory.HeldBack(Bob, new[] { "q1" }).Single().Should().BeTrue("and the message is held back when it is delivered again");
    }

    /// <summary>
    /// The neighbour that holds: the same stream given one token only (to
    /// <see cref="PickupClient.ReceiveLiveAsync"/>), cancelled while the drained message is being
    /// opened, with the same lookup. The enumeration ends cancelled and the message is not
    /// remembered.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_message_being_opened_when_the_caller_cancels_a_streams_only_token_is_not_remembered(bool tokenGivenToTheEnumeration)
    {
        var (exchange, client) = PickupClientLiveStreamTests.Live();
        exchange.UseBudget(Budget);
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var caller = new CancellationTokenSource();
        exchange.Unpacker = async (text, ct) =>
        {
            var ends = new TaskCompletionSource<DeliveredOutcome>(); // its continuations run inline, inside the callback
            await register.Parked();
            caller.Token.UnsafeRegister(_ => ends.TrySetException(new DidResolutionException("did:example:alice", "notFound")), null);
            return await new ParkedAwaitable<DeliveredOutcome>(ends.Task, () => calls.Enter(text, ct));
        };
        exchange.OnSequence(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")), r => Status(r));

        var stream = tokenGivenToTheEnumeration
            ? client.ReceiveLiveAsync(M, L).GetAsyncEnumerator(caller.Token)
            : client.ReceiveLiveAsync(M, L, ct: caller.Token).GetAsyncEnumerator();
        var moving = stream.MoveNextAsync().AsTask();
        await register.OpenAsync(1, RealTime);
        await calls.WaitForAsync(1, RealTime);
        await Task.Run(caller.Cancel).WaitAsync(RealTime);

        await FluentActions.Awaiting(() => moving.WaitAsync(RealTime)).Should().ThrowAsync<OperationCanceledException>("the caller cancelled the enumeration");
        await stream.DisposeAsync().AsTask().WaitAsync(RealTime);
        exchange.Budgeted!.Memory.Count.Should().Be(0, "a message still being opened when the caller cancelled is not remembered");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static DeliveredInput Message(string id) => new(id, TestJwe.Bytes(TestJwe.Anoncrypt(id)));

    // What a delivery that did not end cancelled ended with, for a failing assertion's text.
    private static string Describe(Task<IReadOnlyList<DeliveredOutcome>> delivery, DeliveredMessageUnpacker unpacker)
        => delivery.IsCompletedSuccessfully
            ? $"it ended with {string.Join(", ", delivery.Result.Select(o => o.Succeeded ? "a success" : o.Error!.GetType().Name))}; remembered: {unpacker.Memory.Count}"
            : $"it ended {delivery.Status}; remembered: {unpacker.Memory.Count}";

    // Wait (bounded) for a task to end, however it ends.
    private static Task Ended(Task task) => task.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(RealTime);
}
