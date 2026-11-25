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
/// RT3 (#111, fix round 2): how the unpacker tells a message's own failure from the end of its
/// delivery, in the orderings <see cref="DeliveredUnpackTokenStateTests"/> does not set up. The
/// token a delivery is opened with may itself be cancelled by another token (a live stream
/// enumerated with two tokens hands its deliveries the combination of both), and host code may
/// report its cancellation as a failure from a callback on the token the caller cancelled. What is
/// checked: the delivery ends cancelled and the message is not remembered, as for any caller's
/// cancellation.
/// </summary>
/// <remarks>
/// As in <see cref="DeliveredUnpackTokenStateTests"/>: a lookup registers its callback only once the
/// test has seen the delivery start and the lookup is parked (<see cref="RegistrationGate"/>), so the
/// order of the callbacks is fixed, and every cancellation is made from a thread-pool thread. Real
/// time is only a hang guard.
/// </remarks>
public sealed class DeliveredUnpackTokenStateRt3Tests
{
    private static readonly TimeSpan Budget = new MediatorClientOptions().UnpackTimeout; // 20 s
    private static readonly TimeSpan RealTime = TimeSpan.FromSeconds(10);
    private static readonly DeliveryScope Bob = new(M, L);

    /// <summary>
    /// A delivery opened with a token that two caller tokens cancel (the combination a compiler
    /// makes for an enumeration given two tokens). The caller cancels one of the two while the
    /// message is being opened, and the lookup, which watches that token, reports the cancellation
    /// as a resolution failure from its callback there. The delivery ends cancelled and the message
    /// is not remembered.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: the delivery ends with the lookup's failure as the
    /// message's outcome, and the message is remembered.
    /// </remarks>
    [Fact(Skip = "RT3: whether a failure reported when the caller cancels a token the delivery's token is combined from is taken as the message's own")]
    public async Task A_failure_reported_when_the_caller_cancels_one_of_two_combined_tokens_leaves_the_result_cancelled_and_nothing_remembered()
    {
        var time = new ManualTimeProvider();
        var calls = new HostCallLog();
        var register = new RegistrationGate();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(first.Token, second.Token);
        var unpacker = new DeliveredMessageUnpacker(CallbackLookups.ReportsCancellationAsFailure(calls, register, on: _ => first.Token), Budget, time);

        var delivery = unpacker.UnpackAllAsync(Bob, new[] { Message("alice-1") }, "request-1", combined.Token);
        await register.OpenAsync(1, RealTime); // the worker waits on its call: the lookup's callback is registered last
        await calls.WaitForAsync(1, RealTime);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Run(first.Cancel).WaitAsync(RealTime);
        await Ended(delivery);

        delivery.Status.Should().Be(TaskStatus.Canceled, "the caller cancelled: what its lookup reported then is not the message's own failure ({0})", Describe(delivery, unpacker));
        unpacker.Memory.Count.Should().Be(0, "a message still running when the caller cancelled is not remembered");
    }

    /// <summary>
    /// The same through a live stream: <see cref="PickupClient.ReceiveLiveAsync"/> is given one
    /// token and its enumeration another, a drained message is being opened, and the caller cancels
    /// the first token; the lookup watches that token and reports the cancellation as a resolution
    /// failure from its callback there. The message is not remembered: a later stream or fetch of
    /// the same container opens it.
    /// </summary>
    /// <remarks>
    /// Observed on the head of fix round 2: the message is remembered (one entry in the memory, and
    /// the message is held back).
    /// </remarks>
    [Fact(Skip = "RT3: whether a message being opened when the caller cancels one of a live stream's two tokens is remembered")]
    public async Task A_message_being_opened_when_the_caller_cancels_one_of_a_streams_two_tokens_is_not_remembered()
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
            first.Token.UnsafeRegister(_ => ends.TrySetException(new DidResolutionException("did:example:alice", "notFound")), null);
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
        memory.Count.Should().Be(0, "a message still being opened when the caller cancelled is not remembered");
        memory.HeldBack(Bob, new[] { "q1" }).Single().Should().BeFalse("the next stream or fetch opens it");
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
