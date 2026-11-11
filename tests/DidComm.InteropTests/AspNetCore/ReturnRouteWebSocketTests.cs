using System.Collections.Concurrent;
using System.Net.WebSockets;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #91 F11 — the WebSocket binding of the return-route extension (FR-RR-05) end to end against a
/// TestServer with <c>AllowSameSocketReplies = true</c>: an authcrypt message carrying
/// <c>return_route: all</c> opens a session on its socket, every push a handler makes through
/// <c>context.Session</c> arrives on that socket in order, and closing the socket unregisters it.
/// </summary>
public sealed class ReturnRouteWebSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_pushes_through_the_session_arrive_on_the_same_socket_in_order_and_close_unregisters()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        var opened = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionOpened += (_, e) => opened.Enqueue(e);
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);

        host.Recorder.Behavior = (message, context) => (
            new[]
            {
                Push(host, "first", message.Id),
                Push(host, "second", message.Id),
            },
            null);

        var inbound = host.Stamped(host.RecordingMessage(host.Alice, "push-two").WithReturnRoute(ReturnRouteMode.All)).Build();
        using var socket = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, inbound));

        var first = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        var second = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        first.Type.Should().Be(WebSocketMessageType.Binary, "each push is one binary WebSocket message (FR-TRN-09)");
        second.Type.Should().Be(WebSocketMessageType.Binary);

        var firstUnpacked = await host.Alice.Client.UnpackAsync(first.Text);
        var secondUnpacked = await host.Alice.Client.UnpackAsync(second.Text);
        firstUnpacked.Message.Type.Should().EndWith("/first", "pushes are written in the order they were made");
        secondUnpacked.Message.Type.Should().EndWith("/second");
        foreach (var pushed in new[] { firstUnpacked, secondUnpacked })
        {
            pushed.Encrypted.Should().BeTrue();
            pushed.Authenticated.Should().BeTrue();
            pushed.SenderKid.Should().StartWith(host.ServerDid + "#");
            pushed.Message.From.Should().Be(host.ServerDid);
            pushed.Message.To.Should().Equal(host.Alice.Did);
            pushed.Message.Thid.Should().Be(inbound.Id);
        }

        // The handler records after its pushes returned, which can be after the frames arrived here.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1, Timeout)).Should().BeTrue();
        var seen = host.Recorder.Seen.Single();
        seen.PushResults.Should().Equal(true, true);
        seen.Session.Should().NotBeNull();
        seen.Session!.PeerDid.Should().Be(host.Alice.Did, "the peer is the authcrypt sender's DID subject");
        seen.Session.LocalDid.Should().Be(host.ServerDid, "the local DID is the one whose key decrypted the inbound");
        seen.Session.Transport.Should().Be(ReturnRouteTransport.WebSocket);
        seen.Session.IsLive.Should().BeTrue();
        seen.ReturnRoute.Mode.Should().Be(ReturnRouteMode.All);
        host.Registry.Count.Should().Be(1);
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(seen.Session);
        opened.Should().ContainSingle().Which.Session.Should().BeSameAs(seen.Session);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => !closed.IsEmpty, Timeout)).Should().BeTrue("closing the socket raises SessionClosed (FR-RR-05)");
        var close = closed.Single();
        close.Session.Should().BeSameAs(seen.Session);
        close.Reason.Should().Be(SessionCloseReason.Unregistered);
        host.Registry.Count.Should().Be(0, "the session is unregistered when its socket closes");
        seen.Session.Closed.IsCancellationRequested.Should().BeTrue();
        seen.Session.Mode.Should().Be(ReturnRouteMode.None);
        host.LeakingLogLines().Should().BeEmpty("no log line may carry a DID or key id (JC-6)");
    }

    [Fact]
    public async Task A_second_socket_for_the_same_DID_at_the_per_peer_bound_evicts_the_first_with_a_normal_close()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
        });
        var closed = new ConcurrentQueue<ReturnRouteSessionEventArgs>();
        host.Registry.SessionClosed += (_, e) => closed.Enqueue(e);

        using var first = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(first, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();
        var firstSession = host.Registry.Find(host.Alice.Did).Single();

        using var second = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(second, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build()));

        // FR-RR-03: evicting a WebSocket session closes its socket so the peer observes it.
        var observed = await ReturnRouteTestHost.ReceiveAsync(first, Timeout);
        observed.Type.Should().Be(WebSocketMessageType.Close);
        first.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);

        (await ReturnRouteTestHost.WaitUntilAsync(() => !closed.IsEmpty, Timeout)).Should().BeTrue();
        closed.Should().ContainSingle().Which.Should().Match<ReturnRouteSessionEventArgs>(e => e.Reason == SessionCloseReason.Evicted && ReferenceEquals(e.Session, firstSession));
        var remaining = host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;
        remaining.Should().NotBeSameAs(firstSession);
        firstSession.Closed.IsCancellationRequested.Should().BeTrue();

        // The evicted connection's own end unregisters nothing twice.
        await Task.Delay(200);
        closed.Should().HaveCount(1, "the registry already forgot the evicted session; its connection's end raises no second event");
        host.Registry.Count.Should().Be(1);

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 0, Timeout)).Should().BeTrue();
        host.LeakingLogLines().Should().BeEmpty();
    }

    private static Message Push(ReturnRouteTestHost host, string name, string thid)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(host.ServerDid)
            .WithTo(host.Alice.Did)
            .WithThid(thid)
            .Build();
}
