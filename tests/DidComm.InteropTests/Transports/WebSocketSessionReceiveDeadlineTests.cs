using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// The client transport's time limit on messages pushed over a return-route session socket
/// (<see cref="WebSocketTransportOptions.MessageReceiveTimeout"/>): each message must arrive whole
/// within the limit of its first part, or the socket is closed with 1008 (policy violation) and the
/// rest of its messages are discarded; the wait between messages is never timed, and neither is the
/// inbound consumer.
/// </summary>
/// <remarks>
/// <para>
/// The close and stall behaviour runs against a real Kestrel peer on loopback that writes raw frames
/// (<see cref="RawFrameWebSocketPeer"/>) and a real <see cref="ClientWebSocket"/> in the transport,
/// because cancelling, aborting and closing a socket behave differently in test doubles. The loop
/// logic that needs an exact interleaving uses <see cref="ScriptedSessionWebSocket"/>.
/// </para>
/// <para>
/// The limit is timed on a hand-moved clock passed to the transport. Before each move the test waits
/// for the specific timer (created after a mark, due exactly the limit from now) and afterwards
/// asserts how many timers the move fired; a ping's pong shows that the client's reader has handled
/// everything sent before the ping. Real time is only a hang guard.
/// </para>
/// </remarks>
public sealed class WebSocketSessionReceiveDeadlineTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    // Not the default (60 s), so every test that waits for a timer due at this time proves the set value is used.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly Uri ScriptedEndpoint = new("ws://agents.r.us/ws");
    private const string Alice = "did:example:alice";
    private const string Bob = "did:example:bob";
    private const string TimeoutLine = "was not finished within";

    // ---- real Kestrel peer, real ClientWebSocket ----------------------------------------------

    [Fact]
    public async Task An_idle_session_socket_is_never_timed_and_a_later_push_still_reaches_the_consumer()
    {
        await using var t = await Live.StartAsync();
        var mark = t.Clock.Mark();
        var s = await t.OpenSessionAsync();

        // The reader waits for a first message (its pong came from inside that wait). The wait is not
        // timed: no timer exists, so many limits pass and nothing fires.
        t.Clock.Mark().Should().Be(mark, "no timer is created for the wait between messages");
        t.Clock.Advance(Limit * 10);
        t.Clock.TimersFired.Should().Be(0);
        (await s.PingAsync(Guard)).Should().BeEmpty("the idle session socket is still open and reading: no close frame came");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);

        // A message in one frame starts no timer either.
        await s.SendTextAsync(Utf8("whole"), endOfMessage: true);
        (await WaitUntilAsync(() => t.Consumer!.Calls == 1)).Should().BeTrue();
        t.Clock.Mark().Should().Be(mark, "a message read in one part is never timed");

        // A message in parts is timed from its first part, not from the start of the idle wait.
        await s.SendTextAsync(Utf8("two "), endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);
        await s.SendContinuationAsync(Utf8("parts"), endOfMessage: true);
        (await WaitUntilAsync(() => t.Consumer!.Calls == 2)).Should().BeTrue();
        t.Consumer!.Frames.Select(f => Text(f.Payload)).Should().Equal("whole", "two parts");
        t.Clock.ScheduledTimersSince(mark).Should().Be(0, "the message's timer stopped at its last frame");
        t.Clock.TimersFired.Should().Be(0);
    }

    [Fact]
    public async Task A_stalled_push_is_closed_with_1008_at_the_limit_and_the_rest_of_the_socket_is_discarded()
    {
        await using var t = await Live.StartAsync();
        var s = await t.OpenSessionAsync();

        // The first part of a message, and then nothing.
        var mark = t.Clock.Mark();
        await s.SendTextAsync(new byte[3000], endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);
        t.Clock.TimersFired.Should().Be(0);

        t.Clock.Advance(Limit - Tick);
        t.Clock.TimersFired.Should().Be(0);
        (await s.PingAsync(Guard)).Should().BeEmpty("one tick before the limit the socket is still open");

        t.Clock.Advance(Tick);
        t.Clock.TimersFired.Should().Be(1);
        var close = await NextCloseAsync(s);
        close.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        close.CloseReason.Should().Be("message receive timeout");
        t.Transport.ActiveSessionSocketCount.Should().Be(0, "the socket takes no more sends once it is closing");
        t.Transport.LiveSessionSocketCount.Should().Be(1, "it is still read, discarding, until the peer's close");

        // A header-less send to the same endpoint and DIDs goes to a pooled connection, not to S.
        await t.Transport.SendAsync(Request(t.Peer.Endpoint, "plain"), default).WaitAsync(Guard);
        var pooled = await t.Peer.NextConnectionAsync(Guard);
        Text(await pooled.NextMessageAsync(Guard)).Should().Be("plain");

        // The rest of the stalled message and a later whole message are read and dropped.
        await s.SendContinuationAsync(Utf8("rest"), endOfMessage: true);
        await s.SendTextAsync(Utf8("later"), endOfMessage: true);
        (await s.PingAsync(Guard)).Should().BeEmpty("the reader goes on reading after its close");
        t.Transport.InboundMessagesDropped.Should().Be(2);
        t.Consumer!.Calls.Should().Be(0, "neither the partial message nor anything after it reached the consumer");

        // The peer answers the close: the reader ends there and reports the socket's end once.
        await s.SendCloseAsync(WebSocketCloseStatus.PolicyViolation, "bye");
        s.End();
        (await WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0)).Should().BeTrue();
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(1);

        // A send that asks for a return route opens a fresh session socket.
        await t.Transport.SendAsync(Request(t.Peer.Endpoint, "again", ReturnRouteMode.All), default).WaitAsync(Guard);
        var fresh = await t.Peer.NextConnectionAsync(Guard);
        Text(await fresh.NextMessageAsync(Guard)).Should().Be("again");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
        t.Peer.Connections.Should().Be(3);

        var lines = t.Logs.Records.Where(r => r.Text.Contains(TimeoutLine, StringComparison.Ordinal)).ToList();
        lines.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        lines[0].Text.Should().Contain("1008").And.NotContain("ws://").And.NotContain("did:");
    }

    [Fact]
    public async Task A_stalled_push_whose_peer_never_answers_the_close_ends_at_the_backstop()
    {
        // The backstop is one SendTimeout of real time; short here so the test does not wait long.
        await using var t = await Live.StartAsync(configure: o => o.SendTimeout = TimeSpan.FromSeconds(1));
        var s = await t.OpenSessionAsync();
        var mark = t.Clock.Mark();
        await s.SendTextAsync(new byte[100], endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);

        t.Clock.Advance(Limit);
        t.Clock.TimersFired.Should().Be(1);
        (await NextCloseAsync(s)).CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);

        // The peer never answers: the backstop stops the reader and the client drops the connection.
        (await s.NextFrameAsync(Guard)).Should().BeNull("the client ends the connection once the backstop fires");
        (await WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0)).Should().BeTrue();
        t.Consumer!.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_trickle_with_pings_between_its_parts_is_still_cut_at_the_limit_of_its_first_part()
    {
        await using var t = await Live.StartAsync();
        var s = await t.OpenSessionAsync();
        var mark = t.Clock.Mark();
        await s.SendTextAsync(Utf8("part 0;"), endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);
        var armed = t.Clock.Mark();

        var step = Limit / 4;
        for (var i = 1; i <= 3; i++)
        {
            t.Clock.Advance(step);
            t.Clock.TimersFired.Should().Be(0);
            await s.SendPingAsync(new[] { (byte)i });
            await s.SendContinuationAsync(Utf8($"part {i};"), endOfMessage: false);
            await s.SendPingAsync(new[] { (byte)(100 + i) });
            (await s.PingAsync(Guard)).Should().BeEmpty("the socket is open after {0} of 4 steps", i);
            t.Clock.Mark().Should().Be(armed, "a later part never starts a new timer: the time runs from the first part");
        }

        t.Clock.Advance(step);
        t.Clock.TimersFired.Should().Be(1, "the first part's timer fires at the limit");
        (await NextCloseAsync(s)).CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        t.Consumer!.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_push_finished_just_inside_the_limit_reaches_the_consumer_and_the_socket_stays_open()
    {
        await using var t = await Live.StartAsync();
        var s = await t.OpenSessionAsync();
        var mark = t.Clock.Mark();
        // Larger than the reader's buffer, so the first part is read in several pieces.
        var first = Enumerable.Repeat((byte)'a', 20_000).ToArray();
        await s.SendTextAsync(first, endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);

        t.Clock.Advance(Limit - Tick);
        t.Clock.TimersFired.Should().Be(0);
        await s.SendContinuationAsync(Utf8("end"), endOfMessage: true);

        (await WaitUntilAsync(() => t.Consumer!.Calls == 1)).Should().BeTrue();
        t.Consumer!.Frames.Single().Payload.ToArray().Should().Equal(first.Concat(Utf8("end")));
        t.Clock.ScheduledTimersSince(mark).Should().Be(0, "the timer stopped at the last frame");
        t.Clock.Advance(Limit);
        t.Clock.TimersFired.Should().Be(0);
        (await s.PingAsync(Guard)).Should().BeEmpty("no close frame");
        t.Transport.ActiveSessionSocketCount.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_consumer_is_not_cut_off_by_the_limit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerToken = CancellationToken.None;
        await using var t = await Live.StartAsync();
        t.Consumer!.Behavior = async (_, n, ct) =>
        {
            if (n != 1)
                return;
            consumerToken = ct;
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        try
        {
            var s = await t.OpenSessionAsync();
            var mark = t.Clock.Mark();
            await s.SendTextAsync(Utf8("slow "), endOfMessage: false);
            await t.Clock.WaitForTimerAsync(mark, Limit, Guard);
            await s.SendContinuationAsync(Utf8("one"), endOfMessage: true);
            await entered.Task.WaitAsync(Guard);

            // The consumer holds the message; its timer stopped before the consumer was called.
            t.Clock.ScheduledTimersSince(mark).Should().Be(0, "no timer runs while the consumer works");
            t.Clock.Advance(Limit * 3);
            t.Clock.TimersFired.Should().Be(0);
            consumerToken.IsCancellationRequested.Should().BeFalse("the consumer is not cancelled");

            release.TrySetResult();
            (await s.PingAsync(Guard)).Should().BeEmpty("the reader went on once the consumer returned, with no close");
            await s.SendTextAsync(Utf8("next"), endOfMessage: true);
            (await WaitUntilAsync(() => t.Consumer.Calls == 2)).Should().BeTrue();
            t.Consumer.Frames.Select(f => Text(f.Payload)).Should().Equal("slow one", "next");
            t.Transport.ActiveSessionSocketCount.Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task With_ReceiveInbound_off_a_stalled_push_is_still_closed_with_1008()
    {
        await using var t = await Live.StartAsync(receiveInbound: false);
        var s = await t.OpenSessionAsync();
        var mark = t.Clock.Mark();
        await s.SendTextAsync(new byte[500], endOfMessage: false);
        await t.Clock.WaitForTimerAsync(mark, Limit, Guard);

        t.Clock.Advance(Limit);
        t.Clock.TimersFired.Should().Be(1);
        (await NextCloseAsync(s)).CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
    }

    [Fact]
    public async Task An_infinite_limit_starts_no_timer_and_never_closes_a_stalled_push()
    {
        await using var t = await Live.StartAsync(configure: o => o.MessageReceiveTimeout = Timeout.InfiniteTimeSpan);
        var s = await t.OpenSessionAsync();
        var mark = t.Clock.Mark();
        await s.SendTextAsync(Utf8("first "), endOfMessage: false);
        (await s.PingAsync(Guard)).Should().BeEmpty("the reader has the first part and waits for the rest");

        t.Clock.Mark().Should().Be(mark, "no limit, no timer");
        t.Clock.Advance(TimeSpan.FromDays(365));
        t.Clock.TimersFired.Should().Be(0);
        (await s.PingAsync(Guard)).Should().BeEmpty("never closed");

        await s.SendContinuationAsync(Utf8("and last"), endOfMessage: true);
        (await WaitUntilAsync(() => t.Consumer!.Calls == 1)).Should().BeTrue();
        Text(t.Consumer!.Frames.Single().Payload).Should().Be("first and last");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_push_in_progress_when_dispose_takes_the_socket_is_no_longer_kept_or_timed_and_is_dropped(bool anotherPartArrives)
    {
        // #100 OD-7: dispose switches the session socket to discarding before its close frame. A
        // message whose first part was read before that is dropped whole: the next part read stops
        // its timer and nothing more of it is kept; and if no part comes, its time running out during
        // the close wait closes nothing (dispose's close is the socket's close) and logs nothing.
        var t = await Live.StartAsync();
        try
        {
            var s = await t.OpenSessionAsync();
            var mark = t.Clock.Mark();
            await s.SendTextAsync(new byte[100], endOfMessage: false);
            await t.Clock.WaitForTimerAsync(mark, Limit, Guard);

            var dispose = t.Transport.DisposeAsync().AsTask();
            (await NextCloseAsync(s)).CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
            if (anotherPartArrives)
            {
                await s.SendContinuationAsync(Utf8("more"), endOfMessage: false);
                // The pong comes from the reader's next receive, so the part was handled before it.
                (await s.PingAsync(Guard)).Should().BeEmpty();
                t.Clock.ScheduledTimersSince(mark).Should().Be(0, "the part read once dispose took the socket stopped the message's timer");
            }
            else
            {
                t.Clock.Advance(Limit);
                t.Clock.TimersFired.Should().Be(1, "the message's time ran out during the close wait");
            }
            await s.SendContinuationAsync(Utf8("last"), endOfMessage: true);
            await s.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
            s.End();
            await dispose.WaitAsync(Guard);

            t.Consumer!.Calls.Should().Be(0, "nothing reaches the consumer once dispose took the socket");
            t.Transport.InboundMessagesDropped.Should().Be(1, "the message was read to its end and dropped whole");
            t.Clock.ScheduledTimersSince(mark).Should().Be(0);
            t.Logs.Records.Should().NotContain(r => r.Text.Contains(TimeoutLine, StringComparison.Ordinal),
                "no 1008: a socket dispose is closing is not closed for a message's time");
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("the peer closes")]
    [InlineData("the transport is disposed")]
    public async Task A_socket_that_ends_mid_message_stops_the_messages_timer(string end)
    {
        var t = await Live.StartAsync();
        try
        {
            var s = await t.OpenSessionAsync();
            var mark = t.Clock.Mark();
            await s.SendTextAsync(new byte[100], endOfMessage: false);
            await t.Clock.WaitForTimerAsync(mark, Limit, Guard);

            if (end == "the peer closes")
            {
                await s.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                s.End();
            }
            else
            {
                // Dispose sends its close and waits (bounded) for the peer's (#100 OD-7): the peer
                // answers it, so the socket ends by dispose's close handshake while the loop is
                // still inside the message.
                var dispose = t.Transport.DisposeAsync().AsTask();
                (await NextCloseAsync(s)).CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
                await s.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                s.End();
                await dispose.WaitAsync(Guard);
            }

            (await WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0)).Should().BeTrue();
            t.Clock.ScheduledTimersSince(mark).Should().Be(0, "the message's timer stops when its socket ends ({0})", end);
            t.Clock.TimersFired.Should().Be(0);
            t.Consumer!.Calls.Should().Be(0);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    // ---- scripted socket: exact interleavings --------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_part_already_waiting_when_the_time_runs_out_cuts_the_message_unless_it_is_the_last(bool last)
    {
        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock);
        var socket = await t.OpenSessionAsync();
        var mark = clock.Mark();
        var firedAtSecondReceive = -1;
        // The reader's second receive starts after the first part started the timer. The time runs out
        // just then, while the next part is already waiting, so that receive completes at once.
        socket.BeforeReceive = n =>
        {
            if (n == 2 && clock.ScheduledTimersSince(mark) == 1)
            {
                clock.Advance(Limit);
                firedAtSecondReceive = clock.TimersFired;
            }
        };
        socket.PushPart(Utf8("first;"), endOfMessage: false);
        if (last)
        {
            socket.PushPart(Utf8("last"), endOfMessage: true);
        }
        else
        {
            // A peer whose parts are always waiting (here empty ones) never leaves a receive outstanding.
            for (var i = 0; i < 50; i++)
                socket.PushPart(Array.Empty<byte>(), endOfMessage: false);
            socket.PushPart(Utf8("last"), endOfMessage: true);
        }

        if (last)
        {
            // The last part was there when the time ran out: the message arrived in time.
            (await WaitUntilAsync(() => t.Consumer.Calls == 1)).Should().BeTrue();
            firedAtSecondReceive.Should().Be(1);
            Text(t.Consumer.Frames.Single().Payload).Should().Be("first;last");
            socket.CloseOutputStatus.Should().BeNull();
        }
        else
        {
            (await WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 1)).Should().BeTrue();
            firedAtSecondReceive.Should().Be(1);
            socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation, "a later part that is not the last is late all the same");
            t.Consumer.Calls.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_clock_that_cannot_time_a_message_ends_the_session_socket()
    {
        await using var t = Scripted.Create(new FailingTimeProvider());
        var socket = await t.OpenSessionAsync();

        socket.PushPart(Utf8("first"), endOfMessage: false);

        (await WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0)).Should().BeTrue("the socket ends rather than read on without a limit");
        socket.Disposed.Should().BeTrue();
        t.Transport.ActiveSessionSocketCount.Should().Be(0);
        t.Consumer.Calls.Should().Be(0);
        t.Logs.Records.Should().Contain(r => r.Text.Contains("stopped reading (InvalidOperationException)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_message_timed_out_on_a_retiring_socket_is_dropped_without_a_second_close()
    {
        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock);
        var socket = await t.OpenSessionAsync();
        await t.Transport.SendAsync(Request(ScriptedEndpoint, "none", ReturnRouteMode.None), default).WaitAsync(Guard);
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the none retired S: its close is sent");
        t.Transport.LiveSessionSocketCount.Should().Be(1, "a retiring socket is still read");

        var mark = clock.Mark();
        socket.PushPart(Utf8("late "), endOfMessage: false);
        await clock.WaitForTimerAsync(mark, Limit, Guard);
        clock.Advance(Limit);
        clock.TimersFired.Should().Be(1);
        (await WaitUntilAsync(() => t.Logs.Records.Any(r => r.Text.Contains(TimeoutLine, StringComparison.Ordinal)))).Should().BeTrue();

        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.NormalClosure, "the retire's close was already sent; no second close");
        socket.CloseOutputDescription.Should().Be("return-route session ended");
        t.Logs.Records.Should().NotContain(r => r.Text.Contains("closing a timed-out", StringComparison.Ordinal), "no close was attempted in CloseSent");

        socket.PushPart(Utf8("rest"), endOfMessage: true);
        socket.PushMessage(Utf8("after"));
        (await WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 2)).Should().BeTrue();
        t.Consumer.Calls.Should().Be(0);
        socket.PushClose();
        (await WaitUntilAsync(() => t.Transport.LiveSessionSocketCount == 0)).Should().BeTrue();
    }

    [Fact]
    public async Task An_oversized_push_stops_its_timer_and_is_closed_once_with_1009()
    {
        const int cap = 16 * 1024;
        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock, maxReceiveBytes: cap);
        var socket = await t.OpenSessionAsync();
        var mark = clock.Mark();
        socket.PushPart(new byte[1000], endOfMessage: false);
        await clock.WaitForTimerAsync(mark, Limit, Guard);

        socket.PushPart(new byte[cap], endOfMessage: false);
        (await WaitUntilAsync(() => t.Logs.Records.Any(r => r.Text.Contains("exceeded", StringComparison.Ordinal)))).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        clock.ScheduledTimersSince(mark).Should().Be(0, "a discarding socket has no message timer: its backstop bounds it");

        clock.Advance(Limit);
        clock.TimersFired.Should().Be(0);
        socket.PushPart(Utf8("end"), endOfMessage: true);
        (await WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 1)).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        t.Logs.Records.Should().NotContain(r => r.Text.Contains(TimeoutLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timed_out_push_that_then_exceeds_the_cap_is_not_closed_again()
    {
        const int cap = 16 * 1024;
        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock, maxReceiveBytes: cap);
        var socket = await t.OpenSessionAsync();
        var mark = clock.Mark();
        socket.PushPart(new byte[1000], endOfMessage: false);
        await clock.WaitForTimerAsync(mark, Limit, Guard);
        clock.Advance(Limit);
        clock.TimersFired.Should().Be(1);
        (await WaitUntilAsync(() => t.Logs.Records.Any(r => r.Text.Contains(TimeoutLine, StringComparison.Ordinal)))).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);

        socket.PushPart(new byte[cap * 2], endOfMessage: true);
        (await WaitUntilAsync(() => t.Transport.InboundMessagesDropped == 1)).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        t.Logs.Records.Should().NotContain(r => r.Text.Contains("exceeded", StringComparison.Ordinal), "a discarding socket is not closed a second time");
    }

    // ---- the setting ----------------------------------------------------------------------------

    [Fact]
    public async Task MessageReceiveTimeout_defaults_to_60_seconds_and_the_transport_uses_it()
    {
        new WebSocketTransportOptions().MessageReceiveTimeout.Should().Be(TimeSpan.FromSeconds(60));

        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock, defaultLimit: true);
        var socket = await t.OpenSessionAsync();
        var mark = clock.Mark();
        socket.PushPart(Utf8("first"), endOfMessage: false);
        await clock.WaitForTimerAsync(mark, TimeSpan.FromSeconds(60), Guard);
        clock.Advance(TimeSpan.FromSeconds(60));
        clock.TimersFired.Should().Be(1);
        (await WaitUntilAsync(() => socket.CloseOutputStatus is not null)).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    [Fact]
    public async Task MessageReceiveTimeout_is_read_once_when_the_transport_is_created()
    {
        var clock = new QaManualTimeProvider();
        await using var t = Scripted.Create(clock);
        t.Options.MessageReceiveTimeout = TimeSpan.FromSeconds(1); // after construction: no effect
        var socket = await t.OpenSessionAsync();
        var mark = clock.Mark();
        socket.PushPart(Utf8("first"), endOfMessage: false);

        await clock.WaitForTimerAsync(mark, Limit, Guard);
        clock.Advance(TimeSpan.FromSeconds(1));
        clock.TimersFired.Should().Be(0, "the value changed after construction does not apply");
        clock.Advance(Limit - TimeSpan.FromSeconds(1));
        clock.TimersFired.Should().Be(1);
        (await WaitUntilAsync(() => socket.CloseOutputStatus is not null)).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    public static TheoryData<string> InvalidLimitCases() => new(new[] { "zero", "minus one tick", "minus two milliseconds", "minimum", "one tick over Int32.MaxValue ms", "maximum" });

    [Theory]
    [MemberData(nameof(InvalidLimitCases))]
    public void An_invalid_MessageReceiveTimeout_is_refused_by_the_setter(string @case)
    {
        var value = @case switch
        {
            "zero" => TimeSpan.Zero,
            "minus one tick" => TimeSpan.FromTicks(-1),
            "minus two milliseconds" => TimeSpan.FromMilliseconds(-2),
            "minimum" => TimeSpan.MinValue,
            "one tick over Int32.MaxValue ms" => TimeSpan.FromMilliseconds(int.MaxValue) + Tick,
            "maximum" => TimeSpan.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        var options = new WebSocketTransportOptions { MessageReceiveTimeout = Limit };

        var set = () => options.MessageReceiveTimeout = value;

        set.Should().Throw<ArgumentOutOfRangeException>(@case)
            .Which.ParamName.Should().Be(nameof(WebSocketTransportOptions.MessageReceiveTimeout));
        options.MessageReceiveTimeout.Should().Be(Limit, "a refused value leaves the setting as it was");
    }

    public static TheoryData<string> ValidLimitCases() => new(new[] { "one tick", "Int32.MaxValue ms", "infinite", "thirty seconds" });

    [Theory]
    [MemberData(nameof(ValidLimitCases))]
    public void A_valid_MessageReceiveTimeout_is_kept(string @case)
    {
        var value = @case switch
        {
            "one tick" => Tick,
            "Int32.MaxValue ms" => TimeSpan.FromMilliseconds(int.MaxValue),
            "infinite" => Timeout.InfiniteTimeSpan,
            "thirty seconds" => TimeSpan.FromSeconds(30),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        var options = new WebSocketTransportOptions { MessageReceiveTimeout = value };

        options.MessageReceiveTimeout.Should().Be(value);
    }

    [Fact]
    public async Task UseWebSocketTransport_times_session_messages_on_the_containers_clock()
    {
        var clock = new QaManualTimeProvider();
        var sockets = new ScriptedSocketFactory { OnCreate = s => s.AnswerCloseOutput = false };
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws" };
                o.MaxReconnectAttempts = 0;
                o.WebSocketFactory = sockets.Create;
                o.Connect = (_, _, _) => Task.CompletedTask;
                o.MessageReceiveTimeout = Limit;
            });
        });
        await using var provider = services.BuildServiceProvider();
        var transport = provider.GetRequiredService<WebSocketDidCommTransport>();
        await transport.SendAsync(Request(ScriptedEndpoint, "open", ReturnRouteMode.All), default).WaitAsync(Guard);
        var socket = sockets.Sockets.Single();

        var mark = clock.Mark();
        socket.PushPart(Utf8("first"), endOfMessage: false);
        await clock.WaitForTimerAsync(mark, Limit, Guard);
        clock.Advance(Limit);
        clock.TimersFired.Should().Be(1);

        (await WaitUntilAsync(() => socket.CloseOutputStatus is not null)).Should().BeTrue();
        socket.CloseOutputStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static TransportRequest Request(Uri endpoint, string text, ReturnRouteMode? mode = null)
        => new(endpoint, Utf8(text), MediaTypes.Encrypted) { ReturnRoute = mode, LocalDid = Alice, PeerDid = Bob };

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    private static Task<bool> WaitUntilAsync(Func<bool> condition) => ReturnRouteTestHost.WaitUntilAsync(condition, Guard);

    /// <summary>The client's next frame, which must be a close frame.</summary>
    private static async Task<ClientFrame> NextCloseAsync(RawFrameConnection connection)
    {
        var frame = await connection.NextFrameAsync(Guard);
        frame.Should().NotBeNull("the client sends a close frame before ending the connection");
        frame!.IsClose.Should().BeTrue();
        return frame;
    }

    private static ILogger<WebSocketDidCommTransport> LoggerFor(CapturingLoggerProvider logs)
        => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)).CreateLogger<WebSocketDidCommTransport>();

    /// <summary>A transport whose session sockets are real <see cref="ClientWebSocket"/>s to a <see cref="RawFrameWebSocketPeer"/>, timed on a hand-moved clock.</summary>
    private sealed class Live : IAsyncDisposable
    {
        private readonly ConcurrentQueue<WebSocketLifecycleEventKind> _events = new();

        private Live(RawFrameWebSocketPeer peer, bool receiveInbound, Action<WebSocketTransportOptions>? configure)
        {
            Peer = peer;
            Consumer = receiveInbound ? new RecordingFrameConsumer() : null;
            var options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = receiveInbound,
                MessageReceiveTimeout = Limit,
                // Far beyond any test step, so no real-time bound (the close grace, the backstop) acts
                // unless a test shortens it.
                SendTimeout = TimeSpan.FromMinutes(2),
                // A custom Connect skips the private-address guard (the peer is on loopback); the socket
                // is the transport's default, a real ClientWebSocket.
                Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct),
            };
            configure?.Invoke(options);
            Transport = new WebSocketDidCommTransport(Options.Create(options), LoggerFor(Logs), coreOptions: null, Consumer, Clock);
            Transport.Lifecycle += (_, e) => _events.Enqueue(e.Kind);
        }

        public RawFrameWebSocketPeer Peer { get; }

        public QaManualTimeProvider Clock { get; } = new();

        public RecordingFrameConsumer? Consumer { get; }

        public CapturingLoggerProvider Logs { get; } = new();

        public WebSocketDidCommTransport Transport { get; }

        public IReadOnlyList<WebSocketLifecycleEventKind> Events => _events.ToArray();

        public static async Task<Live> StartAsync(bool receiveInbound = true, Action<WebSocketTransportOptions>? configure = null)
            => new(await RawFrameWebSocketPeer.StartAsync(), receiveInbound, configure);

        /// <summary>
        /// Open the session socket S with an <c>all</c> send, read that message at the peer, and return
        /// once the client's reader waits for the first push (its pong comes from inside that wait).
        /// </summary>
        public async Task<RawFrameConnection> OpenSessionAsync()
        {
            await Transport.SendAsync(Request(Peer.Endpoint, "open", ReturnRouteMode.All), default).WaitAsync(Guard);
            var connection = await Peer.NextConnectionAsync(Guard);
            Text(await connection.NextMessageAsync(Guard)).Should().Be("open");
            Transport.ActiveSessionSocketCount.Should().Be(1, "the send opened a session socket");
            (await connection.PingAsync(Guard)).Should().BeEmpty();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            // The peer first: it ends every connection. The transport's dispose waits (up to one
            // SendTimeout, 2 minutes here) for a peer that is still connected to answer its close
            // (#100 OD-7), and this peer never answers one on its own.
            await Peer.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>A transport over <see cref="ScriptedSessionWebSocket"/>s (peers that never answer a close), timed on <paramref name="clock"/>.</summary>
    private sealed class Scripted : IAsyncDisposable
    {
        private Scripted(TimeProvider clock, int maxReceiveBytes, bool defaultLimit)
        {
            Sockets = new ScriptedSocketFactory { OnCreate = s => s.AnswerCloseOutput = false };
            Options = new WebSocketTransportOptions
            {
                AllowedSchemes = new[] { "ws" },
                MaxReconnectAttempts = 0,
                ReceiveInbound = true,
                SendTimeout = TimeSpan.FromMinutes(2),
                WebSocketFactory = Sockets.Create,
                Connect = (_, _, _) => Task.CompletedTask,
            };
            if (!defaultLimit)
                Options.MessageReceiveTimeout = Limit;
            Transport = new WebSocketDidCommTransport(
                Microsoft.Extensions.Options.Options.Create(Options),
                LoggerFor(Logs),
                Microsoft.Extensions.Options.Options.Create(new DidCommOptions { MaxReceiveBytes = maxReceiveBytes }),
                Consumer,
                clock);
        }

        public WebSocketTransportOptions Options { get; }

        public ScriptedSocketFactory Sockets { get; }

        public RecordingFrameConsumer Consumer { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public WebSocketDidCommTransport Transport { get; }

        public static Scripted Create(TimeProvider clock, int maxReceiveBytes = 256 * 1024, bool defaultLimit = false)
            => new(clock, maxReceiveBytes, defaultLimit);

        /// <summary>Open the session socket S with an <c>all</c> send and return its scripted socket.</summary>
        public async Task<ScriptedSessionWebSocket> OpenSessionAsync()
        {
            await Transport.SendAsync(Request(ScriptedEndpoint, "open", ReturnRouteMode.All), default).WaitAsync(Guard);
            Transport.ActiveSessionSocketCount.Should().Be(1, "the send opened a session socket");
            return Sockets.Sockets.Single();
        }

        public ValueTask DisposeAsync()
        {
            // The peers never answer a close on their own; dispose waits for a session socket's.
            Sockets.AnswerClosesFromNowOn();
            return Transport.DisposeAsync();
        }
    }

    /// <summary>A host clock whose timers cannot be created.</summary>
    private sealed class FailingTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("this clock creates no timers");
    }
}
