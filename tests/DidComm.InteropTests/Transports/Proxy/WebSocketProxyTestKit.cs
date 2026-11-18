using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DidComm.Transports;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.Options;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// Shared pieces of the WebSocket proxy tests. Nothing here names
/// <c>WebSocketTransportOptions.TrustedProxy</c>, so the default-mode tests that use it also compile
/// against the code before the fix (their proof is that they fail there).
/// </summary>
internal static class WebSocketProxyTestKit
{
    /// <summary>Bounds every wait; with a correct product each awaited event is certain.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>The DIDComm media type every request carries.</summary>
    public const string MediaType = "application/didcomm-encrypted+json";

    /// <summary>The payload every request carries.</summary>
    public static readonly byte[] Payload = Encoding.ASCII.GetBytes("packed-envelope");

    /// <summary>
    /// Options for a transport on the default connect path: ws and wss allowed, a 1 ms reconnect
    /// back-off, a short connect budget (so a mutation that dials somewhere unexpected fails fast),
    /// and a socket factory that counts the attempts (one fresh socket per connect attempt).
    /// </summary>
    public static WebSocketTransportOptions DefaultPathOptions(OutboundEndpointPolicy policy, int maxReconnectAttempts, StrongBox<int> sockets) =>
        new()
        {
            AllowedSchemes = new[] { "ws", "wss" },
            MaxReconnectAttempts = maxReconnectAttempts,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(1),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SendTimeout = TimeSpan.FromSeconds(5),
            OutboundEndpointPolicy = policy,
            WebSocketFactory = () =>
            {
                Interlocked.Increment(ref sockets.Value);
                return new System.Net.WebSockets.ClientWebSocket();
            },
        };

    /// <summary>Build the transport over <paramref name="options"/> (the same instance, so a test can change it later).</summary>
    public static WebSocketDidCommTransport Transport(WebSocketTransportOptions options) =>
        new(Options.Create(options));

    /// <summary>A plain request to <paramref name="endpoint"/>.</summary>
    public static TransportRequest Request(Uri endpoint) => new(endpoint, Payload, MediaType);

    /// <summary>Send, expect a failure, and return it (bounded by <see cref="HangGuard"/>).</summary>
    public static async Task<Exception> SendFailsAsync(WebSocketDidCommTransport transport, TransportRequest request)
    {
        var send = transport.SendAsync(request, default);
        if (await Task.WhenAny(send, Task.Delay(HangGuard)) != send)
            throw new TimeoutException($"The send to '{request.Endpoint}' neither failed nor succeeded within the hang guard.");
        try
        {
            await send;
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"The send to '{request.Endpoint}' succeeded; it had to fail.");
    }

    /// <summary>The exception and every inner exception, outermost first.</summary>
    public static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
            yield return e;
    }

    /// <summary>Every message in the chain, joined.</summary>
    public static string Describe(Exception error) => string.Join(" | ", Chain(error).Select(e => e.Message));

    /// <summary>The http/https form of a ws/wss address (what the process proxy is asked about).</summary>
    public static Uri HttpForm(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" };
        if (endpoint.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>A loopback port nothing listens on: bound, read and released.</summary>
    public static int ClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Whether this machine has an IPv6 loopback to listen on.</summary>
    public static bool HasIPv6Loopback()
    {
        if (!Socket.OSSupportsIPv6)
            return false;
        try
        {
            var probe = new TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// The far end of a CONNECT tunnel, played by a <see cref="StandInProxy"/> responder: it answers the
/// CONNECT with 200, completes the WebSocket opening handshake on the tunnelled request (RFC 6455
/// §4.2.2), records each data message the client sends, and answers a close frame with its own. So a
/// send through the stand-in can really succeed, and the test can see what arrived.
/// </summary>
internal sealed class TunnelledWebSocketPeer
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly ConcurrentQueue<string> _tunnelled = new();
    private readonly ConcurrentQueue<byte[]> _messages = new();
    private readonly TaskCompletionSource _firstMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The request line of each tunnelled (opening handshake) request.</summary>
    public IReadOnlyList<string> TunnelledRequests => _tunnelled.ToArray();

    /// <summary>Each data message received, in order.</summary>
    public IReadOnlyList<byte[]> Messages => _messages.ToArray();

    /// <summary>Completes when the first data message was recorded.</summary>
    public Task FirstMessage => _firstMessage.Task;

    /// <summary>The <see cref="StandInProxy.Respond"/> function.</summary>
    public async Task RespondAsync(StandInConnection connection)
    {
        if (!connection.RequestLine.StartsWith("CONNECT ", StringComparison.Ordinal))
            return;
        await connection.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n");
        var head = await ReadHeadAsync(connection.Stream, connection.Stopping);
        if (head is null)
            return;
        var lines = head.Split("\r\n");
        _tunnelled.Enqueue(lines[0]);
        var key = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim()).FirstOrDefault();
        if (key is null)
        {
            await connection.StatusAsync("400 Bad Request");
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));
        await connection.WriteAsync(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

        while (await ReadFrameAsync(connection.Stream, connection.Stopping) is { } frame)
        {
            if (frame.Opcode == 0x8)
            {
                // Answer the close with a normal-closure close frame (unmasked, server to client).
                await connection.Stream.WriteAsync(new byte[] { 0x88, 0x02, 0x03, 0xE8 }, connection.Stopping);
                return;
            }
            if (frame.Opcode is 0x1 or 0x2)
            {
                _messages.Enqueue(frame.Payload);
                _firstMessage.TrySetResult();
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        while (head.Length < 64 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                return null;
            head.WriteByte(one[0]);
            var length = head.Length;
            var buffer = head.GetBuffer();
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, (int)length - 4);
        }
        return null;
    }

    private static async Task<(int Opcode, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                var ext = new byte[2];
                await stream.ReadExactlyAsync(ext, ct);
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                var ext = new byte[8];
                await stream.ReadExactlyAsync(ext, ct);
                length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ext);
            }
            var mask = new byte[4];
            if (masked)
                await stream.ReadExactlyAsync(mask, ct);
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct);
            if (masked)
            {
                for (var i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];
            }
            return (opcode, payload);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
