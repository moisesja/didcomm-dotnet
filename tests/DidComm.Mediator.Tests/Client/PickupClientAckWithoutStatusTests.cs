using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> when the mediator sends no <c>status</c> after a
/// <c>messages-received</c> (#98 F-1; Message Pickup 3.0: it SHOULD, not MUST), on the fake exchange
/// and its manual clock: the client asks with a <c>status-request</c> and returns that status, and a
/// message and the <c>status-request</c> after it share one <see cref="MediatorClientOptions.RequestTimeout"/>.
/// Over a WebSocket the client waits half of it for the status, then asks in the other half; over
/// HTTP (the empty response modelled by <see cref="SilentExchange"/>) it asks at once, in what is
/// left. The end-to-end shape against a real HTTP mediator that answers 202 is in the interop
/// tests' <c>PickupClientAckWithoutStatusTests</c>.
/// </summary>
public sealed class PickupClientAckWithoutStatusTests
{
    // The default RequestTimeout, and the two halves a WebSocket acknowledgement splits it into.
    private static readonly TimeSpan Budget = new MediatorClientOptions().RequestTimeout;
    private static readonly TimeSpan Half = Budget / 2;

    // ---- over a WebSocket -----------------------------------------------------------------------

    [Fact]
    public async Task Over_a_WebSocket_no_status_within_half_the_RequestTimeout_is_followed_by_a_status_request_whose_status_is_returned()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute }
            .On(MessagePickup.MessagesReceivedType, Unanswered)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 3));
        var clock = exchange.Clock;
        var started = clock.GetUtcNow();
        var mark = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });
        await clock.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        clock.Advance(Half).Should().Be(1, "the wait for the status is the only timer due at half the budget");
        var status = await PickupTest.Within(acking);

        status.MessageCount.Should().Be(3, "the status the status-request was answered with");
        exchange.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.StatusRequestType);
        var ask = exchange.SentOf(MessagePickup.StatusRequestType).Single();
        ask.Spec.Should().BeSameAs(DeliveryReader.StatusRequestThreadedSpec, "a status without thid could be the late answer to the chunk given up");
        ask.RecipientDid.Should().BeNull("it asks about all of the mailbox's messages");
        ask.Request.From.Should().Be(L);
        (clock.GetUtcNow() - started).Should().Be(Half);
        clock.ScheduledTimers.Should().Be(0, "every deadline of the call was released");
    }

    [Fact]
    public async Task Over_a_WebSocket_when_the_status_request_is_unanswered_too_the_call_fails_with_a_TimeoutException_at_exactly_one_RequestTimeout()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute }
            .On(MessagePickup.MessagesReceivedType, Unanswered)
            .On(MessagePickup.StatusRequestType, Unanswered);
        var clock = exchange.Clock;
        var started = clock.GetUtcNow();
        var first = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });
        await clock.WaitForTimerAsync(Half, first, PickupTest.Timeout);
        var second = clock.Mark();
        clock.Advance(Half).Should().Be(1);
        await exchange.WaitForSentAsync(2);
        // The status-request's deadline is what is left of the budget, never a RequestTimeout of its own.
        await clock.WaitForTimerAsync(Half, second, PickupTest.Timeout);
        clock.Advance(Half - TimeSpan.FromTicks(1)).Should().Be(0, "nothing comes due before the budget is spent");
        acking.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        var acknowledgement = exchange.SentOf(MessagePickup.MessagesReceivedType).Single().Request.Id;
        var ask = exchange.SentOf(MessagePickup.StatusRequestType).Single().Request.Id;
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(acknowledgement).And.Contain(ask).And.NotContain(L);
        (clock.GetUtcNow() - started).Should().Be(Budget, "the message and the status-request after it share one RequestTimeout");
        exchange.Sent.Should().HaveCount(2, "the status is asked for once");
    }

    [Fact]
    public async Task Over_a_WebSocket_a_chunk_left_without_a_status_is_followed_by_the_next_after_half_the_RequestTimeout_and_the_last_status_is_returned()
    {
        var acknowledgements = 0;
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType,
            r => Interlocked.Increment(ref acknowledgements) == 1 ? Unanswered(r) : Task.FromResult(Status(r, count: 4)));
        var clock = exchange.Clock;
        var mark = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, Ids(150));
        await clock.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        clock.Advance(Half).Should().Be(1);
        var status = await PickupTest.Within(acking);

        status.MessageCount.Should().Be(4, "the status after the last message confirms both");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Select(s => s.Ids.Count).Should().Equal(100, 50);
        exchange.SentOf(MessagePickup.MessagesReceivedType).Select(s => s.Spec).Should().Equal(
            new[] { DeliveryReader.MessagesReceivedSpec, DeliveryReader.MessagesReceivedThreadedSpec },
            (a, b) => ReferenceEquals(a, b), "after chunk 1 was given up, chunk 2 takes only a status that names it");
        exchange.SentOf(MessagePickup.StatusRequestType).Should().BeEmpty("the last message was answered with a status");
    }

    [Fact]
    public async Task Over_a_WebSocket_a_mediator_that_sends_its_status_gets_one_message_per_chunk_and_no_status_request()
    {
        var chunk = 0;
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType, r => Status(r, count: 10 - ++chunk));

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Ids(150)));

        status.MessageCount.Should().Be(8);
        exchange.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
        exchange.Clock.ScheduledTimers.Should().Be(0, "each wait for a status ended with its answer");
    }

    [Fact]
    public async Task Over_a_WebSocket_the_callers_cancellation_while_waiting_for_the_status_is_an_OperationCanceledException_with_the_callers_token()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType, Unanswered);
        using var cts = new CancellationTokenSource();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }, ct: cts.Token);
        await exchange.WaitForSentAsync(1);
        cts.Cancel();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeAssignableTo<OperationCanceledException>().Which.CancellationToken.Should().Be(cts.Token);
        exchange.Sent.Should().ContainSingle("nothing is asked after a cancellation");
    }

    // ---- either route ---------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_problem_report_answering_messages_received_is_thrown_as_it_is_and_no_status_request_follows(bool webSocket)
    {
        var problem = new MediationProtocolException("The mediator reported a problem.", "r1", "e.p.req.x");
        var exchange = new FakePickupExchange { Route = webSocket ? WebSocketRoute : HttpRoute }
            .On(MessagePickup.MessagesReceivedType, _ => Task.FromException<InboundMessageSnapshot>(problem));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" })));

        thrown.Should().BeSameAs(problem);
        exchange.Sent.Should().ContainSingle();
    }

    // ---- over HTTP (the empty response) ---------------------------------------------------------

    [Fact]
    public async Task Over_HTTP_an_empty_response_is_followed_at_once_by_a_status_request_whose_status_is_returned()
    {
        var fake = new FakePickupExchange()
            .On(MessagePickup.MessagesReceivedType, _ => SilentExchange.NoAnswer)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 0));

        var status = await PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" }));

        status.MessageCount.Should().Be(0);
        fake.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.StatusRequestType);
        fake.Clock.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_the_status_request_gets_only_what_is_left_of_the_messages_RequestTimeout()
    {
        // The empty response comes 20 s into a 30 s budget: the status-request has 10 s.
        var fake = new FakePickupExchange();
        fake.On(MessagePickup.MessagesReceivedType, _ =>
            {
                fake.Clock.Advance(TimeSpan.FromSeconds(20));
                return SilentExchange.NoAnswer;
            })
            .On(MessagePickup.StatusRequestType, Unanswered);
        var clock = fake.Clock;
        var started = clock.GetUtcNow();
        var mark = clock.Mark();

        var acking = new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" });
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(10), mark, PickupTest.Timeout);
        clock.Advance(TimeSpan.FromSeconds(10)).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("status-request");
        (clock.GetUtcNow() - started).Should().Be(Budget);
        fake.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Over_HTTP_an_empty_response_after_the_whole_RequestTimeout_is_a_TimeoutException_and_nothing_is_asked()
    {
        var fake = new FakePickupExchange();
        fake.On(MessagePickup.MessagesReceivedType, _ =>
        {
            fake.Clock.Advance(Budget);
            return SilentExchange.NoAnswer;
        });

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" })));

        var acknowledgement = fake.Sent.Single().Request.Id;
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(acknowledgement).And.Contain("no time was left");
        fake.Sent.Should().ContainSingle("no status-request is sent without time to wait for it");
    }

    [Fact]
    public async Task Over_HTTP_a_status_about_one_DID_does_not_answer_the_status_request_about_all()
    {
        var fake = new FakePickupExchange()
            .On(MessagePickup.MessagesReceivedType, _ => SilentExchange.NoAnswer)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 0, recipientDid: "did:example:carol"));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" })));

        thrown.Should().BeOfType<MediationProtocolException>().Which.RequestId.Should().Be(fake.SentOf(MessagePickup.StatusRequestType).Single().Request.Id);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // A request the mediator never answers: it ends only when the token it was sent with is cancelled,
    // the way the channel ends a wait the caller (here, the client's own deadline) gave up on.
    private static async Task<InboundMessageSnapshot> Unanswered(SentRequest request)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, request.Ct);
        throw new InvalidOperationException("A delay without end ended.");
    }

    private static string[] Ids(int count) => Enumerable.Range(0, count).Select(i => $"q{i}").ToArray();

    /// <summary>
    /// <see cref="FakePickupExchange"/> plus the empty HTTP response: a request its handler answers
    /// with <see cref="NoAnswer"/> gets <c>null</c> from
    /// <see cref="IMediatorExchange.ExchangeAllowingNoAnswerAsync"/> and, like the channel, a
    /// <see cref="TimeoutException"/> from <see cref="IMediatorExchange.ExchangeAsync"/>.
    /// </summary>
    private sealed class SilentExchange(FakePickupExchange inner) : IMediatorExchange
    {
        public static readonly InboundMessageSnapshot NoAnswer = Snapshot(new Message { Id = "no-answer", Type = "https://example.org/no-answer/1.0/none" });

        public MediatorClientOptions Options => inner.Options;

        public TimeProvider Time => inner.Time;

        public DuplicateFilter Duplicates => inner.Duplicates;

        public NegotiatedVersionCache Versions => inner.Versions;

        public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
            => inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

        public async Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        {
            var answer = await inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
            return ReferenceEquals(answer, NoAnswer)
                ? throw new TimeoutException($"Mediator request '{request.Id}' ({spec.Name}) got no answer in the HTTP response.")
                : answer;
        }

        public async Task<InboundMessageSnapshot?> ExchangeAllowingNoAnswerAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        {
            var answer = await inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
            return ReferenceEquals(answer, NoAnswer) ? null : answer;
        }

        public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);

        public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
            => inner.UnpackDeliveredBatchAsync(payloads, requestId, ct);
    }
}
