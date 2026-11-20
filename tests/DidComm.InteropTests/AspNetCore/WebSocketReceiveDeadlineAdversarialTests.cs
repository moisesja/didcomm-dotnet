using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// Hostile peers against the WebSocket receive time limit on real Kestrel, on every endpoint that
/// applies it: peers that try to keep a connection (and what it holds) past the limit by stalling the
/// close handshake rather than the message.
/// </summary>
/// <remarks>
/// The receive limit runs on the container's manual clock; the close grace is the product's own real
/// time (5 s), so each test takes about that long. The 30 s waits are hang guards on an event that is
/// certain with a correct product.
/// </remarks>
[Collection(QaSerialTiming.Name)]
public sealed class WebSocketReceiveDeadlineAdversarialTests
{
    private static readonly TimeSpan EndGuard = TimeSpan.FromSeconds(30);

    public static TheoryData<DeadlineEndpoint> Endpoints => new()
    {
        DeadlineEndpoint.Dispatch,
        DeadlineEndpoint.Callback,
        DeadlineEndpoint.Mediator,
    };

    /// <summary>
    /// The peer floods pings and never reads, so the server's pongs fill the connection and its
    /// receive stalls inside a pong it cannot send: the 1008 close then cannot be written either. The
    /// connection must still end within the close grace once the first message's time is up.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_peer_that_floods_pings_and_never_reads_is_still_dropped_when_its_first_message_runs_out_of_time(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var mark = host.Time.Mark();
        await using var peer = await NonReadingPeer.ConnectAsync(host.Port, DeadlineKestrelHost.PathOf(endpoint));
        await host.Time.WaitForTimerAsync(mark, DeadlineKestrelHost.Limit, DeadlineKestrelHost.Guard);

        // Pings until a write no longer completes: both directions of the connection are full, so
        // the server's receive is parked in a pong send.
        (await peer.FloodPingsUntilBlockedAsync(TimeSpan.FromSeconds(2), EndGuard)).Should().BeTrue("precondition: the connection fills up");

        var fired = host.Time.TimersFired;
        host.Time.Advance(DeadlineKestrelHost.Limit);
        host.Time.TimersFired.Should().Be(fired + 1, "the first message's deadline fired");

        // The close frame cannot be sent (the send side is stuck behind a pong the peer never reads):
        // the server gives up on it after the grace and aborts the connection.
        (await ReturnRouteTestHost.WaitUntilAsync(
                () => host.Logs.Records.Any(r => r.Text.Contains("close failed with", StringComparison.Ordinal)),
                EndGuard))
            .Should().BeTrue("the close the server could not send is given up at the grace");
        host.TimeoutLines().Should().ContainSingle();
        await peer.ReadToEndAsync(EndGuard);
    }

    /// <summary>
    /// The peer answers the 1008 close with data frames, forever, instead of a close frame: the server
    /// reads and drops them only until the close grace, then aborts.
    /// </summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_peer_that_answers_the_1008_close_with_data_frames_forever_is_dropped_at_the_close_grace(DeadlineEndpoint endpoint)
    {
        await using var host = await DeadlineKestrelHost.StartAsync();
        var mark = host.Time.Mark();
        await using var peer = await host.ConnectRawAsync(endpoint);
        await host.Time.WaitForTimerAsync(mark, DeadlineKestrelHost.Limit, DeadlineKestrelHost.Guard);
        var fired = host.Time.TimersFired;
        host.Time.Advance(DeadlineKestrelHost.Limit);
        host.Time.TimersFired.Should().Be(fired + 1);

        var close = await peer.ReadCloseAsync(DeadlineKestrelHost.Guard);
        close.Should().NotBeNull();
        close!.CloseCode.Should().Be((int)WebSocketCloseStatus.PolicyViolation);

        using var stop = new CancellationTokenSource();
        var chatter = Task.Run(async () =>
        {
            var payload = Encoding.ASCII.GetBytes(new string('x', 512));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await peer.SendFrameAsync(RawFrame.Text, fin: true, payload);
                    await Task.Delay(20);
                }
                catch (Exception)
                {
                    return; // the server dropped the connection
                }
            }
        });

        var ended = peer.ReadToEndAsync(EndGuard);
        var first = await Task.WhenAny(ended, Task.Delay(EndGuard));
        stop.Cancel();
        await chatter;
        first.Should().BeSameAs(ended, "the drain after the close stops at the grace, however much the peer keeps sending");
        host.DispatchedCount(endpoint).Should().Be(0, "nothing read after the close is processed");
    }

    /// <summary>A raw WebSocket client that writes masked frames and never reads what the server sends.</summary>
    private sealed class NonReadingPeer : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private Task? _pending;

        private NonReadingPeer(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        public static async Task<NonReadingPeer> ConnectAsync(int port, string path)
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, ReceiveBufferSize = 4096 };
            using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard);
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var peer = new NonReadingPeer(tcp);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            await peer._stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            // Read the 101 head byte by byte (nothing after it is ever read until the end).
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await peer._stream.ReadAsync(one, cts.Token) == 0)
                    break;
                head.Add(one[0]);
            }
            Encoding.ASCII.GetString(head.ToArray()).Should().StartWith("HTTP/1.1 101");
            return peer;
        }

        /// <summary>Write batches of pings until one batch does not complete within <paramref name="stall"/>; <c>false</c> if that never happens within <paramref name="guard"/>.</summary>
        public async Task<bool> FloodPingsUntilBlockedAsync(TimeSpan stall, TimeSpan guard)
        {
            var batch = new List<byte>();
            var payload = new byte[125];
            for (var i = 0; i < 1000; i++)
                batch.AddRange(Frame(0x9, payload));
            var bytes = batch.ToArray();
            var deadline = DateTime.UtcNow + guard;
            while (DateTime.UtcNow < deadline)
            {
                var write = _stream.WriteAsync(bytes).AsTask();
                if (await Task.WhenAny(write, Task.Delay(stall)) != write)
                {
                    _pending = write; // left blocked; it fails once the server drops the connection
                    _ = write.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    return true;
                }
                await write;
            }
            return false;
        }

        /// <summary>Read and drop everything until the server ends the TCP connection.</summary>
        public async Task ReadToEndAsync(TimeSpan guard)
        {
            using var cts = new CancellationTokenSource(guard);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(buffer, cts.Token);
                }
                catch (IOException)
                {
                    return;
                }
                if (read == 0)
                    return;
            }
        }

        private static byte[] Frame(int opcode, byte[] payload)
        {
            var mask = RandomNumberGenerator.GetBytes(4);
            var frame = new byte[2 + 4 + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)(0x80 | payload.Length);
            mask.CopyTo(frame, 2);
            for (var i = 0; i < payload.Length; i++)
                frame[6 + i] = (byte)(payload[i] ^ mask[i % 4]);
            return frame;
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
