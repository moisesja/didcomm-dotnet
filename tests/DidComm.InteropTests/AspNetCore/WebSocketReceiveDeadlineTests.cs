using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.Stomp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The WebSocket receive time limit (<c>DidCommReceiveOptions.MessageReceiveTimeout</c>) on real
/// Kestrel, on every endpoint that applies it: <c>MapDidCommWebSocket(pattern)</c>,
/// <c>MapDidCommWebSocket(pattern, onReceive)</c> and the WebSocket endpoint of
/// <c>MapDidCommMediator</c>. A peer has the limit to deliver each whole message, counted from its
/// first part, and a new connection has it to finish its first message, counted from the accept; the
/// wait between messages is never timed. On expiry the server sends a 1008 close and drops what it
/// read.
/// </summary>
/// <remarks>
/// The container's clock is a manual one and the limit is 37 s, a value no default has. Before every
/// clock move the test waits for the specific deadline timer (created after a mark, due exactly when
/// the claim says) and then asserts how many timers the move fired, so nothing here depends on machine
/// speed; real time is only a hang guard. A raw-frame peer is used where the test must control frames
/// (fragments, pings in the middle of a message) or see pongs: a pong proves the server read every
/// frame sent before its ping.
/// </remarks>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketReceiveDeadlineTests
{
    private static readonly TimeSpan Limit = DeadlineKestrelHost.Limit;
    private static readonly TimeSpan Tick = DeadlineKestrelHost.Tick;
    private static readonly TimeSpan Guard = DeadlineKestrelHost.Guard;

    public static TheoryData<DeadlineEndpoint> Endpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
        DeadlineEndpoint.Mediator,
    };

    public static TheoryData<DeadlineEndpoint> SessionEndpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Mediator,
    };

    public static TheoryData<DeadlineEndpoint> StompEndpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
    };

    // ---- a new connection ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_connection_that_stays_silent_is_closed_with_1008_when_the_limit_counted_from_the_accept_runs_out_and_not_before(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard); // the first message's time, started at the accept
        var fired = host.Time.TimersFired;
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);

        host.Time.Advance(Limit - Tick);
        host.Time.TimersFired.Should().Be(fired, "nothing is due before the limit");
        closing.IsCompleted.Should().BeFalse("the connection stays open until the limit");

        host.Time.Advance(Tick);
        host.Time.TimersFired.Should().Be(fired + 1, "the connection's receive deadline fired at the limit");
        var (type, status, reason) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close, "the server sends a close frame, not a bare disconnect");
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        reason.Should().Be("message receive timeout");
        host.DispatchedCount(endpoint).Should().Be(0);
        host.TimeoutLines().Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information, "the first expiry on an endpoint is written at Information");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_first_message_finished_just_inside_the_limit_is_dispatched_and_the_connection_then_stays_open_untimed_for_a_later_message(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var first = host.Probe();
        var later = host.Probe();
        var firstBytes = await host.PackAsync(first);
        var laterBytes = await host.PackAsync(later);
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard);
        var fired = host.Time.TimersFired;

        host.Time.Advance(Limit - Tick);
        await DeadlinePeer.SendAsync(ws, firstBytes, endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, first.Id);
        host.Time.ScheduledTimersSince(mark).Should().Be(0, "the first message's time stopped when its last frame arrived");

        var idle = host.Time.Mark();
        host.Time.Advance(10 * Limit);
        host.Time.TimersFired.Should().Be(fired, "the wait for the next message is not timed");

        await DeadlinePeer.SendAsync(ws, laterBytes, endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, later.Id);
        host.TimersCreatedSince(idle).Should().Be(0, "neither the idle wait nor a message read in one part starts a timer");
        ws.State.Should().Be(WebSocketState.Open);
        host.TimeoutLines().Should().BeEmpty();
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ---- a later message -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_later_message_stalled_after_its_first_part_is_closed_with_1008_at_the_limit_counted_from_that_part(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var first = host.Probe();
        using var ws = await host.ConnectAsync(endpoint);
        await DeadlinePeer.SendAsync(ws, await host.PackAsync(first), endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, first.Id);
        host.Time.Advance(3 * Limit); // idle, untimed: it counts neither against this connection nor against the next message

        var stalled = await host.PackAsync(host.Probe());
        var mark = host.Time.Mark();
        var fired = host.Time.TimersFired;
        await DeadlinePeer.SendAsync(ws, stalled.AsMemory(0, 100), endOfMessage: false);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard); // the message's time started at its first part
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);

        host.Time.Advance(Limit - Tick);
        host.Time.TimersFired.Should().Be(fired);
        closing.IsCompleted.Should().BeFalse();

        host.Time.Advance(Tick);
        host.Time.TimersFired.Should().Be(fired + 1);
        var (type, status, reason) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        reason.Should().Be("message receive timeout");
        host.DispatchedCount(endpoint).Should().Be(1, "what was read of the stalled message is dropped, never processed");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_message_trickled_one_small_fragment_per_clock_step_with_pings_between_is_still_closed_at_the_limit_from_its_first_part(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        await using var peer = await host.ConnectRawAsync(endpoint);
        var first = host.Probe();
        await peer.SendFrameAsync(RawFrame.Text, fin: true, await host.PackAsync(first));
        await host.WaitDispatchedAsync(endpoint, first.Id);

        var mark = host.Time.Mark();
        var fired = host.Time.TimersFired;
        await peer.SendFrameAsync(RawFrame.Text, fin: false, "{\"id\":"u8.ToArray());
        await peer.PingAsync("first part");
        await host.Time.WaitForTimerAsync(mark, Limit, Guard);

        // One small fragment per 5 s step, each followed by a ping whose pong proves the server read it.
        var step = TimeSpan.FromSeconds(5);
        var elapsed = TimeSpan.Zero;
        for (var part = 1; elapsed + step < Limit; part++)
        {
            host.Time.Advance(step);
            elapsed += step;
            host.Time.TimersFired.Should().Be(fired, $"{elapsed} after the first part is inside the limit");
            await peer.SendFrameAsync(RawFrame.Continuation, fin: false, "\"x"u8.ToArray());
            await peer.PingAsync($"part {part}");
            host.Time.ScheduledTimersSince(mark).Should().Be(1, "the whole message has one timer, not one per part");
        }

        host.Time.Advance(Limit - elapsed - Tick);
        host.Time.TimersFired.Should().Be(fired, "a part that arrives in time does not move the deadline, nor end it early");
        host.Time.Advance(Tick);
        host.Time.TimersFired.Should().Be(fired + 1, "the deadline counted from the first part fired, however many parts came since");
        var close = await peer.ReadCloseAsync(Guard);

        close.Should().NotBeNull("the server sends a close frame before it ends the connection");
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.PolicyViolation);
        close.CloseReason.Should().Be("message receive timeout");
        host.DispatchedCount(endpoint).Should().Be(1);
        await peer.SendFrameAsync(RawFrame.Close, fin: true, close.Payload.AsMemory(0, 2));
    }

    // ---- return-route sessions and live delivery ----------------------------------------------

    [Theory]
    [MemberData(nameof(SessionEndpoints))]
    public async Task An_idle_return_route_session_keeps_getting_pushes_past_many_limits_and_a_stalled_message_on_its_socket_ends_the_session_with_1008(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = r =>
        {
            r.MessageReceiveTimeout = Limit;
            r.AllowSameSocketReplies = true;
        });
        using var ws = await host.ConnectAsync(endpoint);
        var opening = host.Probe(returnRoute: true);
        await DeadlinePeer.SendAsync(ws, await host.PackAsync(opening), endOfMessage: true);
        await host.WaitDispatchedAsync(endpoint, opening.Id);
        var session = host.Recorder.Seen.Single(r => r.Message.Id == opening.Id).Session;
        session.Should().NotBeNull("an authcrypted message with return_route all opens a session");
        host.Registry.Find(host.Alice.Did).Should().ContainSingle();

        var idle = host.Time.Mark();
        var fired = host.Time.TimersFired;
        host.Time.Advance(10 * Limit);
        host.Time.TimersFired.Should().Be(fired, "an idle session is not timed");
        host.TimersCreatedSince(idle).Should().Be(0);

        var push = Push(host);
        (await session!.TrySendAsync(push, CancellationToken.None)).Should().BeTrue("the session is still open");
        (await DeadlineKestrelHost.ReceiveMessageAsync(ws, host.Alice)).Id.Should().Be(push.Id);

        var mark = host.Time.Mark();
        await DeadlinePeer.SendAsync(ws, (await host.PackAsync(host.Probe())).AsMemory(0, 100), endOfMessage: false);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard);
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);
        host.Time.Advance(Limit);
        var (type, status, reason) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        reason.Should().Be("message receive timeout");
        host.Registry.Find(host.Alice.Did).Should().BeEmpty("the session is unregistered before the close frame is sent");
        (await session.TrySendAsync(Push(host), CancellationToken.None)).Should().BeFalse("nothing is pushed after the session ended");
    }

    [Fact]
    public async Task On_the_mediator_endpoint_an_idle_live_delivery_socket_stays_open_past_many_limits_and_gets_the_next_arrival()
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var bob = await host.EnrolAsync();
        using var ws = await host.ConnectAsync(DeadlineEndpoint.Mediator);
        var status = await host.AskAsync(ws, bob, MessagePickup.CreateLiveDeliveryChange(bob.Did, host.Did, liveDelivery: true));
        status.Type.Should().Be(MessagePickup.StatusType);
        // Turning live delivery on wakes the mediator's push worker, whose per-push deadline is on the
        // same clock: wait until only the purge loop's timer is left, so the move below can fire
        // nothing but a receive deadline.
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Time.ScheduledTimers == 1, Guard)).Should().BeTrue();

        var idle = host.Time.Mark();
        var fired = host.Time.TimersFired;
        host.Time.Advance(10 * Limit);
        host.Time.TimersFired.Should().Be(fired, "an idle live-delivery socket is not timed");
        host.TimersCreatedSince(idle).Should().Be(0);

        (await host.PostForwardAsync(bob)).Should().Be(HttpStatusCode.Accepted);
        var pushed = await DeadlineKestrelHost.ReceiveMessageAsync(ws, bob);

        MessagePickup.TryParseDelivery(pushed, out var delivered, out _).Should().BeTrue("the arrival is pushed live on the idle socket");
        delivered!.Should().ContainSingle();
        ws.State.Should().Be(WebSocketState.Open);
        host.TimeoutLines().Should().BeEmpty();
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    // ---- STOMP ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StompEndpoints))]
    public async Task Under_STOMP_a_connection_that_never_sends_CONNECT_is_closed_with_1008_at_the_limit(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = r =>
        {
            r.MessageReceiveTimeout = Limit;
            r.UseStomp = true;
        });
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard);
        var fired = host.Time.TimersFired;
        var closing = DeadlinePeer.ReceiveCloseAsync(ws);

        host.Time.Advance(Limit - Tick);
        host.Time.TimersFired.Should().Be(fired);
        closing.IsCompleted.Should().BeFalse();
        host.Time.Advance(Tick);
        var (type, status, reason) = await closing.WaitAsync(Guard);

        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.PolicyViolation);
        reason.Should().Be("message receive timeout");
    }

    [Theory]
    [MemberData(nameof(StompEndpoints))]
    public async Task Under_STOMP_a_connection_idle_after_CONNECT_stays_open_past_many_limits_and_its_next_SEND_is_dispatched(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync(o => o.Receive = r =>
        {
            r.MessageReceiveTimeout = Limit;
            r.UseStomp = true;
        });
        var mark = host.Time.Mark();
        using var ws = await host.ConnectAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, Limit, Guard);
        await SendStompAsync(ws, new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "localhost"),
        }, ReadOnlyMemory<byte>.Empty));
        var (_, connected) = await ReturnRouteTestHost.ReceiveAsync(ws, Guard);
        connected.Should().StartWith("CONNECTED");
        host.Time.ScheduledTimersSince(mark).Should().Be(0, "CONNECT was the first message: its time stopped");

        var idle = host.Time.Mark();
        var fired = host.Time.TimersFired;
        host.Time.Advance(10 * Limit);
        host.Time.TimersFired.Should().Be(fired, "the wait after CONNECT is not timed");

        var message = host.Probe();
        await SendStompAsync(ws, new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", DeadlineKestrelHost.PathOf(endpoint)),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, await host.PackAsync(message)));
        await host.WaitDispatchedAsync(endpoint, message.Id);
        host.TimersCreatedSince(idle).Should().Be(0);
        ws.State.Should().Be(WebSocketState.Open);
        await SendStompAsync(ws, new StompFrame("DISCONNECT", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty));
        var (type, status, _) = await DeadlinePeer.ReceiveCloseAsync(ws);
        type.Should().Be(WebSocketMessageType.Close);
        status.Should().Be(WebSocketCloseStatus.NormalClosure);
    }

    // ---- harness ----------------------------------------------------------------------------

    private static Message Push(DeadlineKestrelHost host)
        => new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/push")
            .WithFrom(host.Did)
            .WithTo(host.Alice.Did)
            .Build();

    private static async Task SendStompAsync(WebSocket socket, StompFrame frame)
    {
        using var cts = new CancellationTokenSource(Guard);
        await socket.SendAsync(StompFrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
    }
}
