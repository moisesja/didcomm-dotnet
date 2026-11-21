using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #100, PR #143 review P2-2: <see cref="PickupClient.AckAsync"/> under Message Pickup 4.0 over a
/// WebSocket returns a status taken after its acknowledgements were applied, even when no
/// connection for its answers is open when it is called. A 4.0 <c>messages-received</c> carries no
/// <c>return_route</c> (4.0 L35), and the transport sends such a message on the connection that
/// carries this agent's answers only while one is open (FR-RR-05, FR-RR-07); so the call first sends
/// a <c>status-request</c> of its own, which opens that connection (or reuses the open one), and the
/// acknowledgement and the <c>status-request</c> after it then follow on it. The mediator handles one
/// connection's messages in order, so the acknowledgement is applied before the status is taken.
/// Each case holds the acknowledgement at the mediator's store (<see cref="AckHoldingStore"/>): the
/// call must not return while it is held, though the client has already sent the
/// <c>status-request</c> after it. The cases: a client pinned to 4.0 with no connection yet; a
/// version negotiated over HTTP and remembered, then the call over a WebSocket with no connection
/// yet; and the version remembered after the mediator closed the connection the call would have
/// used. The real client against the real <c>AddMediator()</c> host over the TestServer.
/// </summary>
public sealed class PickupV4AckOrderEndToEndTests
{
    [Fact]
    public async Task Pinned_to_4_0_with_no_connection_yet_AckAsync_opens_one_and_its_status_waits_for_the_held_acknowledgement()
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
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "leased by a fetch over HTTP");
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        server.Count.Should().Be(0, "precondition: no WebSocket yet");

        var status = await AckWithTheAcknowledgementHeldAsync(mediator, harness, store, server, client, bob, delivered.Id, serverSocket: 0, clientSocket: 0);

        status.MessageCount.Should().Be(0);
        harness.Connects.Should().Be(1, "one connection: no pooled socket for the header-less acknowledgement");
        server.Count.Should().Be(1);
        await ShouldBeTheOrderedTailAsync(mediator, observer, bob, delivered.Id);
        observer.PickupKindsFrom(bob.Did).Should().Equal("4.0/delivery-request", "4.0/status-request", "4.0/messages-received", "4.0/status-request");
    }

    [Fact]
    public async Task Negotiated_over_HTTP_and_remembered_then_over_a_WebSocket_with_no_connection_yet_AckAsync_opens_one_and_its_status_waits_for_the_held_acknowledgement()
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

        var status = await AckWithTheAcknowledgementHeldAsync(mediator, harness, store, server, client, bob, delivered.Id, serverSocket: 0, clientSocket: 0);

        status.MessageCount.Should().Be(0);
        harness.Connects.Should().Be(1, "one connection: no pooled socket for the header-less acknowledgement");
        server.Count.Should().Be(1);
        await ShouldBeTheOrderedTailAsync(mediator, observer, bob, delivered.Id);
        observer.PickupKindsFrom(bob.Did).Should().Equal(
            "discover-features/queries", "4.0/status-request", "4.0/delivery-request", "4.0/status-request", "4.0/messages-received", "4.0/status-request");
    }

    [Fact]
    public async Task With_the_version_remembered_after_the_mediator_closed_the_connection_AckAsync_opens_a_new_one_and_its_status_waits_for_the_held_acknowledgement()
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

        var status = await AckWithTheAcknowledgementHeldAsync(mediator, harness, store, server, client, bob, delivered.Id, serverSocket: 2, clientSocket: 1);

        status.MessageCount.Should().Be(0);
        harness.Connects.Should().Be(2, "the first connection and the one AckAsync opened: no pooled socket");
        server.Count.Should().Be(3);
        client.SentOn(0).Should().Be(2, "the query and the status-request before the close");
        await ShouldBeTheOrderedTailAsync(mediator, observer, bob, delivered.Id);
        observer.PickupKindsFrom(bob.Did).Count(k => k == "discover-features/queries").Should().Be(1, "the remembered version was used after the close");
    }

    // AckAsync with the acknowledgement held at the store. Once the acknowledgement has reached the
    // store and the client has written the status-request after it on the same socket (its third
    // message there: the first status-request, the acknowledgement, that status-request), the call
    // must still be running: the mediator reads that status-request only after the acknowledgement's
    // dispatch ends. A status-request on another connection would be answered meanwhile, with the
    // message still counted, and the call would return.
    private static async Task<PickupStatus> AckWithTheAcknowledgementHeldAsync(
        MediatorTestServer mediator, MediatorClientHarness harness, AckHoldingStore store, CountingSockets server, ClientSockets client, TestPeer bob, string id, int serverSocket, int clientSocket)
    {
        var hold = store.HoldNextAcknowledgementOf(id);
        var acking = PickupV4AckOrder.Observed(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { id }, harness.WebSocketEndpoint));
        await hold.Arrived.WaitAsync(MediatorTestServer.Timeout);
        await MediatorTestServer.WaitUntilAsync(() => acking.IsCompleted || client.SentOn(clientSocket) == 3,
            "the client sent the status-request after the held acknowledgement on the same socket (or AckAsync returned)");

        acking.IsCompleted.Should().BeFalse("the status-request after the acknowledgement waits behind it on the one connection");
        var socket = await server.AcceptedAsync(serverSocket);
        socket.MessagesReceived.Should().Be(2, "the mediator read the first status-request and the acknowledgement, and not yet what follows it");
        Qa2.MessageCount(mediator).Should().Be(1, "the acknowledgement is held");
        hold.Release();
        var status = await PickupE2E.Within(acking);
        socket.MessagesReceived.Should().Be(3, "the status-request after the acknowledgement rode the same connection");
        return status;
    }

    // The call's last three messages, as the mediator handled them: the first status-request and the
    // one after the acknowledgement with return_route all, the acknowledgement between them one way
    // (no return_route, 4.0 L35) and stamped (FR-RR-09).
    private static async Task ShouldBeTheOrderedTailAsync(MediatorTestServer mediator, PickupWireObserver observer, TestPeer bob, string id)
    {
        await mediator.FlushObserversAsync();
        var tail = observer.From(bob.Did).Where(r => r.Kind.StartsWith("4.0/", StringComparison.Ordinal)).TakeLast(3).ToArray();
        tail.Select(r => r.Kind).Should().Equal("4.0/status-request", "4.0/messages-received", "4.0/status-request");
        tail[0].ReturnRoute.Should().Be(ReturnRouteMode.All);
        tail[1].ReturnRoute.Should().Be(ReturnRouteMode.None, "one way, without return_route (L35)");
        tail[1].CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "stamped (FR-RR-09)");
        tail[1].Body!["message_id_list"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal(id);
        tail[2].ReturnRoute.Should().Be(ReturnRouteMode.All);
        Qa2.MessageCount(mediator).Should().Be(0);
    }
}
