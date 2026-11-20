using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.Transports;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// What a stranger's broken WebSocket connection writes to the host's logs on real Kestrel: a frame
/// the managed socket refuses (a reserved opcode), or a connection dropped without a close
/// handshake. Neither is a fault of the application, and any peer can cause one per connection, so
/// neither may surface as an "unhandled exception" at Error.
/// </summary>
/// <remarks>
/// These tests show a defect present before the receive-deadline change as well: the receive's
/// <c>WebSocketException</c> escapes both receive loops of <c>MapDidCommWebSocket</c>, and the
/// server logs it at Error with its stack trace, once per connection.
/// </remarks>
public sealed class WebSocketPeerFailureLogTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public static TheoryData<string> Paths => new() { "/ws", "/ws-callback" };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_reserved_opcode_from_a_peer_is_not_logged_as_an_unhandled_application_error(string path)
    {
        await using var host = await PeerFailureHost.StartAsync();
        using var tcp = await host.UpgradeAsync(path);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Frame(0x1, fin: false, Encoding.ASCII.GetBytes("{\"partial\":")));
        await stream.WriteAsync(Frame(0x3, fin: true, Encoding.ASCII.GetBytes("x"))); // reserved non-control opcode

        await ReadToEndAsync(stream);
        await host.WaitRequestEndedAsync();

        host.Logs.Records.Where(r => r.Level >= LogLevel.Error).Select(r => r.Text).Should().BeEmpty(
            "a peer's protocol violation is not an application error");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_peer_that_drops_the_connection_without_a_close_is_not_logged_as_an_unhandled_application_error(string path)
    {
        await using var host = await PeerFailureHost.StartAsync();
        var tcp = await host.UpgradeAsync(path);
        await tcp.GetStream().WriteAsync(Frame(0x1, fin: false, Encoding.ASCII.GetBytes("{\"partial\":")));
        tcp.LingerState = new LingerOption(true, 0);
        tcp.Close(); // reset, no close frame

        await host.WaitRequestEndedAsync();

        host.Logs.Records.Where(r => r.Level >= LogLevel.Error).Select(r => r.Text).Should().BeEmpty(
            "a peer that vanished is not an application error");
    }

    private static byte[] Frame(int opcode, bool fin, byte[] payload)
    {
        payload.Length.Should().BeLessThan(126);
        var mask = RandomNumberGenerator.GetBytes(4);
        var frame = new byte[6 + payload.Length];
        frame[0] = (byte)((fin ? 0x80 : 0) | opcode);
        frame[1] = (byte)(0x80 | payload.Length);
        mask.CopyTo(frame, 2);
        for (var i = 0; i < payload.Length; i++)
            frame[6 + i] = (byte)(payload[i] ^ mask[i % 4]);
        return frame;
    }

    private static async Task ReadToEndAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Guard);
        var buffer = new byte[4096];
        while (true)
        {
            try
            {
                if (await stream.ReadAsync(buffer, cts.Token) == 0)
                    return;
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private sealed class PeerFailureHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _ended;

        private PeerFailureHost(WebApplication app, CapturingLoggerProvider logs)
        {
            _app = app;
            Logs = logs;
        }

        public CapturingLoggerProvider Logs { get; }

        public static async Task<PeerFailureHost> StartAsync()
        {
            var logs = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
            builder.Services.AddDidComm(b =>
            {
                b.UseNetDidResolver();
                b.UseSecretsResolver(new InMemorySecretsResolver());
            });
            var app = builder.Build();
            PeerFailureHost? self = null;
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                finally
                {
                    Interlocked.Increment(ref self!._ended);
                }
            });
            app.UseWebSockets();
            app.MapDidCommWebSocket("/ws");
            app.MapDidCommWebSocket("/ws-callback", (_, _) => Task.CompletedTask);
            self = new PeerFailureHost(app, logs);
            await app.StartAsync();
            return self;
        }

        public async Task<TcpClient> UpgradeAsync(string path)
        {
            var port = new Uri(_app.Urls.Single()).Port;
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using var cts = new CancellationTokenSource(Guard);
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var stream = tcp.GetStream();
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"), cts.Token);
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one, cts.Token) == 0)
                    break;
                head.Add(one[0]);
            }
            Encoding.ASCII.GetString(head.ToArray()).Should().StartWith("HTTP/1.1 101");
            return tcp;
        }

        /// <summary>Wait until the request has left the pipeline, then let the server write the lines that follow it.</summary>
        public async Task WaitRequestEndedAsync()
        {
            (await ReturnRouteTestHost.WaitUntilAsync(() => Volatile.Read(ref _ended) >= 1, Guard)).Should().BeTrue("the request ends");
            (await ReturnRouteTestHost.WaitUntilAsync(
                    () => Logs.Records.Any(r => r.Level >= LogLevel.Error || r.Text.Contains("Request finished", StringComparison.Ordinal)),
                    Guard))
                .Should().BeTrue("the hosting layer finishes the request");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
