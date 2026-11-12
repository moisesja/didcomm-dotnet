using System.Collections.Concurrent;
using DidComm.Messages;
using DidComm.Protocols.TrustPing;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #92 G6 — the return-route client over WebSocket, end to end: a client container built with
/// <c>UseWebSocketTransport(ReceiveInbound)</c> + <c>UseInboundDispatch()</c> + <c>AddBuiltInProtocols()</c>
/// talks to the #91 TestServer agent (<c>AllowSameSocketReplies = true</c>). A send with
/// <c>return_route</c> <c>all</c> opens the session socket S(E, L, P); the server's replies and pushes on
/// it are dispatched to the client's handlers with no session; header-less sends ride S, an explicit
/// <c>none</c> retires it, an eviction ends it, and STOMP never opens one. Every test ends with the log
/// scan (no DID or key id in any line, client or server).
/// </summary>
public sealed class ReturnRouteClientWebSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Default_client_ping_with_All_over_WebSocket_dispatches_the_ping_response_to_a_registered_handler()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var ping = TrustPing.CreatePing(host.Alice.Did, host.ServerDid);

        var result = await client.Client.SendAsync(ping, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        result.Transport.Accepted.Should().BeTrue();
        result.Returned.Should().BeNull("over WebSocket a reply arrives through the inbound consumer, never in SendResult");
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, Timeout)).Should().BeTrue("the server answered the ping on the session socket");
        var seen = client.Recorder.Seen.Single();
        seen.Message.Type.Should().Be(TrustPing.ResponseType);
        seen.Message.Thid.Should().Be(ping.Id);
        seen.Message.From.Should().Be(host.ServerDid);
        seen.Message.To.Should().Equal(host.Alice.Did);
        seen.Session.Should().BeNull("a pushed message is dispatched with no session");
        client.Connects.Should().Be(1);
        ping.CreatedTime.Should().BeNull("the caller's ping was not stamped; the sent copy was");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_push_reaches_a_registered_client_handler_with_Session_null()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (new[] { Push(host, host.Alice, "pushed", message.Id) }, null);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var request = host.RecordingMessage(host.Alice, "push-me").Build();

        await client.Client.SendAsync(request, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, Timeout)).Should().BeTrue();
        var seen = client.Recorder.Seen.Single();
        seen.Message.Type.Should().EndWith("/pushed");
        seen.Message.Thid.Should().Be(request.Id);
        seen.Session.Should().BeNull();
        host.Recorder.Seen.Single().PushResults.Should().Equal(true);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Two_local_DIDs_through_one_transport_use_two_sockets_and_each_gets_its_own_reply()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), Reply(host, message));
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, holders: new[] { host.Alice, host.Carol });
        var fromAlice = host.RecordingMessage(host.Alice, "a").Build();
        var fromCarol = host.RecordingMessage(host.Carol, "c").Build();

        await client.Client.SendAsync(fromAlice, client.ToServer(host.Alice) with { ReturnRoute = ReturnRouteMode.All });
        await client.Client.SendAsync(fromCarol, client.ToServer(host.Carol) with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 2, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(2, "each (endpoint, local DID, peer DID) has its own session socket");
        var replies = client.Recorder.Seen.ToDictionary(s => s.Message.Thid!);
        replies[fromAlice.Id].Message.To.Should().Equal(host.Alice.Did);
        replies[fromCarol.Id].Message.To.Should().Equal(host.Carol.Did);
        host.Registry.Count.Should().Be(2, "the server bound one session per socket");
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_ReturnRoute_two_local_DIDs_share_one_pooled_socket()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, holders: new[] { host.Alice, host.Carol });

        await client.Client.SendAsync(host.RecordingMessage(host.Alice).Build(), client.ToServer(host.Alice));
        await client.Client.SendAsync(host.RecordingMessage(host.Carol).Build(), client.ToServer(host.Carol));

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 2, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(1, "sends without a return route share the endpoint's pooled socket");
        host.Recorder.Seen.Should().OnlyContain(s => s.Session == null);
        client.Transport.ActiveSessionSocketCount.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task All_then_header_less_then_None_keeps_the_session_socket_then_retires_it()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (
            message.Type.EndsWith("/third", StringComparison.Ordinal) ? new[] { Push(host, host.Alice, "after-none", message.Id) } : Array.Empty<Message>(),
            Reply(host, message));
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var first = host.RecordingMessage(host.Alice, "first").Build();
        var second = host.RecordingMessage(host.Alice, "second").Build();
        var third = host.RecordingMessage(host.Alice, "third").Build();
        var fourth = host.RecordingMessage(host.Alice, "fourth").Build();

        await client.Client.SendAsync(first, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, Timeout)).Should().BeTrue();

        await client.Client.SendAsync(second, client.ToServer());
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 2, Timeout)).Should().BeTrue("the header-less send rode S, so its reply came back on S");
        client.Connects.Should().Be(1);

        var endedBefore = host.EndedWebSocketConnections;
        await client.Client.SendAsync(third, client.ToServer() with { ReturnRoute = ReturnRouteMode.None });
        (await client.WaitForDisconnectsAsync(1, Timeout)).Should().BeTrue("the none retired S and the server answered its close");
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.EndedWebSocketConnections == endedBefore + 1, Timeout)).Should().BeTrue();
        var thirdSeen = host.Recorder.Seen.Single(s => s.Message.Id == third.Id);
        thirdSeen.Session.Should().NotBeNull("the none travelled over S, so the server dispatched it on its session");
        thirdSeen.PushResults.Should().Equal(new[] { false }, "after processing the none the server pushes nothing (FR-RR-05)");
        client.Recorder.Count.Should().Be(2, "neither the none's reply nor the push was sent");
        client.Transport.LiveSessionSocketCount.Should().Be(0);

        await client.Client.SendAsync(fourth, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 3, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(2, "an all after the retire opens a fresh session socket");
        client.Recorder.Seen.Select(s => s.Message.Thid).Should().Equal(first.Id, second.Id, fourth.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Header_less_pickup_4_0_messages_received_after_live_pushes_travels_over_S_and_pushes_continue()
    {
        const string pickup = "https://didcomm.org/message-pickup/4.0";
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureDidComm = b => b.AddProtocol<Pickup40RecordingHandler>();
        });
        host.Recorder.Behavior = (message, _) => message.Type.EndsWith("/messages-received", StringComparison.Ordinal)
            ? (new[] { Delivery(host, "d3") }, null)
            : (new[] { Delivery(host, "d1"), Delivery(host, "d2") }, null);
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);

        var live = new MessageBuilder().WithType(pickup + "/live-delivery-change").WithFrom(host.Alice.Did).WithTo(host.ServerDid).Build();
        await client.Client.SendAsync(live, client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 2, Timeout)).Should().BeTrue("both live pushes were admitted (stamped by the server, JC-17) and dispatched");

        var ack = new MessageBuilder().WithType(pickup + "/messages-received").WithFrom(host.Alice.Did).WithTo(host.ServerDid).Build();
        await client.Client.SendAsync(ack, client.ToServer());
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 3, Timeout)).Should().BeTrue("the session still pushes after the header-less acknowledgement");

        client.Connects.Should().Be(1, "the header-less acknowledgement travelled over S");
        host.Recorder.Seen.Single(s => s.Message.Id == ack.Id).Session.Should().NotBeNull("the server received the acknowledgement on the session's socket");
        host.Recorder.Seen.Single(s => s.Message.Id == ack.Id).Message.CreatedTime.Should().Be(host.Clock.Now.ToUnixTimeSeconds(), "SendAsync stamped the header-less mediator-service message (FR-RR-09)");
        client.Recorder.Seen.Select(s => s.Message.Type).Should().OnlyContain(t => t == pickup + "/delivery");
        client.Recorder.Seen.Should().OnlyContain(s => s.Message.CreatedTime == host.Clock.Now.ToUnixTimeSeconds() && s.Session == null);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Builder_set_all_uses_the_session_socket()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), Reply(host, message));
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);
        var request = host.RecordingMessage(host.Alice, "builder").WithReturnRoute(ReturnRouteMode.All).Build();

        await client.Client.SendAsync(request, client.ToServer());

        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 1, Timeout)).Should().BeTrue("FR-RR-09: SendAsync stamped the builder-set all, the server admitted it and replied on S");
        client.Recorder.Seen.Single().Message.Thid.Should().Be(request.Id);
        request.CreatedTime.Should().BeNull("the caller's message is not mutated");

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "next").Build(), client.ToServer());
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Recorder.Count == 2, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Receiver_eviction_ends_the_loop_without_unobserved_exceptions_and_the_next_header_less_send_uses_the_pool()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                if (inner.ToString().Contains("DidComm.", StringComparison.Ordinal))
                    unobserved.Enqueue(inner);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await EvictAndSendAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().BeEmpty("no task the transport or the consumer started ends with an unobserved exception");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task EvictAndSendAsync()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.ConfigureSessions = s => s.MaxSessionsPerPeer = 1;
        });
        await using var client = ReturnRouteWebSocketClientHarness.Create(host);

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "opens").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Registry.Count == 1, Timeout)).Should().BeTrue();

        // Another socket opens a session for the same peer: at the per-peer bound the client's S is evicted.
        using var raw = await host.ConnectAsync();
        await ReturnRouteTestHost.SendAsync(raw, await host.AuthcryptAsync(host.Alice,
            host.Stamped(host.RecordingMessage(host.Alice, "evicts").WithReturnRoute(ReturnRouteMode.All)).Build()));

        (await client.WaitForDisconnectsAsync(1, Timeout)).Should().BeTrue("the eviction's close frame ended S's loop, which answered it");
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Transport.LiveSessionSocketCount == 0, Timeout)).Should().BeTrue();
        client.Transport.ActiveSessionSocketCount.Should().Be(0);

        var next = host.RecordingMessage(host.Alice, "next").Build();
        await client.Client.SendAsync(next, client.ToServer());
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Seen.Any(s => s.Message.Id == next.Id), Timeout)).Should().BeTrue();
        client.Connects.Should().Be(2, "with S gone, the header-less send used a pooled socket");
        host.Recorder.Seen.Single(s => s.Message.Id == next.Id).Session.Should().BeNull();
        client.Events.Where(e => e.Kind == DidComm.Transports.WebSocket.WebSocketLifecycleEventKind.Disconnected)
            .Should().ContainSingle().Which.Endpoint.Should().Be(client.Endpoint);

        await raw.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", default);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task Under_STOMP_no_session_socket_is_opened()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.UseStomp = true;
        });
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, o => o.UseStomp = true);

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "one").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "two").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });

        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 2, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(1, "both sends used the pooled STOMP socket");
        client.Transport.ActiveSessionSocketCount.Should().Be(0);
        host.Recorder.Seen.Should().OnlyContain(s => s.Session == null);
        host.Logs.Entries.Where(l => l.Contains("not available under STOMP (FR-RR-05); return-route sends use the pooled socket", StringComparison.Ordinal))
            .Should().ContainSingle("the notice is logged once per transport");
        client.Recorder.Count.Should().Be(0);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiveInbound_off_drains_session_sockets_and_logs_once()
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o => o.AllowSameSocketReplies = true);
        host.Recorder.Behavior = (message, _) => (new[] { Push(host, host.Alice, "pushed", message.Id) }, Reply(host, message));
        // Default SendTimeout (30 s): the retire backstop is 30 s away, so an end within the 10 s wait
        // below proves the loop completed the close handshake instead of waiting for the backstop.
        await using var client = ReturnRouteWebSocketClientHarness.Create(host, o => o.ReceiveInbound = false);

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "first").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Transport.InboundMessagesDropped == 2, Timeout)).Should().BeTrue("the push and the reply were read and dropped");
        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "second").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.All });
        (await ReturnRouteTestHost.WaitUntilAsync(() => client.Transport.InboundMessagesDropped == 4, Timeout)).Should().BeTrue();
        client.Connects.Should().Be(1);
        client.Recorder.Count.Should().Be(0, "nothing is dispatched with ReceiveInbound off");

        await client.Client.SendAsync(host.RecordingMessage(host.Alice, "third").Build(), client.ToServer() with { ReturnRoute = ReturnRouteMode.None });
        (await client.WaitForDisconnectsAsync(1, Timeout)).Should().BeTrue("the retired socket kept reading, saw the server's close and ended without the backstop");

        host.Logs.Entries.Where(l => l.Contains("ReceiveInbound is off", StringComparison.Ordinal)).Should().HaveCount(2,
            "one line at construction (a consumer is registered but unused) and one at the first session socket");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- helpers ----

    private static Message Push(ReturnRouteTestHost host, TestPeer to, string name, string thid)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/" + name)
            .WithFrom(host.ServerDid)
            .WithTo(to.Did)
            .WithThid(thid)
            .Build();

    private static Message Reply(ReturnRouteTestHost host, Message to)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/reply")
            .WithFrom(host.ServerDid)
            .WithTo(to.From!)
            .WithThid(to.Id)
            .Build();

    private static Message Delivery(ReturnRouteTestHost host, string name)
        => new MessageBuilder()
            .WithType("https://didcomm.org/message-pickup/4.0/delivery")
            .WithFrom(host.ServerDid)
            .WithTo(host.Alice.Did)
            .WithThid(name)
            .Build();
}
