using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: a wallet that acknowledges under Message Pickup 4.0 and then shuts down at once does
/// not lose the acknowledgement. Under 4.0 nothing answers <c>messages-received</c>, so
/// <see cref="PickupClient.AckAsync"/> returns once it is sent; the transport's
/// <see cref="WebSocketDidCommTransport.DisposeAsync"/> then sends its close frame and waits (bounded)
/// for the mediator's. The mediator (<c>MapDidCommMediator</c>) handles one connection's messages in
/// order and reads the close frame only after the acknowledgement before it was handled, so its answer
/// to the close means the acknowledgement was applied.
/// <para>
/// Each case slows the acknowledgement at the mediator's store by
/// <see cref="AckDelayingStore.Delay"/> (500 ms, well below the 30 s default
/// <see cref="WebSocketTransportOptions.SendTimeout"/> that bounds the close), and checks the store
/// at the moment the close (or dispose) returned. A client that only sends its close frame and drops
/// the connection returns first: the acknowledgement is not applied yet, and on Kestrel the dropped
/// connection cancels its handling. The raw-socket cases isolate the mediator's ordering; the others
/// run the real <see cref="PickupClient"/> and transport, on the pooled socket (no session) and on a
/// return-route session socket (opened first by a status request), over the TestServer and over real
/// Kestrel with real <see cref="ClientWebSocket"/>s.
/// </para>
/// </summary>
public sealed class WebSocketDisposeAcknowledgementEndToEndTests
{
    /// <summary>Which socket the acknowledgement travels on.</summary>
    public enum AckSocket
    {
        /// <summary>The endpoint's pooled socket: no session was opened.</summary>
        Pooled,

        /// <summary>The return-route session socket a 4.0 status request opened first.</summary>
        Session,
    }

    public static TheoryData<AckSocket> Sockets => new() { AckSocket.Pooled, AckSocket.Session };

    // ---- the mediator's ordering, with raw sockets ------------------------------------------------

    [Fact]
    public async Task Over_the_TestServer_a_raw_socket_that_acknowledges_then_closes_gets_the_mediators_close_only_after_the_acknowledgement_was_applied()
    {
        AckDelayingStore store = null!;
        await using var mediator = await PickupV4E2E.StartMediatorAsync(new PickupWireObserver(), o => o.ConfigureServices = s => AckDelayingStore.Register(s, x => store = x));
        await using var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        await PickupE2E.SendAsync(mediator, await harness.NewAliceAsync(), recipient);
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        var ack = store.DelayNextAcknowledgementOf(delivered.Id);

        using var socket = await mediator.ConnectAsync();
        var received = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(bob.Did, mediator.Did, new[] { delivered.Id }));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, bob, received));
        using var guard = new CancellationTokenSource(MediatorTestServer.Timeout);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("the mediator answers the close only after it handled the acknowledgement sent before it");
        Qa2.MessageCount(mediator).Should().Be(0);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the mediator answered the close");
    }

    [Fact]
    public async Task On_Kestrel_a_raw_ClientWebSocket_that_acknowledges_then_closes_gets_the_mediators_close_only_after_the_acknowledgement_was_applied()
    {
        AckDelayingStore store = null!;
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Builder = b => AckDelayingStore.Register(b.Services, x => store = x));
        var (bob, id) = await QueueAndLeaseOneAsync(host);
        var ack = store.DelayNextAcknowledgementOf(id);

        using var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await SendAcknowledgementAsync(host, socket, bob, id);
        using var guard = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("the mediator answers the close only after it handled the acknowledgement sent before it");
        StoreCount(host).Should().Be(0);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the mediator answered the close");
        socket.State.Should().Be(WebSocketState.Closed);
    }

    // ---- the real client and transport ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Sockets))]
    public async Task Over_the_TestServer_disposing_the_container_right_after_AckAsync_returns_once_the_mediator_applied_the_acknowledgement(AckSocket on)
    {
        AckDelayingStore store = null!;
        var client = new ClientSockets();
        await using var mediator = await PickupV4E2E.StartMediatorAsync(new PickupWireObserver(), o => o.ConfigureServices = s => AckDelayingStore.Register(s, x => store = x));
        var harness = await PickupV4E2E.HarnessAsync(mediator, MessagePickupVersion.V4, webSocket: true, o => o.WebSocket = client.Install);
        await using var _ = harness; // also disposed by the test, at the point under test (idempotent)
        var transport = harness.Services.GetRequiredService<WebSocketDidCommTransport>();
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        await PickupE2E.SendAsync(mediator, await harness.NewAliceAsync(), recipient);
        var delivered = (await PickupV4Qa2.Fetch40Async(mediator, bob)).Should().ContainSingle().Subject;
        if (on == AckSocket.Session)
        {
            (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: harness.WebSocketEndpoint))).MessageCount.Should().Be(1);
            transport.ActiveSessionSocketCount.Should().Be(1, "precondition: a session socket is open");
        }
        var ack = store.DelayNextAcknowledgementOf(delivered.Id);

        (await PickupE2E.Within(harness.Pickup.AckAsync(mediator.Did, bob.Did, new[] { delivered.Id }, harness.WebSocketEndpoint))).Should().BeNull("under 4.0 nothing answers the acknowledgement");
        client.All.Should().ContainSingle("the acknowledgement went on the one socket the client has");
        transport.ActiveSessionSocketCount.Should().Be(on == AckSocket.Session ? 1 : 0, $"precondition: the acknowledgement rides the {on} socket");
        await harness.DisposeAsync().AsTask().WaitAsync(MediatorTestServer.Timeout);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("dispose waited for the mediator's close, which it sends only after it handled the acknowledgement");
        Qa2.MessageCount(mediator).Should().Be(0);
        client.All[0].CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the mediator answered the client's close");
    }

    [Theory]
    [MemberData(nameof(Sockets))]
    public async Task On_Kestrel_disposing_the_container_right_after_AckAsync_returns_once_the_mediator_applied_the_acknowledgement(AckSocket on)
    {
        AckDelayingStore store = null!;
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Builder = b => AckDelayingStore.Register(b.Services, x => store = x));
        var (bob, id) = await QueueAndLeaseOneAsync(host);
        var recipient = KestrelRecipient.Create(host, bob);
        await using var _ = recipient; // also disposed by the test, at the point under test (idempotent)
        var transport = recipient.Transport;
        var endpoint = host.UriOf(DeadlineEndpoint.Mediator);
        if (on == AckSocket.Session)
        {
            (await recipient.Pickup.GetStatusAsync(host.Did, bob.Did, serviceEndpointOverride: endpoint).WaitAsync(DeadlineKestrelHost.Guard)).MessageCount.Should().Be(1);
            transport.ActiveSessionSocketCount.Should().Be(1, "precondition: a session socket is open");
        }
        var ack = store.DelayNextAcknowledgementOf(id);

        (await recipient.Pickup.AckAsync(host.Did, bob.Did, new[] { id }, endpoint).WaitAsync(DeadlineKestrelHost.Guard)).Should().BeNull("under 4.0 nothing answers the acknowledgement");
        recipient.Sockets.Should().ContainSingle("the acknowledgement went on the one socket the client has");
        transport.ActiveSessionSocketCount.Should().Be(on == AckSocket.Session ? 1 : 0, $"precondition: the acknowledgement rides the {on} socket");
        await recipient.DisposeAsync().AsTask().WaitAsync(DeadlineKestrelHost.Guard);

        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("dispose waited for the mediator's close, which it sends only after it handled the acknowledgement");
        StoreCount(host).Should().Be(0);
        var socket = recipient.Sockets.Single();
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the mediator answered the client's close");
        host.Logs.Entries.Should().NotContain(e => e.Contains("ignoring close exception during dispose", StringComparison.Ordinal),
            "the close completed: it neither failed nor ran out of time");
    }

    // ---- steps ------------------------------------------------------------------------------------

    // Bob, enrolled, with one forward from a sender queued for him and leased by a 4.0 delivery-request
    // on a socket of its own (closed again): its id is what the acknowledgement names. Also used by
    // the OD-8 tests (WebSocketMessageFinishTests).
    internal static async Task<(TestPeer Bob, string Id)> QueueAndLeaseOneAsync(DeadlineKestrelHost host)
    {
        var bob = await host.EnrolAsync();
        (await host.PostForwardAsync(bob)).Should().Be(HttpStatusCode.Accepted);
        await host.App.Services.GetRequiredService<ForwardHandler>().FlushAsync(DeadlineKestrelHost.Guard);
        using var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        var answer = await host.AskAsync(socket, bob, MessagePickup40.CreateDeliveryRequest(bob.Did, host.Did, 10));
        MessagePickup40.TryParseDelivery(answer, out var messages, out _).Should().BeTrue();
        var id = messages!.Should().ContainSingle().Subject.Id;
        using var guard = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);
        return (bob, id);
    }

    // A 4.0 messages-received as the client sends it: stamped, without return_route (L35), authcrypted.
    internal static async Task SendAcknowledgementAsync(DeadlineKestrelHost host, ClientWebSocket socket, TestPeer bob, string id)
    {
        var packed = await PackAcknowledgementAsync(host, bob, id);
        using var guard = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await socket.SendAsync(packed, WebSocketMessageType.Text, endOfMessage: true, guard.Token);
    }

    // The same messages-received, packed, as UTF-8 bytes.
    internal static async Task<byte[]> PackAcknowledgementAsync(DeadlineKestrelHost host, TestPeer bob, string id)
    {
        var message = MessagePickup40.CreateMessagesReceived(bob.Did, host.Did, new[] { id });
        message.CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var packed = await bob.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { host.Did }, From: bob.Did, Forward: false));
        return Encoding.UTF8.GetBytes(packed.Message);
    }

    internal static int StoreCount(DeadlineKestrelHost host)
        => ((IQa2WrappingStore)host.App.Services.GetRequiredService<IMessageStore>()).Inner.MessageCount;
}
