using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #100, PR #143 review P2-2 and round 2 (owner decision OD-6): <see cref="PickupClient.AckAsync"/>
/// never returns a status it did not get as the mediator's answer to its own acknowledgement.
/// <para>
/// Why: a status asked for separately is not ordered after an acknowledgement nothing answered. The
/// round-2 repro showed it on the code before OD-6: AckAsync under Message Pickup 4.0 sent a
/// <c>status-request</c> of its own first, the connection was lost right after its answer arrived,
/// the acknowledgement and the <c>status-request</c> after it went out on other connections, and the
/// mediator answered that <c>status-request</c> while the acknowledgement was still on its way, so
/// AckAsync returned a status that still counted the acknowledged message.
/// </para>
/// <para>
/// Under 4.0 nothing answers <c>messages-received</c> (4.0 L62, L294): AckAsync only sends it, one way
/// and without <c>return_route</c> (L35), and returns <c>null</c>. Under 3.0 it returns the status
/// whose <c>thid</c> names its own <c>messages-received</c>, or <c>null</c> when none came within
/// half of <see cref="MediatorClientOptions.RequestTimeout"/> after the send. In neither version does
/// it send a <c>status-request</c>. Each case holds the acknowledgement at the mediator's store
/// (<see cref="AckHoldingStore"/>) so that a status taken meanwhile would show the message as still
/// queued. The real client against the real <c>AddMediator()</c> host over the TestServer.
/// </para>
/// </summary>
public sealed class PickupV4AckOrderEndToEndTests
{
    /// <summary>The state of the client's connection to the mediator when AckAsync is called.</summary>
    public enum Connection
    {
        /// <summary>No connection yet.</summary>
        NoneYet,

        /// <summary>
        /// No connection yet, and the first socket the call opens is set to drop right after it
        /// receives its first message: the point where the round-2 repro lost its connection (right
        /// after the answer to the status-request the call used to send first).
        /// </summary>
        DropArmedAtTheCallsFirstAnswer,

        /// <summary>A connection for answers was open, and was lost right after the last answer it carried (the client noticed).</summary>
        LostRightAfterItsLastAnswer,

        /// <summary>A connection for answers is open and stays open.</summary>
        OpenAndKept,
    }

    public static TheoryData<Connection> Connections => new()
    {
        Connection.NoneYet,
        Connection.DropArmedAtTheCallsFirstAnswer,
        Connection.LostRightAfterItsLastAnswer,
        Connection.OpenAndKept,
    };

    // ---- Message Pickup 4.0 -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Connections))]
    public async Task Under_4_0_AckAsync_never_returns_a_status_it_did_not_get_as_the_answer_and_sends_no_status_request_whatever_its_connection(Connection connection)
    {
        AckHoldingStore store = null!;
        var server = new CountingSockets();
        var client = new ClientSockets();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o =>
        {
            o.ConfigureServices = s => AckHoldingStore.Register(s, x => store = x);
            o.ConfigurePipeline = server.Install;
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o => o.WebSocket = client.Install);
        var transport = harness.Services.GetRequiredService<WebSocketDidCommTransport>();
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        server.Count.Should().Be(0, "precondition: no WebSocket yet");

        var ackSocket = 0;
        switch (connection)
        {
            case Connection.DropArmedAtTheCallsFirstAnswer:
                client.AbortAfterReceiving(index: 0, messages: 1);
                break;
            case Connection.LostRightAfterItsLastAnswer:
                client.AbortAfterReceiving(index: 0, messages: 1);
                (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint))).MessageCount.Should().Be(1);
                await MediatorTestServer.WaitUntilAsync(() => transport.ActiveSessionSocketCount == 0 && transport.LiveSessionSocketCount == 0, "the client noticed its connection was lost");
                client.All[0].MessagesReceived.Should().Be(1, "precondition: the connection dropped right after the status, its only answer");
                ackSocket = 1;
                break;
            case Connection.OpenAndKept:
                (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint))).MessageCount.Should().Be(1);
                transport.ActiveSessionSocketCount.Should().Be(1, "precondition: the client has a connection for its answers");
                break;
        }

        await AckWithTheAcknowledgementHeldShouldReturnNullAsync(mediator, harness, store, client, observer, bob, delivered.Id, ackSocket);

        switch (connection)
        {
            case Connection.NoneYet:
            case Connection.DropArmedAtTheCallsFirstAnswer:
                harness.Connects.Should().Be(1, "one connection, for the acknowledgement");
                server.Count.Should().Be(1);
                client.All[0].MessagesReceived.Should().Be(0, "the call received nothing on its connection, so a drop armed at its first answer never fired");
                break;
            case Connection.LostRightAfterItsLastAnswer:
                harness.Connects.Should().Be(2, "the lost connection, and the one the acknowledgement went on");
                server.Count.Should().Be(2);
                client.SentOn(0).Should().Be(1, "the earlier status-request, and nothing after the drop");
                break;
            case Connection.OpenAndKept:
                harness.Connects.Should().Be(1, "the acknowledgement rode the open connection");
                server.Count.Should().Be(1);
                (await server.AcceptedAsync(0)).MessagesReceived.Should().Be(2, "the earlier status-request and the acknowledgement");
                break;
        }
    }

    [Fact]
    public async Task Negotiated_over_HTTP_and_remembered_then_over_a_WebSocket_AckAsync_sends_only_the_acknowledgement_and_returns_null()
    {
        AckHoldingStore store = null!;
        var server = new CountingSockets();
        var client = new ClientSockets();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o =>
        {
            o.ConfigureServices = s => AckHoldingStore.Register(s, x => store = x);
            o.ConfigurePipeline = server.Install;
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate, webSocket: true, o => o.WebSocket = client.Install);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did))).MessageCount.Should().Be(1, "negotiated and asked over HTTP, the mediator's first endpoint");
        harness.Channel.PickupVersions.TryGet(mediator.Did, out var remembered).Should().BeTrue();
        remembered.Should().Be(MessagePickupVersion.V4, "precondition: the version is remembered, so the WebSocket call sends no query");
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        server.Count.Should().Be(0, "precondition: no WebSocket yet");

        await AckWithTheAcknowledgementHeldShouldReturnNullAsync(mediator, harness, store, client, observer, bob, delivered.Id, ackSocket: 0);

        harness.Connects.Should().Be(1, "one connection, for the acknowledgement");
        server.Count.Should().Be(1);
        observer.PickupKindsFrom(bob.Did).Should().Equal("discover-features/queries", "4.0/status-request", "4.0/delivery-request", "4.0/messages-received");
    }

    [Fact]
    public async Task With_the_version_remembered_after_the_mediator_closed_the_connection_AckAsync_sends_only_the_acknowledgement_and_returns_null()
    {
        AckHoldingStore store = null!;
        var server = new CountingSockets();
        var client = new ClientSockets();
        var observer = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o =>
        {
            o.ConfigureServices = s =>
            {
                AckHoldingStore.Register(s, x => store = x);
                // One session per peer: another connection of Bob's makes the mediator close the client's.
                s.AddOptions<ReturnRouteSessionOptions>().Configure(x => x.MaxSessionsPerPeer = 1);
            };
            o.ConfigurePipeline = server.Install;
        });
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.Negotiate, webSocket: true, o => o.WebSocket = client.Install);
        var transport = harness.Services.GetRequiredService<WebSocketDidCommTransport>();
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint))).MessageCount.Should().Be(1);
        harness.Channel.PickupVersions.TryGet(mediator.Did, out var remembered).Should().BeTrue();
        remembered.Should().Be(MessagePickupVersion.V4, "precondition: negotiated on the connection, and remembered");
        transport.ActiveSessionSocketCount.Should().Be(1, "precondition: the client has a connection for its answers");

        // Another socket of Bob's takes the only session: the mediator closes the client's connection.
        using var raw = await mediator.ConnectAsync();
        var takeover = MessagePickup40.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(raw, bob, takeover)).Status40(takeover.Id);
        await MediatorTestServer.WaitUntilAsync(() => transport.ActiveSessionSocketCount == 0 && transport.LiveSessionSocketCount == 0, "the client noticed the mediator closed its connection");
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        server.Count.Should().Be(2, "precondition: the client's first connection and the takeover");

        await AckWithTheAcknowledgementHeldShouldReturnNullAsync(mediator, harness, store, client, observer, bob, delivered.Id, ackSocket: 1);

        harness.Connects.Should().Be(2, "the first connection and the one the acknowledgement went on");
        server.Count.Should().Be(3);
        client.SentOn(0).Should().Be(2, "the query and the status-request before the close");
        observer.PickupKindsFrom(bob.Did).Count(k => k == "discover-features/queries").Should().Be(1, "the remembered version was used after the close");
    }

    // AckAsync under 4.0 over the WebSocket with the acknowledgement held at the store. The call
    // returns once its one message is sent: with null, having written that message on the client's
    // socket `ackSocket` and nothing else on any socket (a status-request before or after it would be
    // one more), and with the message still queued because the acknowledgement is still held. After
    // the release the mediator applies it, and Bob sent the mediator nothing else during the call.
    private static async Task AckWithTheAcknowledgementHeldShouldReturnNullAsync(
        MediatorTestServer mediator, MediatorClientHarness harness, AckHoldingStore store, ClientSockets client, PickupWireObserver observer, TestPeer bob, string id, int ackSocket)
    {
        await mediator.FlushObserversAsync();
        var before = observer.PickupKindsFrom(bob.Did).Count;
        var sent = client.SentOnAll;
        var sentOnAckSocket = client.SentOn(ackSocket);

        var hold = store.HoldNextAcknowledgementOf(id);
        var acking = PickupV4AckOrder.Observed(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { id }, harness.WebSocketEndpoint));
        await MediatorTestServer.WaitUntilAsync(() => acking.IsCompleted || client.SentOnAll > sent + 1,
            "AckAsync returned (or the client wrote more than the acknowledgement)");

        acking.IsCompletedSuccessfully.Should().BeTrue("under 4.0 AckAsync waits for nothing the mediator does: it returns once the acknowledgement is sent");
        (await acking).Should().BeNull("under 4.0 nothing answers an acknowledgement, and AckAsync asks for no status in its place (OD-6)");
        client.SentOnAll.Should().Be(sent + 1, "the client wrote the acknowledgement and nothing else: no status-request before or after it");
        client.SentOn(ackSocket).Should().Be(sentOnAckSocket + 1, $"the acknowledgement went on the client's socket {ackSocket}");
        await hold.Arrived.WaitAsync(MediatorTestServer.Timeout);
        Qa2.MessageCount(mediator).Should().Be(1, "the acknowledgement is held at the store: AckAsync returned before it was applied");
        hold.Release();

        await Qa2.StoreHoldsAsync(mediator, 0, "the mediator applied the acknowledgement once it was released");
        await observer.WaitForAsync(bob.Did, r => r.Kind == "4.0/messages-received" && IdsOf(r).Contains(id), "the mediator handled the acknowledgement");
        await mediator.FlushObserversAsync();
        var during = observer.From(bob.Did).Where(r => PickupKind(r.Kind)).Skip(before).ToArray();
        during.Select(r => r.Kind).Should().Equal(new[] { "4.0/messages-received" }, "the mediator received the acknowledgement from Bob, and no status-request");
        during[0].ReturnRoute.Should().Be(ReturnRouteMode.None, "one way, without return_route (L35)");
        during[0].CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "stamped (FR-RR-09)");
        IdsOf(during[0]).Should().Equal(id);
    }

    // ---- Message Pickup 3.0 -----------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Under_3_0_AckAsync_returns_the_status_whose_thid_is_its_own_messages_received_and_sends_no_status_request(bool connectionLostRightAfterTheAnswer)
    {
        var time = new QaManualTimeProvider();
        AckHoldingStore store = null!;
        var client = new ClientSockets();
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigureServices = s => AckHoldingStore.Register(s, x => store = x));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3, webSocket: true, o =>
        {
            o.Time = time;
            o.WebSocket = client.Install;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var answerWait = harness.Channel.Options.RequestTimeout / 2;
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        var delivered = await LeaseOver30Async(mediator, bob);
        if (connectionLostRightAfterTheAnswer)
            client.AbortAfterReceiving(index: 0, messages: 1); // the acknowledgement's own connection, right after its answer
        await mediator.FlushObserversAsync();
        var before = observer.PickupKindsFrom(bob.Did).Count;

        var hold = store.HoldNextAcknowledgementOf(delivered.Id);
        var mark = time.Mark();
        var acking = PickupV4AckOrder.Observed(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { delivered.Id }, harness.WebSocketEndpoint));
        await hold.Arrived.WaitAsync(MediatorTestServer.Timeout);
        // The acknowledgement is sent and its answer is being waited for (the wait starts once the
        // send completed); the held acknowledgement has not produced an answer yet.
        await time.WaitForTimerAsync(mark, answerWait, MediatorTestServer.Timeout);
        acking.IsCompleted.Should().BeFalse("the call waits for the answer to its own messages-received");
        hold.Release();
        var status = PickupE2E.Answered(await PickupE2E.Within(acking));

        status.MessageCount.Should().Be(0, "the answer is taken after the acknowledgement was applied");
        var ack = await AcknowledgementSeenAsync(mediator, observer, bob, delivered.Id, before);
        // What came back: one status, on the acknowledgement's thread, counting what the call returned.
        var answers = await StatusesFromAsync(harness, clientSeen, mediator, minimum: 1);
        var answer = answers.Should().ContainSingle("nothing else asked for a status").Subject;
        answer.Thid.Should().Be(ack.Id, "the status answers AckAsync's own messages-received");
        answer.Body!["message_count"]!.GetValue<int>().Should().Be(status.MessageCount, "what AckAsync returned is that answer");
        client.SentOnAll.Should().Be(1, "the client wrote the acknowledgement and nothing else");
        if (connectionLostRightAfterTheAnswer)
            client.All[0].MessagesReceived.Should().Be(1, "the connection dropped right after the answer, which the call still took");
        Qa2.MessageCount(mediator).Should().Be(0);
        harness.Channel.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Under_3_0_AckAsync_returns_null_when_its_messages_received_gets_no_answer_in_time_and_asks_for_no_status_in_its_place()
    {
        var time = new QaManualTimeProvider();
        AckHoldingStore store = null!;
        var client = new ClientSockets();
        var observer = new PickupWireObserver();
        var clientSeen = new PickupWireObserver();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(observer, o => o.ConfigureServices = s => AckHoldingStore.Register(s, x => store = x));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V3, webSocket: true, o =>
        {
            o.Time = time;
            o.WebSocket = client.Install;
            o.ConfigureServices = s => s.AddSingleton<IProtocolObserver>(clientSeen);
        });
        var answerWait = harness.Channel.Options.RequestTimeout / 2;
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        var delivered = await LeaseOver30Async(mediator, bob);
        await mediator.FlushObserversAsync();
        var before = observer.PickupKindsFrom(bob.Did).Count;

        var hold = store.HoldNextAcknowledgementOf(delivered.Id);
        try
        {
            var mark = time.Mark();
            var acking = PickupV4AckOrder.Observed(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { delivered.Id }, harness.WebSocketEndpoint));
            await hold.Arrived.WaitAsync(MediatorTestServer.Timeout);
            // The acknowledgement is held, so its answer cannot come. The wait for it is running;
            // moving the clock to its end gives the call its outcome: no answer in time.
            await time.WaitForTimerAsync(mark, answerWait, MediatorTestServer.Timeout);
            var sent = client.SentOnAll;
            sent.Should().Be(1, "precondition: the client wrote the acknowledgement");
            var fired = time.TimersFired;
            time.Advance(answerWait);
            (time.TimersFired - fired).Should().Be(1, "the move ends the wait for the answer, and nothing else");
            await MediatorTestServer.WaitUntilAsync(() => acking.IsCompleted || client.SentOnAll > sent,
                "AckAsync returned (or the client wrote something after the missing answer)");

            acking.IsCompletedSuccessfully.Should().BeTrue("no answer in time ends the call; it is not an error");
            (await acking).Should().BeNull("no status answered the messages-received in time, and AckAsync asks for none in its place");
            client.SentOnAll.Should().Be(sent, "nothing was sent after the missing answer: no status-request");
            Qa2.MessageCount(mediator).Should().Be(1, "the acknowledgement is still held");
        }
        finally
        {
            hold.Release();
        }

        await Qa2.StoreHoldsAsync(mediator, 0, "the mediator applied the acknowledgement once it was released");
        var ack = await AcknowledgementSeenAsync(mediator, observer, bob, delivered.Id, before);
        // The mediator answered after all, too late: the status reached the client and nothing took it.
        await clientSeen.WaitForAsync(mediator.Did, r => r.Kind == "3.0/status" && r.Thid == ack.Id, "the late status reached the client");
        (await StatusesFromAsync(harness, clientSeen, mediator, minimum: 1)).Should().ContainSingle()
            .Which.Thid.Should().Be(ack.Id, "the only status that came answers the acknowledgement");
        harness.Channel.PendingCount.Should().Be(0);
        client.SentOnAll.Should().Be(1);
    }

    // A 3.0 delivery-request over HTTP, sent by hand: the one message it leases (nothing acknowledges it).
    private static async Task<DeliveredMessage> LeaseOver30Async(MediatorTestServer mediator, TestPeer bob)
    {
        var request = MessagePickup.CreateDeliveryRequest(bob.Did, mediator.Did, 10);
        return (await mediator.AskAsync(bob, request)).Delivery(request.Id).Should().ContainSingle().Subject;
    }

    // The 3.0 messages-received AckAsync sent, once the mediator handled it: the one message Bob sent
    // after the first `before`, with return_route all (3.0 asks for the answer) and the id.
    private static async Task<WireRecord> AcknowledgementSeenAsync(MediatorTestServer mediator, PickupWireObserver observer, TestPeer bob, string id, int before)
    {
        await observer.WaitForAsync(bob.Did, r => r.Kind == "3.0/messages-received" && IdsOf(r).Contains(id), "the mediator handled the acknowledgement");
        await mediator.FlushObserversAsync();
        var during = observer.From(bob.Did).Where(r => PickupKind(r.Kind)).Skip(before).ToArray();
        during.Select(r => r.Kind).Should().Equal(new[] { "3.0/messages-received" }, "the mediator received the acknowledgement from Bob, and no status-request");
        during[0].ReturnRoute.Should().Be(ReturnRouteMode.All);
        IdsOf(during[0]).Should().Equal(id);
        return during[0];
    }

    // The 3.0 statuses the client received from the mediator, read once at least `minimum` are there
    // and the client's observer queue is drained.
    private static async Task<IReadOnlyList<WireRecord>> StatusesFromAsync(MediatorClientHarness harness, PickupWireObserver clientSeen, MediatorTestServer mediator, int minimum)
    {
        await MediatorTestServer.WaitUntilAsync(() => clientSeen.From(mediator.Did).Count(r => r.Kind == "3.0/status") >= minimum, "the status reached the client");
        await harness.Dispatcher.FlushObserversAsync(MediatorTestServer.Timeout);
        return clientSeen.From(mediator.Did).Where(r => r.Kind == "3.0/status").ToArray();
    }

    private static bool PickupKind(string kind)
        => kind.StartsWith("3.0/", StringComparison.Ordinal) || kind.StartsWith("4.0/", StringComparison.Ordinal) || kind.StartsWith("discover-features/", StringComparison.Ordinal);

    private static IReadOnlyList<string> IdsOf(WireRecord record)
        => record.Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
}
