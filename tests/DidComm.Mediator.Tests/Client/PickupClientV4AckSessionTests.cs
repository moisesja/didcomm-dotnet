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
/// #100, PR #143 review P2-2: <see cref="PickupClient.AckAsync"/> under Message Pickup 4.0 on a route
/// that is not HTTP sends one <c>status-request</c> of its own before its first
/// <c>messages-received</c>, as an ordinary request (it is answered, and the answer is read as a
/// status and not returned). A 4.0 <c>messages-received</c> carries no <c>return_route</c> (4.0 L35),
/// and the WebSocket transport sends such a message on the connection that carries this agent's
/// answers only while one is open (FR-RR-05, FR-RR-07): that first request opens it, or reuses the
/// open one, so the acknowledgements and the <c>status-request</c> that confirms them travel on one
/// connection, which the mediator handles in order. Over HTTP, and under 3.0 (whose
/// <c>messages-received</c> asks for a return route itself), nothing is sent first. When the first
/// request fails, nothing is acknowledged. Its time is its own: the confirming
/// <c>status-request</c> still gets what is left of the last message's
/// <see cref="MediatorClientOptions.RequestTimeout"/>. Against the fake exchange (the channel's own
/// deadline and problem-report handling are exercised in <see cref="PickupClientV4AckSessionChannelTests"/>).
/// </summary>
public sealed class PickupClientV4AckSessionTests
{
    // A route that is neither HTTP nor a WebSocket: the channel waits for its answer as on a WebSocket.
    private static readonly MediatorRoute OtherRoute = new(new Uri("didcomm+custom://mediator.example/"), IsHttp: false, IsWebSocket: false);

    public static TheoryData<string> RoutesThatAreNotHttp => new() { "ws", "other" };

    private static FakePickupExchange V4On(MediatorRoute route)
    {
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.V4) { Route = route };
        return exchange;
    }

    private static MediatorRoute RouteOf(string name) => name == "ws" ? WebSocketRoute : OtherRoute;

    // ---- the order of the messages --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RoutesThatAreNotHttp))]
    public async Task Under_4_0_on_a_route_that_is_not_HTTP_AckAsync_asks_first_then_sends_each_chunk_one_way_then_asks_again(string routeName)
    {
        var route = RouteOf(routeName);
        var exchange = V4On(route).OnSequence(MessagePickup40.StatusRequestType,
            r => Status40(r, count: 7),
            r => Status40(r, count: 0));
        var ids = Enumerable.Range(0, 150).Select(i => $"q{i}").ToArray();

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, ids));

        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(
            (MessagePickup40.StatusRequestType, false),
            (MessagePickup40.MessagesReceivedType, true),
            (MessagePickup40.MessagesReceivedType, true),
            (MessagePickup40.StatusRequestType, false));
        var first = exchange.Sent[0];
        first.Spec.Should().BeSameAs(PickupWire.StatusRequestSpec4, "an ordinary request, answered with a 4.0 status");
        first.RecipientDid.Should().BeNull("it asks about all of the mailbox's messages");
        exchange.Sent[1].Ids.Should().Equal(ids[..100]);
        exchange.Sent[2].Ids.Should().Equal(ids[100..]);
        exchange.Sent.Skip(1).Take(2).Should().OnlyContain(s => !HasReturnRoute(s.Request), "the acknowledgements stay one way, without return_route (4.0 L35)");
        exchange.Sent.Should().OnlyContain(s => s.Route == route && s.MediatorDid == M && s.LocalDid == L, "every message goes the same way");
        exchange.Sent[3].Request.Id.Should().NotBe(first.Request.Id, "two requests, each with an id of its own");
        exchange.Sent[3].RecipientDid.Should().BeNull();
        status.MessageCount.Should().Be(0, "the status after the last acknowledgement is returned, not the first one");
    }

    [Fact]
    public async Task Under_4_0_over_HTTP_AckAsync_sends_nothing_before_the_acknowledgement()
    {
        var exchange = V4On(HttpRoute).On(MessagePickup40.StatusRequestType, r => Status40(r, count: 0));

        await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(
            (MessagePickup40.MessagesReceivedType, true), (MessagePickup40.StatusRequestType, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Under_3_0_AckAsync_sends_nothing_before_the_acknowledgement(bool webSocket)
    {
        var exchange = new FakePickupExchange { Route = webSocket ? WebSocketRoute : HttpRoute };

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(new[] { (MessagePickup.MessagesReceivedType, false) },
            "a 3.0 messages-received asks for a return route itself, and the mediator's status answers it");
        status.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Under_4_0_over_a_WebSocket_a_version_remembered_from_another_route_still_gets_the_first_status_request()
    {
        // The version was negotiated earlier (over HTTP, say) and remembered: no query on this route
        // opened its connection, so the first status-request does.
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate) { Route = WebSocketRoute };
        exchange.PickupVersions.Set(M, MessagePickupVersion.V4);

        await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        exchange.Sent.Select(s => s.Type).Should().Equal(
            MessagePickup40.StatusRequestType, MessagePickup40.MessagesReceivedType, MessagePickup40.StatusRequestType);
    }

    // ---- when the first status-request fails ------------------------------------------------------

    [Fact]
    public async Task A_first_status_request_that_times_out_fails_the_call_and_nothing_is_acknowledged()
    {
        // The channel's own deadline (one RequestTimeout) throws this; its text names the request.
        var exchange = V4On(WebSocketRoute).On(MessagePickup40.StatusRequestType,
            r => Task.FromException<InboundMessageSnapshot>(new TimeoutException($"Mediator request '{r.Request.Id}' (status-request) was not answered within 00:00:30.")));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" })));

        var first = exchange.Sent.Should().ContainSingle("nothing is sent after the first status-request fails").Subject;
        first.Type.Should().Be(MessagePickup40.StatusRequestType);
        exchange.AcknowledgedIds.Should().BeEmpty();
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain(first.Request.Id);
    }

    [Fact]
    public async Task A_first_status_request_answered_with_a_problem_report_fails_the_call_and_nothing_is_acknowledged()
    {
        // The channel turns a problem report into this exception (its code sanitized on ProblemCode).
        var exchange = V4On(WebSocketRoute).On(MessagePickup40.StatusRequestType,
            r => Task.FromException<InboundMessageSnapshot>(new MediationProtocolException($"The mediator answered request '{r.Request.Id}' (status-request) with a problem report.", r.Request.Id, "e.p.req.not-enrolled")));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" })));

        var first = exchange.Sent.Should().ContainSingle().Subject;
        exchange.AcknowledgedIds.Should().BeEmpty();
        var protocol = thrown.Should().BeOfType<MediationProtocolException>().Subject;
        protocol.RequestId.Should().Be(first.Request.Id);
        protocol.ProblemCode.Should().Be("e.p.req.not-enrolled");
    }

    [Fact]
    public async Task A_first_status_request_answered_with_a_status_about_one_DID_only_fails_the_call_and_nothing_is_acknowledged()
    {
        // It asked about all of the mailbox's messages: a status about one DID does not answer that.
        var exchange = V4On(WebSocketRoute).On(MessagePickup40.StatusRequestType, r => Status40(r, count: 1, recipientDid: "did:example:carol"));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" })));

        var first = exchange.Sent.Should().ContainSingle().Subject;
        exchange.AcknowledgedIds.Should().BeEmpty();
        thrown.Should().BeOfType<MediationProtocolException>().Which.RequestId.Should().Be(first.Request.Id);
    }

    [Fact]
    public async Task A_first_status_request_answered_with_a_delivery_fails_the_call_and_nothing_is_acknowledged()
    {
        var exchange = V4On(WebSocketRoute).On(MessagePickup40.StatusRequestType, r => Delivery40(r));

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" })));

        exchange.Sent.Should().ContainSingle();
        exchange.AcknowledgedIds.Should().BeEmpty();
        thrown.Should().BeOfType<MediationProtocolException>();
    }

    [Fact]
    public async Task Cancelling_during_the_first_status_request_ends_the_call_and_nothing_is_acknowledged()
    {
        var exchange = V4On(WebSocketRoute).On(MessagePickup40.StatusRequestType, Unanswered);
        using var cts = new CancellationTokenSource();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }, ct: cts.Token);
        await exchange.WaitForSentAsync(1);
        await cts.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        exchange.Sent.Should().ContainSingle().Which.Ct.Should().Be(cts.Token, "the first status-request runs under the caller's token (and the channel's own RequestTimeout)");
        exchange.AcknowledgedIds.Should().BeEmpty();
    }

    // ---- time ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_status_requests_time_is_its_own_and_the_status_request_after_the_last_message_gets_the_rest_of_that_messages_RequestTimeout()
    {
        // The first status-request takes 25 s of its own RequestTimeout (30 s), the one-way send 20 s
        // of the acknowledgement's: the status-request after it waits the acknowledgement's other
        // 10 s, the same as when nothing is sent first. Counted from the start of the call, nothing
        // would be left.
        var exchange = V4On(WebSocketRoute);
        var clock = exchange.Clock;
        var asked = 0;
        exchange.On(MessagePickup40.StatusRequestType, r =>
        {
            if (Interlocked.Increment(ref asked) > 1)
                return Unanswered(r);
            clock.Advance(TimeSpan.FromSeconds(25));
            return Task.FromResult(Status40(r, count: 1));
        });
        exchange.OneWay = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };
        var mark = clock.Mark();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(10), mark, PickupTest.Timeout);
        clock.Advance(TimeSpan.FromSeconds(10)).Should().Be(1);
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        exchange.Sent.Select(s => s.Type).Should().Equal(
            MessagePickup40.StatusRequestType, MessagePickup40.MessagesReceivedType, MessagePickup40.StatusRequestType);
        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should()
            .Contain(exchange.Sent[1].Request.Id, "it names the acknowledgement")
            .And.Contain(exchange.Sent[2].Request.Id, "and the status-request that asked for its status")
            .And.NotContain(exchange.Sent[0].Request.Id);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static async Task<InboundMessageSnapshot> Unanswered(SentRequest request)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, request.Ct);
        throw new InvalidOperationException("A delay without end ended.");
    }

    private static bool HasReturnRoute(Message message) => message.AdditionalHeaders?.ContainsKey("return_route") ?? false;
}
