using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// A WebSocket peer on real Kestrel (loopback) that speaks raw RFC 6455 frames: it accepts each
/// connection with a hand-made upgrade, and the test writes exactly the frames it wants (a message's
/// first part and then nothing, parts with pings between them, data after the client's close) and
/// reads every frame the client sends, close status included. The client side is a real
/// <see cref="ClientWebSocket"/>, so cancellation, abort and close behave as in production.
/// </summary>
/// <remarks>
/// A ping is also the tests' synchronisation point: the client answers it from inside its next
/// receive, so its pong proves the client's reader has handled everything sent before the ping and is
/// waiting again. No frame from the client can hide behind the pong: frames arrive in order.
/// </remarks>
internal sealed class RawFrameWebSocketPeer : IAsyncDisposable
{
    private readonly Channel<RawFrameConnection> _connections = Channel.CreateUnbounded<RawFrameConnection>();
    private readonly List<RawFrameConnection> _all = new();
    private readonly WebApplication _app;
    private int _count;

    private RawFrameWebSocketPeer(WebApplication app) => _app = app;

    /// <summary>The peer's WebSocket URI.</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>How many connections were accepted.</summary>
    public int Connections => Volatile.Read(ref _count);

    public static async Task<RawFrameWebSocketPeer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var peer = new RawFrameWebSocketPeer(app);
        app.Map("/raw", peer.AcceptAsync);
        await app.StartAsync();
        peer.Endpoint = new UriBuilder(app.Urls.Single()) { Scheme = "ws", Path = "/raw" }.Uri;
        return peer;
    }

    /// <summary>The next connection the client opened (the timeout is a hang guard only).</summary>
    public async Task<RawFrameConnection> NextConnectionAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _connections.Reader.ReadAsync(cts.Token);
    }

    private async Task AcceptAsync(HttpContext context)
    {
        var upgrade = context.Features.Get<IHttpUpgradeFeature>();
        var key = context.Request.Headers.SecWebSocketKey.ToString();
        if (upgrade is not { IsUpgradableRequest: true } || key.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // RFC 6455 §4.2.2: the accept value is the base64 SHA-1 of the key and the protocol's GUID.
        context.Response.Headers.Upgrade = "websocket";
        context.Response.Headers.Connection = "Upgrade";
        context.Response.Headers.SecWebSocketAccept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var stream = await upgrade.UpgradeAsync();
        var connection = new RawFrameConnection(Interlocked.Increment(ref _count), stream);
        lock (_all)
            _all.Add(connection);
        _connections.Writer.TryWrite(connection);
        // The connection lives until the client ends it or the test does (Kestrel then closes TCP).
        await connection.Served;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_all)
        {
            foreach (var connection in _all)
                connection.End();
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _app.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        await _app.DisposeAsync();
    }
}

/// <summary>One upgraded connection of a <see cref="RawFrameWebSocketPeer"/>: write server frames, read the client's.</summary>
internal sealed class RawFrameConnection
{
    private const byte Continuation = 0x0;
    private const byte Text = 0x1;
    private const byte Close = 0x8;
    private const byte Ping = 0x9;
    private const byte Pong = 0xA;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Channel<ClientFrame> _frames = Channel.CreateUnbounded<ClientFrame>();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pings;

    public RawFrameConnection(int index, Stream stream)
    {
        Index = index;
        _stream = stream;
        var reading = Task.Run(ReadAsync);
        Served = Task.WhenAny(reading, _ended.Task);
    }

    /// <summary>The order the peer accepted this connection in (1-based).</summary>
    public int Index { get; }

    /// <summary>Completes when the client ended the connection or the test did (<see cref="End"/>).</summary>
    public Task Served { get; }

    /// <summary>Send a text message's first (or only) part.</summary>
    public Task SendTextAsync(byte[] bytes, bool endOfMessage) => SendFrameAsync(Text, endOfMessage, bytes);

    /// <summary>Send a later part of the message in progress.</summary>
    public Task SendContinuationAsync(byte[] bytes, bool endOfMessage) => SendFrameAsync(Continuation, endOfMessage, bytes);

    /// <summary>Send a ping and do not wait for its pong.</summary>
    public Task SendPingAsync(byte[] payload) => SendFrameAsync(Ping, fin: true, payload);

    /// <summary>Send a close frame.</summary>
    public Task SendCloseAsync(WebSocketCloseStatus status, string reason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var payload = new byte[2 + reasonBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
        reasonBytes.CopyTo(payload, 2);
        return SendFrameAsync(Close, fin: true, payload);
    }

    /// <summary>
    /// Send a ping and read the client's frames until its pong: the client's reader has then handled
    /// every frame sent before the ping. Returns the frames other than pongs that came first (for
    /// example a close frame); throws when the connection ends before the pong.
    /// </summary>
    public async Task<IReadOnlyList<ClientFrame>> PingAsync(TimeSpan timeout)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, Interlocked.Increment(ref _pings) + 1_000_000);
        await SendPingAsync(payload);
        var before = new List<ClientFrame>();
        while (true)
        {
            var frame = await NextFrameAsync(timeout)
                ?? throw new EndOfStreamException("the connection ended before the client answered the ping");
            if (frame.Opcode == Pong)
            {
                if (frame.Payload.AsSpan().SequenceEqual(payload))
                    return before;
                continue; // the pong of an earlier ping
            }
            before.Add(frame);
        }
    }

    /// <summary>The client's next frame (keep-alive pongs, which carry no payload, skipped), or <c>null</c> once the connection ended.</summary>
    public async Task<ClientFrame?> NextFrameAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (await _frames.Reader.WaitToReadAsync(cts.Token))
        {
            if (_frames.Reader.TryRead(out var frame) && !(frame.Opcode == Pong && frame.Payload.Length == 0))
                return frame;
        }
        return null;
    }

    /// <summary>The client's next whole data message (its parts joined; pongs skipped).</summary>
    public async Task<byte[]> NextMessageAsync(TimeSpan timeout)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await NextFrameAsync(timeout)
                ?? throw new EndOfStreamException("the connection ended before a whole message arrived");
            if (frame.Opcode == Pong)
                continue;
            if (frame.Opcode is not (Text or Continuation or 0x2))
                throw new InvalidOperationException($"expected a data frame, got opcode {frame.Opcode}");
            message.Write(frame.Payload);
            if (frame.Fin)
                return message.ToArray();
        }
    }

    /// <summary>End the connection from the peer's side: Kestrel closes the TCP connection.</summary>
    public void End() => _ended.TrySetResult();

    private async Task SendFrameAsync(byte opcode, bool fin, byte[] payload)
    {
        // Server frames are not masked (RFC 6455 §5.1).
        var header = new byte[10];
        header[0] = (byte)((fin ? 0x80 : 0) | opcode);
        int headerLength;
        if (payload.Length < 126)
        {
            header[1] = (byte)payload.Length;
            headerLength = 2;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length);
            headerLength = 4;
        }
        else
        {
            header[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2), (ulong)payload.Length);
            headerLength = 10;
        }
        await _write.WaitAsync();
        try
        {
            await _stream.WriteAsync(header.AsMemory(0, headerLength));
            await _stream.WriteAsync(payload);
            await _stream.FlushAsync();
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            var head = new byte[2];
            while (await _stream.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false) == 2)
            {
                var fin = (head[0] & 0x80) != 0;
                var opcode = (byte)(head[0] & 0x0F);
                var masked = (head[1] & 0x80) != 0;
                long length = head[1] & 0x7F;
                if (length == 126)
                {
                    var ext = new byte[2];
                    await _stream.ReadExactlyAsync(ext);
                    length = BinaryPrimitives.ReadUInt16BigEndian(ext);
                }
                else if (length == 127)
                {
                    var ext = new byte[8];
                    await _stream.ReadExactlyAsync(ext);
                    length = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                }
                var mask = new byte[4];
                if (masked)
                    await _stream.ReadExactlyAsync(mask);
                var payload = new byte[length];
                await _stream.ReadExactlyAsync(payload);
                if (masked)
                {
                    for (var i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];
                }
                _frames.Writer.TryWrite(new ClientFrame(fin, opcode, payload));
            }
        }
        catch (Exception)
        {
            // The connection ended mid-frame (an abort): the same as its end for the tests.
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }
}

/// <summary>A frame the client sent, unmasked.</summary>
internal sealed record ClientFrame(bool Fin, byte Opcode, byte[] Payload)
{
    /// <summary>Whether this is a close frame.</summary>
    public bool IsClose => Opcode == 0x8;

    /// <summary>A close frame's status code, if it carries one.</summary>
    public WebSocketCloseStatus? CloseStatus => IsClose && Payload.Length >= 2
        ? (WebSocketCloseStatus)BinaryPrimitives.ReadUInt16BigEndian(Payload)
        : null;

    /// <summary>A close frame's reason text.</summary>
    public string? CloseReason => IsClose && Payload.Length >= 2 ? Encoding.UTF8.GetString(Payload, 2, Payload.Length - 2) : null;
}
