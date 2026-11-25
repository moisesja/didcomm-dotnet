using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;
using static DidComm.Mediator.Tests.Client.PickupClientLiveStreamTests;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The live stream when one of its requests fails after the caller's token was cancelled (#111 fix
/// round 2): whether that is the caller's cancellation or the request's own failure is decided by
/// the token's state, never by the exception's type. A step may report its cancellation as a failure
/// of its own (a timeout, a failed connection, a refusal); the enumeration then still ends with the
/// cancellation, and nothing is logged or armed for a retry.
/// </summary>
/// <remarks>
/// The held request fails from inside its own cancellation callback, so the stream observes the
/// failure with the token already cancelled, whatever the order of continuations. The token is
/// cancelled from a thread-pool thread. The clock is manual; real time is only a hang guard.
/// </remarks>
public sealed class LiveStreamCancelledStepTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);

    /// <summary>The request in flight when the caller cancels.</summary>
    public enum Step
    {
        /// <summary>A cycle's <c>live-delivery-change</c>.</summary>
        LiveOn,

        /// <summary>A drain round's <c>delivery-request</c>.</summary>
        DrainRound,

        /// <summary>A <c>messages-received</c>.</summary>
        Acknowledgement,
    }

    /// <summary>What the request reports when its token is cancelled.</summary>
    public enum Reported
    {
        /// <summary>A <see cref="TimeoutException"/> (retried while the token is not cancelled).</summary>
        Timeout,

        /// <summary>A <see cref="TransportException"/> (retried while the token is not cancelled).</summary>
        Transport,

        /// <summary>The container's bound of waiting requests (retried while the token is not cancelled).</summary>
        Busy,

        /// <summary>An <see cref="InvalidOperationException"/> no retry fixes (ends the stream while the token is not cancelled).</summary>
        Refusal,
    }

    /// <summary>
    /// Past the give-up point (a run of failures older than RequestTimeout + 30 s, where a retryable
    /// failure ends the stream with its own exception): the caller cancels while a request is in
    /// flight, and the request reports that as a failure. The enumeration ends with an
    /// <see cref="OperationCanceledException"/>, not with what the request reported.
    /// </summary>
    [Theory]
    [InlineData(Step.LiveOn, Reported.Timeout)]
    [InlineData(Step.LiveOn, Reported.Transport)]
    [InlineData(Step.LiveOn, Reported.Busy)]
    [InlineData(Step.LiveOn, Reported.Refusal)]
    [InlineData(Step.DrainRound, Reported.Timeout)]
    [InlineData(Step.DrainRound, Reported.Transport)]
    [InlineData(Step.DrainRound, Reported.Busy)]
    [InlineData(Step.DrainRound, Reported.Refusal)]
    [InlineData(Step.Acknowledgement, Reported.Timeout)]
    [InlineData(Step.Acknowledgement, Reported.Transport)]
    [InlineData(Step.Acknowledgement, Reported.Busy)]
    [InlineData(Step.Acknowledgement, Reported.Refusal)]
    public async Task A_request_that_fails_after_the_caller_cancelled_ends_the_stream_with_the_cancellation_and_not_its_own_exception(Step step, Reported reported)
    {
        var (exchange, client) = Live(o => o.LivePollInterval = Interval);
        var held = new HeldRequest(reported);
        var liveOns = 0;
        var acks = 0;
        // The run's first failure is the step's first attempt (no answer); its second attempt is held.
        exchange.On(MessagePickup.LiveDeliveryChangeType, r =>
        {
            if (!r.Live)
                return Task.FromResult(Status(r, live: false));
            var n = Interlocked.Increment(ref liveOns);
            return step switch
            {
                Step.LiveOn => n == 1 ? NoAnswer() : held.Hold(r),
                Step.DrainRound => n == 1 ? NoAnswer() : Task.FromResult(Status(r, live: true)),
                _ => Task.FromResult(Status(r, live: true)),
            };
        });
        exchange.On(MessagePickup.DeliveryRequestType, r => step switch
        {
            Step.DrainRound => held.Hold(r),
            _ => Task.FromResult(Delivery(r, ("q1", "ok:m1"))),
        });
        exchange.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) == 1 ? NoAnswer() : held.Hold(r));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        var mark = exchange.Clock.Mark();
        if (step == Step.Acknowledgement)
        {
            (await Next(stream)).Id.Should().Be("q1");
            mark = exchange.Clock.Mark();
        }
        var move = stream.MoveNextAsync().AsTask(); // the first attempt gets no answer: the run's first failure
        await exchange.Clock.WaitForTimerAsync(LiveDeliveryStream.FirstBackoff, mark, PickupTest.Timeout);
        exchange.Clock.Advance(LiveDeliveryStream.FirstBackoff).Should().Be(1, "the back-off ends");
        await held.InFlight.WaitAsync(PickupTest.Timeout);
        // The run's give-up alarm, armed at the first failure, fires while the request is in flight.
        exchange.Clock.Advance(exchange.Options.RequestTimeout + LiveDeliveryStream.GiveUpMargin).Should().BeGreaterThanOrEqualTo(1);
        move.IsCompleted.Should().BeFalse("the request is held");

        await Task.Run(() => cts.Cancel());
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(move));
        await PickupTest.Within(stream.DisposeAsync());

        held.Failed.Should().BeTrue("the request reported its cancellation as {0}", reported);
        thrown.Should().BeAssignableTo<OperationCanceledException>(
            "the caller cancelled: what the request reported after that ({0}) is the cancellation, not a failure of its own", reported);
        Types(exchange).Should().EndWith(LiveOff, "the end still turns live mode off");
    }

    /// <summary>
    /// Before the give-up point (where a retryable failure backs off): the caller cancels while a
    /// request is in flight, and the request reports that as a failure. The enumeration ends with
    /// the cancellation, and no failure is logged: neither a failed request with a retry delay, nor
    /// a failed acknowledgement.
    /// </summary>
    [Theory]
    [InlineData(Step.LiveOn, Reported.Timeout)]
    [InlineData(Step.LiveOn, Reported.Transport)]
    [InlineData(Step.DrainRound, Reported.Timeout)]
    [InlineData(Step.DrainRound, Reported.Transport)]
    [InlineData(Step.Acknowledgement, Reported.Timeout)]
    [InlineData(Step.Acknowledgement, Reported.Transport)]
    public async Task A_request_that_fails_after_the_caller_cancelled_is_not_logged_as_a_failure_to_retry(Step step, Reported reported)
    {
        var exchange = new FakePickupExchange(o => o.LivePollInterval = Interval) { Route = WebSocketRoute };
        var logs = new PickupTestLogs();
        var client = PickupTest.Client(exchange, logs);
        var held = new HeldRequest(reported);
        var acks = 0;
        exchange.On(MessagePickup.LiveDeliveryChangeType, r => !r.Live
            ? Task.FromResult(Status(r, live: false))
            : step == Step.LiveOn ? held.Hold(r) : Task.FromResult(Status(r, live: true)));
        exchange.On(MessagePickup.DeliveryRequestType, r => step == Step.DrainRound ? held.Hold(r) : Task.FromResult(Delivery(r, ("q1", "ok:m1"))));
        // The stream's acknowledgement is held; the one its end sends afterwards is accepted.
        exchange.On(MessagePickup.MessagesReceivedType, r => Interlocked.Increment(ref acks) == 1 ? held.Hold(r) : Task.FromResult(Status(r)));
        using var cts = new CancellationTokenSource();
        var stream = client.ReceiveLiveAsync(M, L, ct: cts.Token).GetAsyncEnumerator();

        if (step == Step.Acknowledgement)
            (await Next(stream)).Id.Should().Be("q1");
        var move = stream.MoveNextAsync().AsTask();
        await held.InFlight.WaitAsync(PickupTest.Timeout);
        move.IsCompleted.Should().BeFalse("the request is held");

        await Task.Run(() => cts.Cancel());
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(move));
        await PickupTest.Within(stream.DisposeAsync());

        held.Failed.Should().BeTrue("the request reported its cancellation as {0}", reported);
        thrown.Should().BeAssignableTo<OperationCanceledException>();
        logs.Entries.Select(e => e.Message).Should().NotContain(m => m.Contains("failed", StringComparison.Ordinal),
            "nothing failed on its own and nothing is retried: the caller cancelled");
    }

    private static Task<InboundMessageSnapshot> NoAnswer() => Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer (test)"));

    /// <summary>
    /// A request whose answer never comes and which, when its token is cancelled, fails with an
    /// exception of its own from inside the cancellation callback (a step that reports its
    /// cancellation as a failure).
    /// </summary>
    private sealed class HeldRequest(Reported reported)
    {
        private readonly TaskCompletionSource<InboundMessageSnapshot> _answer = new();
        private readonly TaskCompletionSource _inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failed;

        /// <summary>Completes once a request is held.</summary>
        public Task InFlight => _inFlight.Task;

        /// <summary>Whether the held request has reported its failure.</summary>
        public bool Failed => Volatile.Read(ref _failed) != 0;

        public Task<InboundMessageSnapshot> Hold(SentRequest request)
        {
            request.Ct.UnsafeRegister(_ =>
            {
                Volatile.Write(ref _failed, 1);
                _answer.TrySetException(reported switch
                {
                    Reported.Timeout => new TimeoutException("reported at the cancellation (test)"),
                    Reported.Transport => new TransportException("reported at the cancellation (test)"),
                    Reported.Busy => new MediatorBusyException("reported at the cancellation (test)"),
                    _ => new InvalidOperationException("reported at the cancellation (test)"),
                });
            }, null);
            _inFlight.TrySetResult();
            return _answer.Task;
        }
    }
}
