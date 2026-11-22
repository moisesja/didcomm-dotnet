using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.AckAsync"/> under Message Pickup 3.0 when the mediator sends no
/// <c>status</c> after a <c>messages-received</c> (#98 F-1; 3.0 says it SHOULD, not that it must),
/// on the fake exchange and its manual clock (#100, PR #143 review round 2, OD-6). The call returns
/// the status that answered the last message, or <c>null</c> when that message got none, and it
/// never asks for a status of its own. Over a WebSocket each message's status is waited for half of
/// <see cref="MediatorClientOptions.RequestTimeout"/>; over HTTP the empty response (modelled by
/// <see cref="SilentExchange"/>) shows at once that none came. A message that could not be sent is
/// an exception, never <c>null</c>. The end-to-end shape against a real HTTP mediator that answers
/// 202 is in the interop tests' <c>PickupClientAckWithoutStatusTests</c>.
/// </summary>
public sealed class PickupClientAckWithoutStatusTests
{
    // The default RequestTimeout, and the half of it a WebSocket acknowledgement waits for its status.
    private static readonly TimeSpan Budget = new MediatorClientOptions().RequestTimeout;
    private static readonly TimeSpan Half = Budget / 2;

    // ---- over a WebSocket -----------------------------------------------------------------------

    [Fact]
    public async Task Over_a_WebSocket_no_status_within_half_the_RequestTimeout_returns_null_and_nothing_is_asked()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute }
            .On(MessagePickup.MessagesReceivedType, Unanswered)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 3));
        var clock = exchange.Clock;
        var started = clock.GetUtcNow();
        var mark = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });
        await clock.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        clock.Advance(Half - TimeSpan.FromTicks(1)).Should().Be(0);
        acking.IsCompleted.Should().BeFalse("the wait for the status has a tick left");
        clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "the wait for the status is the only timer");
        var status = await PickupTest.Within(acking);

        status.Should().BeNull("no status came, and the call does not ask for one");
        exchange.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType);
        (clock.GetUtcNow() - started).Should().Be(Half);
        clock.ScheduledTimers.Should().Be(0, "every wait of the call was released");
    }

    [Fact]
    public async Task Over_a_WebSocket_a_status_that_answered_the_first_chunk_is_not_returned_when_the_last_chunk_gets_none()
    {
        // OD-6: only the answer to the last message is returned. The first chunk's status is older
        // than the second chunk, so it would count messages that chunk acknowledged.
        var acknowledgements = 0;
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType,
            r => Interlocked.Increment(ref acknowledgements) == 1 ? Task.FromResult(Status(r, count: 50)) : Unanswered(r));
        var clock = exchange.Clock;
        var mark = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, Ids(150));
        await clock.WaitForTimerAsync(Half, mark, PickupTest.Timeout);
        await exchange.WaitForSentAsync(2);
        clock.Advance(Half).Should().Be(1, "only the second chunk's wait is left");
        var status = await PickupTest.Within(acking);

        status.Should().BeNull("the last chunk got no status");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Select(s => s.Ids.Count).Should().Equal(100, 50);
        exchange.SentOf(MessagePickup.StatusRequestType).Should().BeEmpty();
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

        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(4, "the status answering the last message");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Select(s => s.Ids.Count).Should().Equal(100, 50);
        exchange.SentOf(MessagePickup.StatusRequestType).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_a_WebSocket_a_mediator_that_sends_its_status_gets_one_message_per_chunk_and_no_status_request()
    {
        var chunk = 0;
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType, r => Status(r, count: 10 - ++chunk));

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Ids(150)));

        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(8);
        exchange.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
        exchange.Clock.ScheduledTimers.Should().Be(0, "each wait for a status ended with its answer");
    }

    [Fact]
    public async Task Over_a_WebSocket_the_callers_cancellation_while_waiting_for_the_status_is_an_OperationCanceledException_with_the_callers_token()
    {
        // The fake leaves the wait to IMediatorExchange's default ExchangeAllowingNoAnswerAsync,
        // which gives the caller's cancellation back with the caller's token, as the real channel
        // does (MediatorClientChannelAnswerWaitTests).
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.MessagesReceivedType, Unanswered);
        using var cts = new CancellationTokenSource();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }, ct: cts.Token);
        await exchange.WaitForSentAsync(1);
        await cts.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeAssignableTo<OperationCanceledException>("a cancellation is not a missing status")
            .Which.CancellationToken.Should().Be(cts.Token);
        exchange.Sent.Should().ContainSingle("nothing is sent after a cancellation");
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

    public static TheoryData<string, bool> Failures => new()
    {
        { "transport", true },
        { "transport", false },
        { "timeout", true },
        { "timeout", false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_message_that_could_not_be_sent_ends_the_call_with_its_exception_never_null_and_the_later_chunks_are_not_sent(string failure, bool webSocket)
    {
        // The exchange throws what the channel throws: the transport's failure as it is, or a
        // TimeoutException when the send did not complete within RequestTimeout.
        Exception error = failure == "transport" ? new TransportException("the socket closed") : new TimeoutException("not sent within the RequestTimeout");
        var acknowledgements = 0;
        var exchange = new FakePickupExchange { Route = webSocket ? WebSocketRoute : HttpRoute }.On(MessagePickup.MessagesReceivedType,
            r => Interlocked.Increment(ref acknowledgements) == 2 ? Task.FromException<InboundMessageSnapshot>(error) : Task.FromResult(Status(r, count: 1)));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Ids(250))));

        thrown.Should().BeSameAs(error);
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().HaveCount(2, "the call stops at the message that failed");
        exchange.SentOf(MessagePickup.StatusRequestType).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Each_acknowledgement_asks_the_exchange_to_wait_half_the_RequestTimeout_for_its_status_with_the_callers_token(bool webSocket)
    {
        // The channel uses the wait on a WebSocket and ignores it over HTTP; the client always passes it.
        var fake = new FakePickupExchange { Route = webSocket ? WebSocketRoute : HttpRoute };
        var silent = new SilentExchange(fake);
        using var cts = new CancellationTokenSource();

        await PickupTest.Within(new PickupClient(silent, logger: null).AckAsync(M, L, Ids(150), ct: cts.Token));

        silent.AnswerWaits.Should().Equal(new TimeSpan?[] { fake.Options.RequestTimeout / 2, fake.Options.RequestTimeout / 2 }, "one per chunk");
        fake.Sent.Should().HaveCount(2).And.OnlyContain(s => s.Ct == cts.Token);
    }

    // ---- over HTTP (the empty response) ---------------------------------------------------------

    [Fact]
    public async Task Over_HTTP_an_empty_response_returns_null_at_once_and_nothing_is_asked()
    {
        var fake = new FakePickupExchange()
            .On(MessagePickup.MessagesReceivedType, _ => SilentExchange.NoAnswer)
            .On(MessagePickup.StatusRequestType, r => Status(r, count: 0));
        var started = fake.Clock.GetUtcNow();

        var status = await PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" }));

        status.Should().BeNull();
        fake.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType);
        fake.Clock.GetUtcNow().Should().Be(started, "nothing waited on the clock");
        fake.Clock.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public async Task Over_HTTP_a_status_that_answered_the_first_chunk_is_not_returned_when_the_last_chunk_gets_an_empty_response()
    {
        var acknowledgements = 0;
        var fake = new FakePickupExchange().On(MessagePickup.MessagesReceivedType,
            r => Interlocked.Increment(ref acknowledgements) == 1 ? Status(r, count: 50) : SilentExchange.NoAnswer);

        var status = await PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, Ids(150)));

        status.Should().BeNull("only the answer to the last message is returned");
        fake.Sent.Select(s => s.Type).Should().Equal(MessagePickup.MessagesReceivedType, MessagePickup.MessagesReceivedType);
    }

    [Fact]
    public async Task Over_HTTP_an_empty_response_late_in_the_RequestTimeout_is_still_null_and_nothing_is_asked()
    {
        // The empty response comes 20 s into a 30 s RequestTimeout: no status, and nothing follows.
        var fake = new FakePickupExchange();
        fake.On(MessagePickup.MessagesReceivedType, _ =>
            {
                fake.Clock.Advance(TimeSpan.FromSeconds(20));
                return SilentExchange.NoAnswer;
            })
            .On(MessagePickup.StatusRequestType, Unanswered);

        var status = await PickupTest.Within(new PickupClient(new SilentExchange(fake), logger: null).AckAsync(M, L, new[] { "q1" }));

        status.Should().BeNull();
        fake.Sent.Should().ContainSingle();
        fake.Clock.ScheduledTimers.Should().Be(0);
    }

    // ---- helpers ------------------------------------------------------------------------------

    // A request the mediator never answers: it ends only when the token it was sent with is cancelled,
    // the way the channel ends a wait the caller (here, the wait for the status) gave up on.
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
    /// <see cref="TimeoutException"/> from <see cref="IMediatorExchange.ExchangeAsync"/>. It records the
    /// <c>answerWait</c> each acknowledgement was handed.
    /// </summary>
    private sealed class SilentExchange(FakePickupExchange inner) : IMediatorExchange
    {
        public static readonly InboundMessageSnapshot NoAnswer = Snapshot(new Message { Id = "no-answer", Type = "https://example.org/no-answer/1.0/none" });

        private readonly ConcurrentQueue<TimeSpan?> _answerWaits = new();

        /// <summary>The <c>answerWait</c> of every <see cref="ExchangeAllowingNoAnswerAsync"/> call, in order.</summary>
        public IReadOnlyList<TimeSpan?> AnswerWaits => _answerWaits.ToArray();

        public MediatorClientOptions Options => inner.Options;

        public TimeProvider Time => inner.Time;

        public DuplicateFilter Duplicates => inner.Duplicates;

        public NegotiatedVersionCache<CoordinateMediationVersion> Versions => inner.Versions;
        public NegotiatedVersionCache<MessagePickupVersion> PickupVersions => inner.PickupVersions;

        public Task<MediatorRoute> ResolveRouteAsync(string mediatorDid, Uri? serviceEndpointOverride, bool requireWebSocket, CancellationToken ct)
            => inner.ResolveRouteAsync(mediatorDid, serviceEndpointOverride, requireWebSocket, ct);

        public async Task<InboundMessageSnapshot> ExchangeAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, CancellationToken ct)
        {
            var answer = await inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
            return ReferenceEquals(answer, NoAnswer)
                ? throw new TimeoutException($"Mediator request '{request.Id}' ({spec.Name}) got no answer in the HTTP response.")
                : answer;
        }

        public async Task<InboundMessageSnapshot?> ExchangeAllowingNoAnswerAsync(MediatorRoute route, Message request, MediatorExchangeSpec spec, string mediatorDid, string localDid, TimeSpan? answerWait, CancellationToken ct)
        {
            _answerWaits.Enqueue(answerWait);
            var answer = await inner.ExchangeAsync(route, request, spec, mediatorDid, localDid, ct);
            return ReferenceEquals(answer, NoAnswer) ? null : answer;
        }

        public LiveRegistration RegisterLiveStream(string mediatorDid, string localDid) => inner.RegisterLiveStream(mediatorDid, localDid);

        public Task SendOneWayAsync(MediatorRoute route, Message request, string name, string mediatorDid, string localDid, CancellationToken ct)
            => inner.SendOneWayAsync(route, request, name, mediatorDid, localDid, ct);

        public Task<IReadOnlyList<DeliveredOutcome>> UnpackDeliveredBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, string? requestId, CancellationToken ct)
            => inner.UnpackDeliveredBatchAsync(payloads, requestId, ct);
    }
}
