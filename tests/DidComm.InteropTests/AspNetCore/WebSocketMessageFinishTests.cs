using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Transports;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.Stomp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using E2E = DidComm.InteropTests.Transports.WebSocketDisposeAcknowledgementEndToEndTests;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #100 OD-8, on real Kestrel (a TestServer hides a client's abort): the registry-aware WebSocket
/// loop (<c>MapDidCommWebSocket(pattern)</c>, and through it <c>MapDidCommMediator</c>'s WebSocket
/// endpoint) handles a message it has received in full under a token of its own. When the connection
/// ends with the message in hand the message is still handled, for at most the close grace
/// (<see cref="WebSocketReturnRouteSession.DefaultCloseGrace"/>, on the container's clock, which here is
/// the manual <see cref="QaManualTimeProvider"/>) and only until the host starts to stop; nothing more
/// is read from that connection.
/// <para>
/// Most cases hold the mediator's own Message Pickup 4.0 <c>messages-received</c> at its store
/// (<see cref="AckDelayingStore.HoldNextAcknowledgementOf"/>, a hold that honours the handler's token):
/// "applied" is the store removing the message, "cancelled" is the handler's token firing first. Each
/// waits for the bound's timer to be created (L-065 rule 3), which is the server having seen the end
/// with the message in hand, before it moves anything.
/// </para>
/// </summary>
public sealed class WebSocketMessageFinishTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;
    private static readonly TimeSpan Grace = WebSocketReturnRouteSession.DefaultCloseGrace;

    // ---- (a) a message received in full is applied after an abrupt drop ------------------------------

    [Fact]
    public async Task A_messages_received_whose_sender_aborts_the_connection_while_it_is_being_applied_is_still_applied()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id);

        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await E2E.SendAcknowledgementAsync(host, socket, bob, id);
        await ack.Started.Task.WaitAsync(Guard);
        var mark = host.Time.Mark();
        socket.Abort(); // no close frame
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);
        ack.Release();

        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("the message was received in full before the drop, so it is still applied");
        ack.Cancelled.Task.IsCompleted.Should().BeFalse();
        E2E.StoreCount(host).Should().Be(0);
        await host.WaitEndedWebSocketsAsync(2);
        Loud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_messages_received_whose_last_frame_was_read_just_before_the_connection_ended_is_still_applied()
    {
        // The end is seen before the handling starts: the loop is held right after the last frame,
        // inside the receive deadline's stop, until the server's abort token fired.
        GatedDisposeClock gate = null!;
        var (host, store, _) = await StartAsync(o => o.Builder = b => gate = GatedDisposeClock.Install(b));
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id);

        gate.Arm(DeadlineKestrelHost.Limit);
        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await E2E.SendAcknowledgementAsync(host, socket, bob, id);
        await gate.Held.WaitAsync(Guard);
        await AbortAndWaitUntilTheServerSawItAsync(host, socket);
        var mark = host.Time.Mark();
        gate.Release();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard); // the handling started with the connection already ended

        await ack.Started.Task.WaitAsync(Guard);
        ack.Release();
        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("the message was complete before the connection ended");
        E2E.StoreCount(host).Should().Be(0);
        await host.WaitEndedWebSocketsAsync(2);
        Loud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_that_ended_opens_no_session_so_its_message_in_hand_cannot_evict_the_peers_live_session()
    {
        GatedDisposeClock gate = null!;
        var (host, _, holding) = await StartAsync(o => o.Builder = b =>
        {
            gate = GatedDisposeClock.Install(b);
            b.Services.AddOptions<ReturnRouteSessionOptions>().Configure(s => s.MaxSessionsPerPeer = 1);
        });
        await using var _host = host;

        // Alice's live session, on a connection of its own.
        using var live = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        var opener = host.Probe(returnRoute: true);
        await DeadlinePeer.SendAsync(live, await host.PackAsync(opener), endOfMessage: true);
        await host.WaitDispatchedAsync(DeadlineEndpoint.Mediator, opener.Id);
        var liveSession = (WebSocketReturnRouteSession)host.Registry.Find(host.Alice.Did).Should().ContainSingle().Subject;

        // Another connection of Alice's ends with an eligible message (return_route: all) in hand.
        gate.Arm(DeadlineKestrelHost.Limit);
        var ended = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await DeadlinePeer.SendAsync(ended, await host.PackAsync(Hold(host, returnRoute: true)), endOfMessage: true);
        await gate.Held.WaitAsync(Guard);
        await AbortAndWaitUntilTheServerSawItAsync(host, ended);
        var mark = host.Time.Mark();
        gate.Release();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);

        var context = await holding.Entered.Task.WaitAsync(Guard);
        context.Session.Should().BeNull("a connection that already ended opens no session");
        holding.Release();
        (await holding.Handled.Task.WaitAsync(Guard)).Should().BeNull("the message in hand is still handled (it had no session to push on)");
        await host.WaitEndedWebSocketsAsync(1);

        liveSession.IsClosed.Should().BeFalse("the ended connection never registered a session, so the per-peer bound evicted nothing");
        host.Registry.Find(host.Alice.Did).Should().ContainSingle().Which.Should().BeSameAs(liveSession);
        live.State.Should().Be(WebSocketState.Open);
        Loud(host).Should().BeEmpty();
    }

    // ---- (b) a message not received in full is never applied ----------------------------------------

    [Fact]
    public async Task A_messages_received_of_which_only_the_first_fragment_arrived_before_the_abort_is_never_applied()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id);
        var packed = await E2E.PackAcknowledgementAsync(host, bob, id);

        var mark = host.Time.Mark();
        var peer = await host.ConnectRawAsync(DeadlineEndpoint.Mediator);
        await host.Time.WaitForTimerAsync(mark, DeadlineKestrelHost.Limit, Guard); // the first message's time, from the accept
        await peer.SendFrameAsync(RawFrame.Text, fin: false, packed.AsMemory(0, packed.Length / 2));
        await peer.PingAsync("first-fragment-read"); // the server has read the fragment
        await peer.DisposeAsync(); // abort: no close frame, the message unfinished
        await host.WaitEndedWebSocketsAsync(2);

        ack.Started.Task.IsCompleted.Should().BeFalse("half a message is never unpacked or dispatched");
        E2E.StoreCount(host).Should().Be(1);
        host.TimersCreatedSince(mark).Should().Be(1, "only the receive deadline: no message was in hand, so no close grace was started");
        Loud(host).Should().BeEmpty();
    }

    // ---- (c) one finish per connection ---------------------------------------------------------------

    [Fact]
    public async Task After_an_abort_the_message_in_hand_is_applied_and_the_message_behind_it_is_never_read()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id);

        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await E2E.SendAcknowledgementAsync(host, socket, bob, id); // message 1, held
        await ack.Started.Task.WaitAsync(Guard);
        var behind = host.Probe(); // message 2, sent whole behind it
        await DeadlinePeer.SendAsync(socket, await host.PackAsync(behind), endOfMessage: true);
        var mark = host.Time.Mark();
        socket.Abort();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);
        ack.Release();

        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue();
        await host.WaitEndedWebSocketsAsync(2);
        host.Recorder.Invocations.Should().Be(0, "nothing is read after the end: message 2 was never dispatched");
        host.TimersCreatedSince(mark).Should().Be(1, "one finish for the connection");
        Loud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task After_the_eviction_backstop_ends_a_connection_the_message_in_hand_is_applied_and_the_message_behind_it_is_never_read()
    {
        // A session-initiated end (JC-21: the evicted peer never answers the close frame, and after the
        // close grace the session cancels the connection) leaves the socket readable, so here only the
        // loop's own rule keeps message 2 unread. Takes the real 5 s of the backstop.
        var (host, store, _) = await StartAsync(o => o.Builder = b =>
            b.Services.AddOptions<ReturnRouteSessionOptions>().Configure(s => s.MaxSessionsPerPeer = 1));
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);

        using var first = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await host.AskAsync(first, bob, MessagePickup40.CreateStatusRequest(bob.Did, host.Did)); // Bob's session on it
        var evicted = (WebSocketReturnRouteSession)host.Registry.Find(bob.Did).Should().ContainSingle().Subject;
        var ack = store.HoldNextAcknowledgementOf(id);
        await E2E.SendAcknowledgementAsync(host, first, bob, id); // message 1, held
        await ack.Started.Task.WaitAsync(Guard);
        var behind = host.Probe(); // message 2
        await DeadlinePeer.SendAsync(first, await host.PackAsync(behind), endOfMessage: true);

        var mark = host.Time.Mark();
        using var second = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await host.AskAsync(second, bob, MessagePickup40.CreateStatusRequest(bob.Did, host.Did)); // evicts the first session
        evicted.CloseReason.Should().Be(SessionCloseReason.Evicted);
        // The first peer never reads, so it never answers the close frame: the backstop ends the connection.
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);
        ack.Release();

        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Applied.Task.IsCompletedSuccessfully.Should().BeTrue("a message received in full before the server ended the connection is still applied");
        await host.WaitEndedWebSocketsAsync(2);
        host.Recorder.Invocations.Should().Be(0, "nothing is read after the end, although the socket could still be read");
        second.State.Should().Be(WebSocketState.Open);
        Loud(host).Should().BeEmpty();
    }

    // ---- (d) the host's shutdown, (e) the bound ------------------------------------------------------

    [Fact]
    public async Task The_hosts_shutdown_ends_a_finish_before_its_bound()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id);

        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await E2E.SendAcknowledgementAsync(host, socket, bob, id);
        await ack.Started.Task.WaitAsync(Guard);
        var mark = host.Time.Mark();
        socket.Abort();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);
        var fired = host.Time.TimersFired;

        host.App.Lifetime.StopApplication(); // ApplicationStopping; the clock never moves
        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Cancelled.Task.IsCompleted.Should().BeTrue("the host's shutdown ends the finish");
        ack.Applied.Task.IsCompleted.Should().BeFalse();
        host.Time.TimersFired.Should().Be(fired, "the bound did not fire: the shutdown came first");
        E2E.StoreCount(host).Should().Be(1, "the message stays queued and is delivered again (at-least-once)");
        await host.WaitEndedWebSocketsAsync(2);
        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Debug && r.Text.Contains("the host is stopping; the handling is cancelled", StringComparison.Ordinal));
        Loud(host).Should().BeEmpty();
        NoWireValues(host, bob, id);
    }

    [Fact]
    public async Task The_close_grace_ends_a_finish_that_outlasts_it_and_not_a_tick_before()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        await host.WaitEndedWebSocketsAsync(1);
        var ack = store.HoldNextAcknowledgementOf(id); // never released

        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await E2E.SendAcknowledgementAsync(host, socket, bob, id);
        await ack.Started.Task.WaitAsync(Guard);
        var mark = host.Time.Mark();
        socket.Abort();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard); // the grace runs from the drop
        var fired = host.Time.TimersFired;

        host.Time.Advance(Grace - DeadlineKestrelHost.Tick);
        host.Time.TimersFired.Should().Be(fired, "just inside the grace nothing fires");
        ack.Cancelled.Task.IsCompleted.Should().BeFalse();
        host.Time.Advance(DeadlineKestrelHost.Tick);
        host.Time.TimersFired.Should().Be(fired + 1, "the grace's timer fires at the bound");

        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Cancelled.Task.IsCompleted.Should().BeTrue("the bound ends a finish that outlasts it");
        ack.Applied.Task.IsCompleted.Should().BeFalse();
        E2E.StoreCount(host).Should().Be(1);
        await host.WaitEndedWebSocketsAsync(2);
        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Debug && r.Text.Contains("did not finish within 5 s; it is cancelled", StringComparison.Ordinal));
        Loud(host).Should().BeEmpty();
        NoWireValues(host, bob, id);
    }

    // ---- (f) HTTP is unchanged -----------------------------------------------------------------------

    [Fact]
    public async Task Over_HTTP_a_client_that_aborts_its_request_still_cancels_the_handling()
    {
        var (host, store, _) = await StartAsync();
        await using var _host = host;
        var (bob, id) = await E2E.QueueAndLeaseOneAsync(host);
        var ack = store.HoldNextAcknowledgementOf(id);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var content = new ByteArrayContent(await E2E.PackAcknowledgementAsync(host, bob, id));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var abort = new CancellationTokenSource();
        var post = http.PostAsync($"http://127.0.0.1:{host.Port}{DeadlineKestrelHost.MediatorPath}", content, abort.Token);
        await ack.Started.Task.WaitAsync(Guard);
        abort.Cancel(); // the client gives up: its connection is closed under the request

        await Task.WhenAny(ack.Applied.Task, ack.Cancelled.Task).WaitAsync(Guard);
        ack.Cancelled.Task.IsCompleted.Should().BeTrue("over HTTP the request's abort still cancels the handling (#90: abort before commit never relays)");
        ack.Applied.Task.IsCompleted.Should().BeFalse();
        E2E.StoreCount(host).Should().Be(1);
        await ((Func<Task>)(() => post)).Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- (g) a reply and a push after the drop fail quietly: see WebSocketMessageFinishQuietTests ----

    // ---- STOMP ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Under_STOMP_a_complete_SEND_whose_sender_aborts_while_it_is_handled_is_still_dispatched()
    {
        var (host, _, holding) = await StartAsync(o => o.Receive = Stomp);
        await using var _host = host;
        var socket = await host.ConnectAsync(DeadlineEndpoint.Dispatch);
        await ConnectStompAsync(socket);

        await SendStompAsync(socket, SendFrame(await host.PackAsync(Hold(host))));
        await holding.Entered.Task.WaitAsync(Guard);
        var mark = host.Time.Mark();
        socket.Abort();
        await host.Time.WaitForTimerAsync(mark, Grace, Guard);
        holding.Release();

        await Task.WhenAny(holding.Handled.Task, holding.Cancelled.Task).WaitAsync(Guard);
        holding.Handled.Task.IsCompletedSuccessfully.Should().BeTrue("the SEND was received in full before the drop");
        await host.WaitEndedWebSocketsAsync(1);
        Loud(host).Should().BeEmpty();
    }

    [Fact]
    public async Task Under_STOMP_a_CONNECT_complete_when_the_connection_ended_writes_its_CONNECTED_into_the_dead_socket_quietly()
    {
        GatedDisposeClock gate = null!;
        var (host, _, _) = await StartAsync(o =>
        {
            o.Receive = Stomp;
            o.Builder = b => gate = GatedDisposeClock.Install(b);
        });
        await using var _host = host;

        gate.Arm(DeadlineKestrelHost.Limit);
        var socket = await host.ConnectAsync(DeadlineEndpoint.Dispatch);
        await SendStompAsync(socket, ConnectFrame());
        await gate.Held.WaitAsync(Guard); // CONNECT read in full; the loop is held before it handles it
        await AbortAndWaitUntilTheServerSawItAsync(host, socket);
        var mark = host.Time.Mark();
        gate.Release();

        // Nothing holds the CONNECT's handling, so its close grace's timer is created and disposed at
        // once: count the timers created once the connection ended, never wait for one to be scheduled.
        await host.WaitEndedWebSocketsAsync(1);
        host.TimersCreatedSince(mark).Should().Be(1, "CONNECT was handled with the connection already ended: its close grace started at once");
        Loud(host).Should().BeEmpty("a write into a connection the peer dropped is not an application error");
        host.Logs.Records.Should().NotContain(r => r.Text.Contains("unhandled", StringComparison.OrdinalIgnoreCase));
    }

    // ---- a handler's own cancellation is a fault of its message, not the end of the connection --------

    [Fact]
    public async Task A_handler_that_throws_the_cancellation_of_its_own_timeout_leaves_the_connection_serving_the_next_message()
    {
        var state = new TimingOutState();
        var (host, _, _) = await StartAsync(o => o.Builder = b =>
        {
            b.Services.AddSingleton(state);
            b.Services.AddSingleton<IProtocolHandler, TimingOutHandler>();
        });
        await using var _host = host;
        var socket = await host.ConnectAsync(DeadlineEndpoint.Dispatch);

        var mark = host.Time.Mark();
        await DeadlinePeer.SendAsync(socket, await host.PackAsync(TimingOutHandler.Message(host)), endOfMessage: true);
        await host.Time.WaitForTimerAsync(mark, TimingOutHandler.OwnTimeout, Guard); // the handler's own timeout runs
        await state.Entered.Task.WaitAsync(Guard);
        var fired = host.Time.TimersFired;
        host.Time.Advance(TimingOutHandler.OwnTimeout);
        host.Time.TimersFired.Should().Be(fired + 1, "only the handler's own timeout fires");
        await state.TimedOut.Task.WaitAsync(Guard); // it threw a TaskCanceledException of its own token

        var next = host.Probe();
        await DeadlinePeer.SendAsync(socket, await host.PackAsync(next), endOfMessage: true);
        await host.WaitDispatchedAsync(DeadlineEndpoint.Dispatch, next.Id);
        socket.State.Should().Be(WebSocketState.Open, "a handler's own cancellation does not end the connection");
        host.EndedWebSockets.Should().Be(0);
        host.Logs.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Text.Contains("cancelled by a token of its own", StringComparison.Ordinal));
        using (var guard = new CancellationTokenSource(Guard))
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", guard.Token);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the connection still answers a close");
        await host.WaitEndedWebSocketsAsync(1);
        Loud(host).Should().BeEmpty();
    }

    // ---- harness -------------------------------------------------------------------------------------

    internal static async Task<(DeadlineKestrelHost Host, AckDelayingStore Store, HoldingHandler Holding)> StartAsync(Action<DeadlineKestrelHostOptions>? configure = null)
    {
        AckDelayingStore store = null!;
        var holding = new HoldingHandler();
        var host = await DeadlineKestrelHost.StartAsync(o =>
        {
            configure?.Invoke(o);
            var builder = o.Builder;
            o.Builder = b =>
            {
                builder?.Invoke(b);
                AckDelayingStore.Register(b.Services, x => store = x);
                b.Services.AddSingleton<IProtocolHandler>(holding);
            };
        });
        return (host, store, holding);
    }

    /// <summary>A message of the holding protocol from Alice to the host, stamped now; with <c>return_route: all</c> when asked.</summary>
    internal static Message Hold(DeadlineKestrelHost host, bool returnRoute = false)
    {
        var builder = new MessageBuilder()
            .WithType(HoldingHandler.Piuri + "/hold")
            .WithFrom(host.Alice.Did)
            .WithTo(host.Did)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (returnRoute)
            builder = builder.WithReturnRoute(ReturnRouteMode.All);
        return builder.Build();
    }

    /// <summary>Abort <paramref name="socket"/> (no close frame) and wait until the server's abort token for it fired.</summary>
    internal static async Task AbortAndWaitUntilTheServerSawItAsync(DeadlineKestrelHost host, ClientWebSocket socket)
    {
        var aborted = host.WebSocketRequestAborts[^1];
        socket.Abort();
        (await ReturnRouteTestHost.WaitUntilAsync(() => aborted.IsCancellationRequested, Guard)).Should().BeTrue("the server sees the drop");
    }

    /// <summary>Lines at Error or above, from any category.</summary>
    internal static IReadOnlyList<string> Loud(DeadlineKestrelHost host)
        => host.Logs.CategorizedRecords.Where(r => r.Level >= LogLevel.Error).Select(r => $"{r.Category} [{r.Level}] {r.Text}").ToList();

    // D7: the finish's lines carry no wire value.
    private static void NoWireValues(DeadlineKestrelHost host, TestPeer bob, string id)
    {
        var lines = host.Logs.Records.Where(r => r.Text.Contains("received in full", StringComparison.Ordinal)).Select(r => r.Text).ToList();
        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(l => !l.Contains(bob.Did, StringComparison.Ordinal) && !l.Contains(host.Did, StringComparison.Ordinal) && !l.Contains(id, StringComparison.Ordinal));
    }

    private static void Stomp(DidCommReceiveOptions r)
    {
        r.MessageReceiveTimeout = DeadlineKestrelHost.Limit;
        r.UseStomp = true;
    }

    private static StompFrame ConnectFrame() => new("CONNECT", new[]
    {
        KeyValuePair.Create("accept-version", "1.2"),
        KeyValuePair.Create("host", "localhost"),
    }, ReadOnlyMemory<byte>.Empty);

    private static StompFrame SendFrame(byte[] envelope) => new("SEND", new[]
    {
        KeyValuePair.Create("destination", DeadlineKestrelHost.DispatchPath),
        KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
    }, envelope);

    private static async Task ConnectStompAsync(ClientWebSocket socket)
    {
        await SendStompAsync(socket, ConnectFrame());
        var (_, connected) = await ReturnRouteTestHost.ReceiveAsync(socket, Guard);
        connected.Should().StartWith("CONNECTED");
    }

    private static async Task SendStompAsync(WebSocket socket, StompFrame frame)
    {
        using var cts = new CancellationTokenSource(Guard);
        await socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
    }
}

/// <summary>
/// #100 OD-8 (g): once the connection ended, the session is closed, so a push the handler makes and the
/// reply it returns while the loop finishes the message are refused (<c>false</c>), quietly: no Error
/// line, and no task of the library ends with an exception nobody observed. Serial, because
/// <see cref="TaskScheduler.UnobservedTaskException"/> is process-wide.
/// </summary>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketMessageFinishQuietTests
{
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    [Fact]
    public async Task A_push_and_a_reply_made_after_the_drop_are_refused_quietly()
    {
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var inner in e.Exception.Flatten().InnerExceptions)
                if (inner.ToString().Contains("DidComm.AspNetCore", StringComparison.Ordinal))
                    unobserved.Enqueue(inner);
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await PushAndReplyAfterTheDropAsync();
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            unobserved.Should().BeEmpty("no library task ends with an unobserved exception");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task PushAndReplyAfterTheDropAsync()
    {
        var (host, _, holding) = await WebSocketMessageFinishTests.StartAsync();
        await using var _host = host;
        holding.Behavior = _ => (Push(host, "push"), Push(host, "reply"));

        var socket = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        await DeadlinePeer.SendAsync(socket, await host.PackAsync(WebSocketMessageFinishTests.Hold(host, returnRoute: true)), endOfMessage: true);
        var context = await holding.Entered.Task.WaitAsync(Guard);
        var session = context.Session.Should().BeOfType<WebSocketReturnRouteSession>().Subject; // the message opened Alice's session
        var mark = host.Time.Mark();
        socket.Abort();
        await host.Time.WaitForTimerAsync(mark, WebSocketReturnRouteSession.DefaultCloseGrace, Guard);
        session.IsClosed.Should().BeTrue("the session closes as soon as its connection ends");
        session.CloseReason.Should().Be(SessionCloseReason.Unregistered);
        holding.Release();

        (await holding.Handled.Task.WaitAsync(Guard)).Should().BeFalse("the push after the drop is refused");
        await host.WaitEndedWebSocketsAsync(1);
        host.Logs.Records.Should().Contain(r => r.Text.Contains("produced a reply", StringComparison.Ordinal) && r.Text.Contains("returned-on-session=False", StringComparison.Ordinal),
            "the reply after the drop is refused, and only logged");
        WebSocketMessageFinishTests.Loud(host).Should().BeEmpty();
        host.Registry.Count.Should().Be(0);
    }

    private static Message Push(DeadlineKestrelHost host, string what)
        => new MessageBuilder()
            .WithType(HoldingHandler.Piuri + "/" + what)
            .WithFrom(host.Did)
            .WithTo(host.Alice.Did)
            .Build();
}

/// <summary>
/// A protocol handler for the OD-8 tests: it holds each message until <see cref="Release"/>, honouring
/// its token, then makes the push <see cref="Behavior"/> asks for through the session (if any) and
/// returns the reply. One message at a time.
/// </summary>
internal sealed class HoldingHandler : IProtocolHandler
{
    public const string Piuri = "https://didcomm.org/test-od8/1.0";

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _invocations;

    public string ProtocolUri => Piuri;

    /// <summary>The handler has the message (and its context) and is holding it.</summary>
    public TaskCompletionSource<ProtocolContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The handler's token fired while it held the message.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The handler finished; the value is the push's result, or <c>null</c> when it made none.</summary>
    public TaskCompletionSource<bool?> Handled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What to push and what to return once released.</summary>
    public Func<ProtocolContext, (Message? Push, Message? Reply)>? Behavior { get; set; }

    public int Invocations => Volatile.Read(ref _invocations);

    public void Release() => _release.TrySetResult();

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _invocations);
        Entered.TrySetResult(context);
        try
        {
            await _release.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Cancelled.TrySetResult();
            throw;
        }
        var (push, reply) = Behavior?.Invoke(context) ?? (null, null);
        bool? pushed = null;
        if (push is not null && context.Session is not null)
            pushed = await context.Session.TrySendAsync(push, ct);
        Handled.TrySetResult(pushed);
        return reply;
    }
}

/// <summary>What the <see cref="TimingOutHandler"/> did.</summary>
internal sealed class TimingOutState
{
    /// <summary>The handler has the message and its own timeout is running.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The handler's own timeout fired and it threw its cancellation.</summary>
    public TaskCompletionSource TimedOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A protocol handler that waits on a timeout of its own, timed on the container's clock (as a handler
/// whose DID resolution or HTTP call times out does), and lets that timeout's
/// <see cref="TaskCanceledException"/> escape: a cancellation that is not the receive loop's.
/// </summary>
internal sealed class TimingOutHandler(TimingOutState state, TimeProvider time) : IProtocolHandler
{
    public const string Piuri = "https://didcomm.org/test-od8-timeout/1.0";

    /// <summary>The handler's own timeout: a value no other timer of these tests has.</summary>
    public static readonly TimeSpan OwnTimeout = TimeSpan.FromSeconds(11);

    public string ProtocolUri => Piuri;

    /// <summary>A message of this protocol from Alice to the host, stamped now.</summary>
    public static Message Message(DeadlineKestrelHost host)
        => new MessageBuilder()
            .WithType(Piuri + "/wait")
            .WithFrom(host.Alice.Did)
            .WithTo(host.Did)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Build();

    public async Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        using var own = new CancellationTokenSource(OwnTimeout, time);
        state.Entered.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, own.Token);
        }
        catch (OperationCanceledException)
        {
            state.TimedOut.TrySetResult();
            throw;
        }
        return null;
    }
}

/// <summary>
/// The host's <see cref="QaManualTimeProvider"/>, able to hold the receive loop inside the stop of a
/// message's receive deadline: the first timer created after <see cref="Arm"/> with the armed due
/// time blocks in its <see cref="ITimer.Dispose"/> until <see cref="Release"/>. The loop stops a
/// message's deadline right after its last frame, before it handles it, so a test can end the
/// connection while a complete message waits there (L-065 rule 6: the product parked at the point the
/// claim is about).
/// </summary>
internal sealed class GatedDisposeClock(QaManualTimeProvider inner) : TimeProvider
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan? _armed;

    /// <summary>The armed timer's dispose is blocking.</summary>
    public Task Held => _held.Task;

    // The armed dispose parks a thread-pool thread (the receive loop's): raise the pool's floor (never
    // lower it) first, so parking it cannot starve tests running alongside (L-065 corollary).
    private static readonly bool PoolFloorRaised = RaisePoolFloor();

    private static bool RaisePoolFloor()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var needed = Environment.ProcessorCount + 4;
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
        return true;
    }

    /// <summary>Wrap the clock <c>DeadlineKestrelHost</c> registered and register the wrapper in its place.</summary>
    public static GatedDisposeClock Install(WebApplicationBuilder builder)
    {
        _ = PoolFloorRaised;
        var manual = (QaManualTimeProvider)builder.Services.Last(d => d.ServiceType == typeof(TimeProvider)).ImplementationInstance!;
        var gated = new GatedDisposeClock(manual);
        builder.Services.AddSingleton<TimeProvider>(gated);
        return gated;
    }

    /// <summary>Gate the next timer created with <paramref name="dueTime"/>.</summary>
    public void Arm(TimeSpan dueTime)
    {
        lock (_lock)
            _armed = dueTime;
    }

    public void Release() => _release.Set();

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        lock (_lock)
        {
            if (_armed != dueTime)
                return timer;
            _armed = null;
        }
        return new GatedTimer(timer, this);
    }

    private sealed class GatedTimer(ITimer timer, GatedDisposeClock owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            owner._held.TrySetResult();
            owner._release.Wait(DeadlineKestrelHost.Guard);
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
