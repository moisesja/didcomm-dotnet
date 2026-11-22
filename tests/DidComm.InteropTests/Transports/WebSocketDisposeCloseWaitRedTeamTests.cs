using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using DidComm.Transports.WebSocket;
using Xunit;
using Kind = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Kind;
using Live = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Live;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7 red team: what a return-route session socket hands the inbound consumer, and how long
/// dispose takes, while dispose waits for the peer's close. The session close uses the session's own
/// loop as its only reader, and nothing reaches the consumer once dispose took the socket.
/// </summary>
public sealed class WebSocketDisposeCloseWaitRedTeamTests
{
    private static readonly TimeSpan Guard = WebSocketDisposeCloseWaitTests.Guard;

    [Fact]
    public async Task During_the_close_wait_a_session_socket_never_hands_the_consumer_a_message_the_peer_did_not_send()
    {
        // The default SendTimeout (30 s budget): the peer answers well inside it.
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);

        // An honest peer that had pushes in flight when it saw the close: three large whole messages,
        // then its answering close.
        // (A client that drops the connection after its close frame, as before OD-7, makes these
        // writes fail: then nothing reaches the consumer, which is fine.)
        var sent = new List<byte[]>();
        for (var i = 0; i < 3; i++)
            sent.Add(Encoding.UTF8.GetBytes($"push-{i}:" + new string((char)('a' + i), 20_000) + $":end-{i}"));
        await PeerMayBeGoneAsync(async () =>
        {
            foreach (var message in sent)
                await peer.SendTextAsync(message, endOfMessage: true);
            await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        });
        peer.End();
        await dispose.WaitAsync(Guard);

        foreach (var frame in t.Consumer.Frames)
        {
            var payload = frame.Payload.ToArray();
            var whole = sent.Any(m => m.AsSpan().SequenceEqual(payload));
            whole.Should().BeTrue(
                $"the consumer must only ever see a message the peer sent, whole (got {payload.Length} bytes, " +
                $"starting '{Encoding.UTF8.GetString(payload, 0, Math.Min(12, payload.Length))}', ending " +
                $"'{Encoding.UTF8.GetString(payload, Math.Max(0, payload.Length - 8), Math.Min(8, payload.Length))}'; " +
                $"the peer sent {string.Join(", ", sent.Select(m => m.Length))} bytes)");
        }
        t.Consumer.Calls.Should().Be(0, "once dispose began the consumer gets no message at all: every push is dropped whole");
    }

    [Fact]
    public async Task Once_dispose_began_a_session_sockets_consumer_is_handed_no_new_message_with_a_live_token()
    {
        // The loop goes on reading for the whole close wait (up to one SendTimeout), but a message it
        // reads once dispose took the socket is dropped: the transport is being disposed (and, in a
        // container, so are the services the consumer calls).
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);
        var tokens = new List<bool>();
        t.Consumer.Behavior = (_, _, ct) =>
        {
            lock (tokens)
                tokens.Add(ct.IsCancellationRequested);
            return ValueTask.CompletedTask;
        };

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        var calls = 0;
        await PeerMayBeGoneAsync(async () =>
        {
            await peer.SendTextAsync(Encoding.UTF8.GetBytes("after dispose began"), endOfMessage: true);
            await peer.PingAsync(Guard); // the client's readers have handled it
        });
        calls = t.Consumer.Calls;
        await PeerMayBeGoneAsync(() => peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye"));
        peer.End();
        await dispose.WaitAsync(Guard);

        calls.Should().Be(0, "a message read after DisposeAsync began is not handed to the consumer as live (it was handed over {0} time(s), token cancelled: {1})",
            calls, string.Join(",", tokens));
    }

    // The connection may already be gone (a client that does not wait for the close): the peer's
    // writes and its ping then fail, and nothing it sent can have reached the consumer.
    private static async Task PeerMayBeGoneAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or EndOfStreamException or InvalidOperationException)
        {
        }
    }

    [Fact]
    public async Task A_peer_that_streams_data_and_pings_and_never_closes_holds_dispose_one_budget_and_no_more()
    {
        // Slowloris on the close: the peer keeps the socket busy (data, then pings, a frame every few
        // milliseconds) and never answers. Dispose still ends at about one budget, cuts it off, and
        // reports it once.
        await using var t = await Live.StartAsync(o => o.SendTimeout = WebSocketDisposeCloseWaitTests.ShortBudget);
        var peer = await t.OpenAsync(Kind.Session);
        var pooled = await t.OpenAsync(Kind.Pooled, query: "?pooled=1");

        var clock = Stopwatch.StartNew();
        var dispose = t.Transport.DisposeAsync().AsTask();
        using var stop = new CancellationTokenSource();
        async Task FloodAsync(RawFrameConnection c)
        {
            var chunk = new byte[1024];
            var n = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (n++ % 2 == 0)
                        await c.SendTextAsync(chunk, endOfMessage: false); // a message that never ends
                    else
                        await c.SendPingAsync(new byte[] { 1 });
                    await Task.Delay(5);
                    if (n % 2 == 1)
                        await c.SendContinuationAsync(chunk, endOfMessage: false);
                }
            }
            catch
            {
                // the client cut the connection off
            }
        }
        var floods = new[] { FloodAsync(peer), FloodAsync(pooled) };

        await dispose.WaitAsync(Guard);
        clock.Stop();
        stop.Cancel();
        await Task.WhenAll(floods).WaitAsync(Guard);

        clock.Elapsed.Should().BeLessThan(WebSocketDisposeCloseWaitTests.ShortBudget * 2 + TimeSpan.FromSeconds(6),
            "DrainBudget + 2 x CloseBudget at most, however busy the peer keeps the socket");
        t.Events.Count(e => e == WebSocketLifecycleEventKind.Disconnected).Should().Be(2);
        t.Transport.LiveSessionSocketCount.Should().Be(0);
        t.Sockets.Should().OnlyContain(s => s.State == WebSocketState.Aborted || s.State == WebSocketState.Closed);
    }
}
