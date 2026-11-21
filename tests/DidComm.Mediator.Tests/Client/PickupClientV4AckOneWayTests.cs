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
/// <see cref="PickupClient.AckAsync"/> under Message Pickup 4.0 on every kind of route, against the
/// fake exchange (#100, PR #143 review round 2, OD-6). Each chunk of 100 ids is one
/// <c>messages-received</c> sent one way, without <c>return_route</c> (4.0 L35); nothing answers it
/// (L62, L294), so the call returns <c>null</c>. It sends nothing else: no <c>status-request</c>
/// before the acknowledgements (round 1 opened the connection with one on a WebSocket; that is
/// gone) and none after them. A message that could not be sent is an exception, never
/// <c>null</c>. Under 3.0 the same call sends only its acknowledgements too, and returns the status
/// that answers the last one. Over the real channel: <see cref="PickupClientV4AckOneWayChannelTests"/>.
/// </summary>
public sealed class PickupClientV4AckOneWayTests
{
    // A route that is neither HTTP nor a WebSocket: the channel treats it as a WebSocket.
    private static readonly MediatorRoute OtherRoute = new(new Uri("didcomm+custom://mediator.example/"), IsHttp: false, IsWebSocket: false);

    public static TheoryData<string> Routes => new() { "http", "ws", "other" };

    private static MediatorRoute RouteOf(string name) => name switch
    {
        "http" => HttpRoute,
        "ws" => WebSocketRoute,
        _ => OtherRoute,
    };

    // A 4.0 exchange whose mediator would answer a status-request (with 7 queued) if one were sent.
    private static FakePickupExchange V4On(MediatorRoute route)
        => new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.V4) { Route = route }
            .On(MessagePickup40.StatusRequestType, r => Status40(r, count: 7));

    // ---- what is sent, and what comes back ------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Under_4_0_AckAsync_sends_each_chunk_one_way_and_nothing_else_and_returns_null(string routeName)
    {
        var route = RouteOf(routeName);
        var exchange = V4On(route);
        var ids = Enumerable.Range(0, 150).Select(i => $"q{i}").ToArray();

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, ids));

        status.Should().BeNull("no status answers a 4.0 acknowledgement, and the call does not ask for one");
        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(
            (MessagePickup40.MessagesReceivedType, true),
            (MessagePickup40.MessagesReceivedType, true));
        exchange.Sent[0].Ids.Should().Equal(ids[..100]);
        exchange.Sent[1].Ids.Should().Equal(ids[100..]);
        exchange.Sent.Should().OnlyContain(s => !HasReturnRoute(s.Request), "4.0 L35: no return_route");
        exchange.Sent.Should().OnlyContain(s => s.Route == route && s.MediatorDid == M && s.LocalDid == L);
        exchange.SentOf(MessagePickup40.StatusRequestType).Should().BeEmpty();
    }

    [Fact]
    public async Task Under_4_0_over_a_WebSocket_a_version_remembered_from_another_route_sends_only_the_acknowledgement()
    {
        // Round 1 sent a status-request first in this case; under OD-6 nothing is sent first.
        var exchange = new FakePickupExchange(o => o.MessagePickupVersion = MessagePickupVersion.Negotiate) { Route = WebSocketRoute }
            .On(MessagePickup40.StatusRequestType, r => Status40(r, count: 7));
        exchange.PickupVersions.Set(M, MessagePickupVersion.V4);

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        status.Should().BeNull();
        exchange.Sent.Select(s => s.Type).Should().Equal(MessagePickup40.MessagesReceivedType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Under_3_0_AckAsync_sends_only_the_acknowledgement_and_returns_the_status_that_answers_it(bool webSocket)
    {
        var exchange = new FakePickupExchange { Route = webSocket ? WebSocketRoute : HttpRoute }
            .On(MessagePickup.MessagesReceivedType, r => Status(r, count: 5));

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" }));

        exchange.Sent.Select(s => (s.Type, s.OneWay)).Should().Equal(new[] { (MessagePickup.MessagesReceivedType, false) },
            "a 3.0 messages-received asks for a return route itself, and the mediator's status answers it");
        status.Should().NotBeNull();
        status!.MessageCount.Should().Be(5);
    }

    // ---- time -----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Under_4_0_AckAsync_waits_for_nothing_on_the_clock(string routeName)
    {
        var exchange = V4On(RouteOf(routeName));
        var clock = exchange.Clock;
        var started = clock.GetUtcNow();
        var mark = clock.Mark();

        var status = await PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 250).Select(i => $"q{i}").ToArray()));

        status.Should().BeNull();
        exchange.Sent.Should().HaveCount(3);
        clock.GetUtcNow().Should().Be(started, "the call ended when the last send did");
        clock.Mark().Should().Be(mark, "no wait was started");
    }

    // ---- failures -------------------------------------------------------------------------------

    public static TheoryData<string, string> Failures => new()
    {
        { "transport", "ws" },
        { "transport", "http" },
        { "timeout", "ws" },
        { "timeout", "http" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Under_4_0_a_message_that_could_not_be_sent_ends_the_call_with_its_exception_never_null(string failure, string routeName)
    {
        // The exchange throws what the channel throws: the transport's failure as it is, or a
        // TimeoutException when the send did not complete within RequestTimeout.
        Exception error = failure == "transport" ? new TransportException("the socket closed") : new TimeoutException("not sent within the RequestTimeout");
        var exchange = V4On(RouteOf(routeName));
        var sends = 0;
        exchange.OneWay = _ => Interlocked.Increment(ref sends) == 2 ? Task.FromException(error) : Task.CompletedTask;

        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 250).Select(i => $"q{i}").ToArray())));

        thrown.Should().BeSameAs(error);
        exchange.Sent.Select(s => s.Type).Should().Equal(new[] { MessagePickup40.MessagesReceivedType, MessagePickup40.MessagesReceivedType },
            "the call stops at the message that failed, and asks for nothing");
    }

    [Fact]
    public async Task Under_4_0_cancelling_during_a_send_ends_the_call_and_the_later_chunks_are_not_sent()
    {
        var exchange = V4On(WebSocketRoute);
        exchange.OneWay = async sent => await Task.Delay(Timeout.InfiniteTimeSpan, sent.Ct);
        using var cts = new CancellationTokenSource();

        var acking = PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 150).Select(i => $"q{i}").ToArray(), ct: cts.Token);
        await exchange.WaitForSentAsync(1);
        await cts.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => PickupTest.Within(acking));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        exchange.Sent.Should().ContainSingle().Which.Ct.Should().Be(cts.Token, "each message is sent under the caller's token");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static bool HasReturnRoute(Message message) => message.AdditionalHeaders?.ContainsKey("return_route") ?? false;
}
