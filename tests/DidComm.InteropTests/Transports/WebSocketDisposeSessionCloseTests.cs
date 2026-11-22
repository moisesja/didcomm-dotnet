using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Xunit;
using Kind = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Kind;
using Live = DidComm.InteropTests.Transports.WebSocketDisposeCloseWaitTests.Live;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: dispose's close of a return-route session socket uses the session's own receive loop
/// as the socket's only reader. Dispose sends its close frame and waits (bounded by the close budget)
/// for the loop to read the peer's close. From the moment dispose takes the socket, every message the
/// peer pushes on it is read whole by that loop and dropped: nothing more reaches the inbound
/// consumer. Real Kestrel peer writing raw frames, real <see cref="ClientWebSocket"/> in the
/// transport. Every push in these tests is sent only once the peer holds dispose's close frame (the
/// gate), so it arrives during the close wait.
/// </summary>
public sealed class WebSocketDisposeSessionCloseTests
{
    private static readonly TimeSpan Guard = WebSocketDisposeCloseWaitTests.Guard;

    [Fact]
    public async Task During_the_close_wait_the_consumer_never_receives_a_message_that_differs_from_what_the_peer_sent()
    {
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        // Three large whole messages, then the answering close.
        var sent = Pushes(count: 3, size: 20_000);
        foreach (var message in sent)
            await peer.SendTextAsync(message, endOfMessage: true);
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();
        await dispose.WaitAsync(Guard);

        foreach (var frame in t.Consumer.Frames)
        {
            sent.Any(m => m.AsSpan().SequenceEqual(frame.Payload.Span)).Should().BeTrue(
                $"the consumer only ever gets a message exactly as the peer sent it (got {frame.Payload.Length} bytes)");
        }
    }

    [Fact]
    public async Task During_the_close_wait_the_sessions_loop_is_the_only_reader_and_drops_every_push_whole()
    {
        // A flood: three large messages and 200 small ones, as fast as the peer can write them, then
        // the answering close. The loop reads every one of them to its end and drops it, and dispose
        // ends when the loop reads the close.
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);
        var dropped = t.Transport.InboundMessagesDropped;

        var dispose = t.Transport.DisposeAsync().AsTask();
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        var sent = Pushes(count: 3, size: 20_000).Concat(Pushes(count: 200, size: 1_000)).ToList();
        foreach (var message in sent)
            await peer.SendTextAsync(message, endOfMessage: true);
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10)); // the budget is 30 s: it ended at the close

        t.Consumer.Calls.Should().Be(0, "nothing reaches the consumer once dispose took the socket");
        (t.Transport.InboundMessagesDropped - dropped).Should().Be(sent.Count, "the loop read each push whole, and dropped it");
        var socket = t.Sockets.Single();
        socket.State.Should().Be(WebSocketState.Closed);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        socket.CloseStatusDescription.Should().Be("bye", "the loop read the peer's close");
        t.ExceptionLines().Should().BeEmpty();
        t.Events.Count(e => e == DidComm.Transports.WebSocket.WebSocketLifecycleEventKind.Disconnected).Should().Be(1);
    }

    [Fact]
    public async Task A_consumer_call_in_progress_when_dispose_begins_has_its_token_cancelled_and_does_not_hold_the_close()
    {
        // The default SendTimeout (a 30 s budget). The consumer is inside a push and waits until it is
        // cancelled. Nothing is in flight, so every drain is over at once, and dispose then cancels
        // the consumer, before it closes the socket (the loop's own receives go on): the loop gets
        // back to reading, reads the peer's close, and dispose returns at that close, not at the
        // budget. (A reply the consumer is sending is drained first: WebSocketDisposeConsumerReplyTests.)
        await using var t = await Live.StartAsync();
        var peer = await t.OpenAsync(Kind.Session);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Consumer.Behavior = async (_, _, ct) =>
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            inside.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        await peer.SendTextAsync(Encoding.UTF8.GetBytes("in progress"), endOfMessage: true);
        await inside.Task.WaitAsync(Guard);

        var dispose = t.Transport.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(Guard);
        await WebSocketDisposeCloseWaitTests.NextCloseAsync(peer);
        await peer.SendCloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        peer.End();

        await dispose.WaitAsync(TimeSpan.FromSeconds(10)); // a third of the budget: the consumer did not hold it
        t.Consumer.Calls.Should().Be(1);
        t.Sockets.Single().CloseStatusDescription.Should().Be("bye", "the loop, back from the consumer, read the peer's close");
        t.ExceptionLines().Should().BeEmpty("a consumer that ends with its cancellation is not a fault");
    }

    private static List<byte[]> Pushes(int count, int size)
        => Enumerable.Range(0, count)
            .Select(i => Encoding.UTF8.GetBytes($"push-{size}-{i}:" + new string((char)('a' + i % 26), size) + $":end-{i}"))
            .ToList();
}
