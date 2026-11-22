using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Transports;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>The three WebSocket endpoints that apply <see cref="DidCommReceiveOptions.MessageReceiveTimeout"/>.</summary>
public enum DeadlineEndpoint
{
    /// <summary><c>MapDidCommWebSocket(pattern)</c>: the registry-aware endpoint.</summary>
    Dispatch,

    /// <summary><c>MapDidCommWebSocket(pattern, onReceive)</c>: the callback endpoint.</summary>
    Callback,

    /// <summary>The WebSocket endpoint of <c>MapDidCommMediator(pattern)</c>.</summary>
    Mediator,
}

/// <summary>Knobs for <see cref="DeadlineKestrelHost.StartAsync"/>.</summary>
internal sealed class DeadlineKestrelHostOptions
{
    /// <summary>Applied to the host's <see cref="DidCommReceiveOptions"/>; the default sets <see cref="DeadlineKestrelHost.Limit"/>.</summary>
    public Action<DidCommReceiveOptions>? Receive { get; set; } = o => o.MessageReceiveTimeout = DeadlineKestrelHost.Limit;

    /// <summary>Register the manual clock as the container's <see cref="TimeProvider"/>.</summary>
    public bool RegisterClock { get; set; } = true;

    /// <summary>The host's <c>DidCommOptions.MaxReceiveBytes</c> unless a test sets another.</summary>
    public const int DefaultMaxReceiveBytes = 64 * 1024;

    public int MaxReceiveBytes { get; set; } = DefaultMaxReceiveBytes;

    /// <summary>Runs on the host's builder after its own services (for example to bind options from its configuration).</summary>
    public Action<WebApplicationBuilder>? Builder { get; set; }

    /// <summary>
    /// Runs inside every WebSocket request's handshake (<c>AcceptWebSocketAsync</c>), before the real
    /// one: a seam to hold the handshake or make it fail the way the server's would.
    /// </summary>
    public Func<HttpContext, Task>? Handshake { get; set; }
}

/// <summary>
/// A real Kestrel host on a loopback port the OS chose, for the WebSocket receive-deadline tests: the
/// container's <see cref="TimeProvider"/> is a <see cref="QaManualTimeProvider"/>, so a deadline fires
/// only when a test moves the clock. It maps <c>/ws</c> (registry-aware), <c>/ws-callback</c> (the
/// callback overload, which records each unpacked message id) and <c>MapDidCommMediator("/didcomm")</c>,
/// with the <see cref="RecordingHandler"/> registered next to the mediator's protocols, so a message of
/// the recording protocol is observable as dispatched on all three. The mediator's purge runs once a
/// day, so no test's clock move reaches it.
/// </summary>
internal sealed class DeadlineKestrelHost : IAsyncDisposable
{
    /// <summary>The receive time limit the tests configure: a value no default has.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(37);

    /// <summary>One clock tick: the step that separates "just inside the limit" from "at the limit".</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    /// <summary>Real-time hang guard for an event that is certain with a correct product.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public const string DispatchPath = "/ws";
    public const string CallbackPath = "/ws-callback";
    public const string MediatorPath = "/didcomm";

    private readonly List<IAsyncDisposable> _owned = new();
    private EndedWebSocketCounter _ended = new();

    private DeadlineKestrelHost(WebApplication app, int port, string did, QaManualTimeProvider time, CapturingLoggerProvider logs, RecordingHandlerState recorder, ConcurrentQueue<string> callbackSeen, TestPeer alice)
    {
        App = app;
        Port = port;
        Did = did;
        Time = time;
        Logs = logs;
        Recorder = recorder;
        CallbackSeen = callbackSeen;
        Alice = alice;
    }

    public WebApplication App { get; }

    public int Port { get; }

    /// <summary>The host's DID: the agent behind <c>/ws</c> and <c>/ws-callback</c>, and the mediator's routing DID.</summary>
    public string Did { get; }

    public QaManualTimeProvider Time { get; }

    public CapturingLoggerProvider Logs { get; }

    public RecordingHandlerState Recorder { get; }

    /// <summary>The ids of the messages the callback endpoint handed to its callback, in order.</summary>
    public ConcurrentQueue<string> CallbackSeen { get; }

    public TestPeer Alice { get; }

    public IReturnRouteSessionRegistry Registry => App.Services.GetRequiredService<IReturnRouteSessionRegistry>();

    /// <summary>WebSocket requests (any path) whose endpoint delegate has returned: their receive loop ended.</summary>
    public int EndedWebSockets => Volatile.Read(ref _ended.Count);

    /// <summary>The abort token of every WebSocket request so far, in arrival order.</summary>
    public IReadOnlyList<CancellationToken> WebSocketRequestAborts => _ended.Aborts.ToArray();

    /// <summary>Kestrel's connection id of every WebSocket request so far, in the same order as <see cref="WebSocketRequestAborts"/>.</summary>
    public IReadOnlyList<string> WebSocketConnectionIds => _ended.ConnectionIds.ToArray();

    /// <summary>Wait (real-time hang guard) until <paramref name="count"/> WebSocket requests have ended.</summary>
    public async Task WaitEndedWebSocketsAsync(int count)
        => (await ReturnRouteTestHost.WaitUntilAsync(() => EndedWebSockets >= count, Guard)).Should().BeTrue($"{count} WebSocket connections ended");

    public static async Task<DeadlineKestrelHost> StartAsync(Action<DeadlineKestrelHostOptions>? configure = null)
    {
        var options = new DeadlineKestrelHostOptions();
        configure?.Invoke(options);

        // The service URL only has to be a valid DIDComm endpoint for the mediator's start-up check;
        // the host listens on a port the OS picks.
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://127.0.0.1/didcomm", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        var alice = await MediatorCast.MintPeerAsync();
        var time = new QaManualTimeProvider();
        var logs = new CapturingLoggerProvider();
        var recorder = new RecordingHandlerState();
        var callbackSeen = new ConcurrentQueue<string>();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(recorder);
        if (options.RegisterClock)
            builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddMediator(m =>
            {
                m.CoordinateMediation.RoutingDids.Add(identity.Did);
                m.PurgeInterval = TimeSpan.FromHours(24);
            });
            b.AddProtocol<RecordingHandler>();
            b.Configure(o => o.MaxReceiveBytes = options.MaxReceiveBytes);
        });
        if (options.Receive is { } receive)
            builder.Services.AddOptions<DidCommReceiveOptions>().Configure(receive);
        options.Builder?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets();
        var ended = new EndedWebSocketCounter();
        app.Use(async (context, next) =>
        {
            // #100 OD-8: observe when a WebSocket request's endpoint delegate returns, which is after
            // its receive loop and the loop's finally ran (nothing more is read or handled), and keep
            // each WebSocket request's abort token, so a test can wait until the server saw a drop,
            // and its Kestrel connection id, so a test can wait until Kestrel ended that connection.
            if (context.WebSockets.IsWebSocketRequest)
            {
                lock (ended)
                {
                    ended.Aborts.Enqueue(context.RequestAborted);
                    ended.ConnectionIds.Enqueue(context.Connection.Id);
                }
                if (options.Handshake is { } handshake)
                    context.Features.Set<IHttpWebSocketFeature>(new HookedHandshake(context.Features.Get<IHttpWebSocketFeature>()!, context, handshake));
            }
            try
            {
                await next(context);
            }
            finally
            {
                if (context.WebSockets.IsWebSocketRequest)
                    Interlocked.Increment(ref ended.Count);
            }
        });
        app.MapDidCommWebSocket(DispatchPath);
        app.MapDidCommWebSocket(CallbackPath, (unpacked, _) =>
        {
            callbackSeen.Enqueue(unpacked.Message.Id);
            return Task.CompletedTask;
        });
        app.MapDidCommMediator(MediatorPath);
        await app.StartAsync();
        if (options.RegisterClock)
            await time.WaitForScheduledTimersAsync(1, Guard); // the mediator's purge loop, so no test's mark sees it

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var host = new DeadlineKestrelHost(app, new Uri(address).Port, identity.Did, time, logs, recorder, callbackSeen, alice);
        host._owned.Add(alice);
        host._ended = ended;
        return host;
    }

    /// <summary>Dispose <paramref name="owned"/> with the host.</summary>
    public T Own<T>(T owned) where T : IAsyncDisposable
    {
        _owned.Add(owned);
        return owned;
    }

    public static string PathOf(DeadlineEndpoint endpoint) => endpoint switch
    {
        DeadlineEndpoint.Dispatch => DispatchPath,
        DeadlineEndpoint.Callback => CallbackPath,
        DeadlineEndpoint.Mediator => MediatorPath,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint)),
    };

    public Uri UriOf(DeadlineEndpoint endpoint) => new($"ws://127.0.0.1:{Port}{PathOf(endpoint)}");

    public async Task<ClientWebSocket> ConnectAsync(DeadlineEndpoint endpoint)
    {
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(Guard);
        await ws.ConnectAsync(UriOf(endpoint), cts.Token);
        return ws;
    }

    public Task<RawWebSocketPeer> ConnectRawAsync(DeadlineEndpoint endpoint) => RawWebSocketPeer.ConnectAsync(Port, PathOf(endpoint));

    /// <summary>A message of the recording protocol from Alice to the host, stamped now; with <c>return_route: all</c> when asked.</summary>
    public Message Probe(bool returnRoute = false)
    {
        var builder = new MessageBuilder()
            .WithType(RecordingHandler.ProtocolUri + "/probe")
            .WithFrom(Alice.Did)
            .WithTo(Did)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (returnRoute)
            builder = builder.WithReturnRoute(ReturnRouteMode.All);
        return builder.Build();
    }

    /// <summary><paramref name="message"/> authcrypted from Alice to the host, as UTF-8 bytes.</summary>
    public async Task<byte[]> PackAsync(Message message)
        => Encoding.UTF8.GetBytes((await Alice.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { Did },
            From: Alice.Did,
            Forward: false))).Message);

    /// <summary>Whether <paramref name="endpoint"/> handed the message <paramref name="id"/> to its handler (the recorder, or the callback).</summary>
    public bool Dispatched(DeadlineEndpoint endpoint, string id)
        => endpoint == DeadlineEndpoint.Callback
            ? CallbackSeen.Contains(id)
            : Recorder.Seen.Any(r => r.Message.Id == id);

    /// <summary>How many messages <paramref name="endpoint"/> handed to its handler.</summary>
    public int DispatchedCount(DeadlineEndpoint endpoint)
        => endpoint == DeadlineEndpoint.Callback ? CallbackSeen.Count : Recorder.Count;

    /// <summary>Wait (real-time hang guard) until <paramref name="endpoint"/> dispatched <paramref name="id"/>.</summary>
    public async Task WaitDispatchedAsync(DeadlineEndpoint endpoint, string id)
        => (await ReturnRouteTestHost.WaitUntilAsync(() => Dispatched(endpoint, id), Guard)).Should().BeTrue($"the endpoint dispatches message {id}");

    /// <summary>How many timers were created on the clock since <paramref name="mark"/> (scheduled, fired or disposed).</summary>
    public long TimersCreatedSince(long mark) => Time.Mark() - mark;

    /// <summary>The endpoint's receive-timeout lines (any level).</summary>
    public IReadOnlyList<(LogLevel Level, string Text)> TimeoutLines()
        => Logs.Records.Where(r => r.Text.Contains("within the receive timeout", StringComparison.Ordinal)).ToList();

    // ---- mediator (live delivery) ------------------------------------------------------------

    /// <summary>A new recipient, granted mediation by this mediator with its own DID registered (through the store: no request, no timer).</summary>
    public async Task<TestPeer> EnrolAsync()
    {
        var bob = await MediatorCast.MintPeerAsync();
        _owned.Add(bob);
        var mediation = App.Services.GetRequiredService<IMediationStore>();
        (await mediation.GrantAsync(new MediationGrant(bob.Did, new[] { Did }), CancellationToken.None)).Should().NotBeNull();
        (await mediation.UpdateRecipientsAsync(bob.Did, new[] { new RecipientUpdate(bob.Did, RecipientUpdateAction.Add) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Added);
        return bob;
    }

    /// <summary>Send <paramref name="request"/> from <paramref name="from"/> (stamped, <c>return_route: all</c>, authcrypted to the mediator) on <paramref name="ws"/>; return the next message on it, unpacked.</summary>
    public async Task<Message> AskAsync(ClientWebSocket ws, TestPeer from, Message request)
    {
        request.CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.All, thread: null);
        var packed = await from.Client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { Did }, From: from.Did, Forward: false));
        using var cts = new CancellationTokenSource(Guard);
        await ws.SendAsync(Encoding.UTF8.GetBytes(packed.Message), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        return await ReceiveMessageAsync(ws, from);
    }

    /// <summary>Receive the next whole message on <paramref name="ws"/> and unpack it as <paramref name="peer"/>.</summary>
    public static async Task<Message> ReceiveMessageAsync(ClientWebSocket ws, TestPeer peer)
    {
        var (type, text) = await ReturnRouteTestHost.ReceiveAsync(ws, Guard);
        type.Should().Be(WebSocketMessageType.Text, "the server writes each envelope as one text message");
        return (await peer.Client.UnpackAsync(text)).Message;
    }

    /// <summary>A forward from a fresh sender to <paramref name="bob"/>, posted to the mediator over HTTP; returns the status.</summary>
    public async Task<HttpStatusCode> PostForwardAsync(TestPeer bob)
    {
        var sender = await MediatorSender.CreateAsync(new MediatorNetwork(), new TestClock(DateTimeOffset.UtcNow));
        _owned.Add(sender);
        var (packed, _) = await sender.ForwardAsync(Did, bob.Did, await sender.AuthcryptAsync(sender.Note(bob.Did), bob.Did));
        using var http = new HttpClient { Timeout = Guard };
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await http.PostAsync($"http://127.0.0.1:{Port}{MediatorPath}", content);
        return response.StatusCode;
    }

    private sealed class EndedWebSocketCounter
    {
        public int Count;

        public ConcurrentQueue<CancellationToken> Aborts { get; } = new();

        public ConcurrentQueue<string> ConnectionIds { get; } = new();
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await App.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A connection that did not end in time; the host is disposed regardless.
        }
        await App.DisposeAsync();
        foreach (var owned in _owned)
            await owned.DisposeAsync();
    }
}

/// <summary>The WebSocket handshake feature with <see cref="DeadlineKestrelHostOptions.Handshake"/> run before the real one.</summary>
internal sealed class HookedHandshake(IHttpWebSocketFeature inner, HttpContext context, Func<HttpContext, Task> hook) : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest => inner.IsWebSocketRequest;

    public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext acceptContext)
    {
        await hook(context);
        return await inner.AcceptAsync(acceptContext);
    }
}

/// <summary>How the raw peers of these tests wait for the server to end their TCP connection.</summary>
internal static class ConnectionEnd
{
    /// <summary>
    /// Read and drop everything until the server ends the TCP connection: a FIN (a read of 0 bytes)
    /// or a reset. <paramref name="guard"/> is a hang guard only: when it runs out the connection has
    /// NOT ended, so this throws <see cref="TimeoutException"/> — whatever the read threw — and the
    /// guard can never count as the end the caller is waiting for.
    /// </summary>
    /// <param name="stream">The peer's network stream.</param>
    /// <param name="guard">Real-time hang guard.</param>
    /// <param name="bufferSize">Read buffer size.</param>
    public static async Task ReadUntilServerEndsAsync(Stream stream, TimeSpan guard, int bufferSize = 4096)
    {
        using var cts = new CancellationTokenSource(guard);
        var buffer = new byte[bufferSize];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"The server did not end the connection within the {guard} hang guard.");
            }
            catch (IOException)
            {
                return; // a reset: the server aborted the connection
            }
            if (read == 0)
                return;
        }
    }
}

/// <summary>Helpers for a <see cref="ClientWebSocket"/> peer of the deadline tests.</summary>
internal static class DeadlinePeer
{
    /// <summary>Send <paramref name="payload"/> as one text frame; <paramref name="endOfMessage"/> <c>false</c> leaves the message unfinished.</summary>
    public static async Task SendAsync(ClientWebSocket ws, ReadOnlyMemory<byte> payload, bool endOfMessage)
    {
        using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage, cts.Token);
    }

    /// <summary>
    /// Start reading the next message; it completes with the close status once the server's close
    /// frame arrives (and answers it, so the server's handshake ends at once). Any data message first
    /// completes it with <c>null</c> status and that message's type.
    /// </summary>
    public static async Task<(WebSocketMessageType Type, WebSocketCloseStatus? Status, string? Reason)> ReceiveCloseAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard + DeadlineKestrelHost.Guard);
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
        }
        while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
        if (result.MessageType != WebSocketMessageType.Close)
            return (result.MessageType, null, null);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", cts.Token);
        return (result.MessageType, ws.CloseStatus, ws.CloseStatusDescription);
    }
}

/// <summary>One WebSocket frame as a raw peer read it.</summary>
internal sealed record RawFrame(int Opcode, bool Fin, byte[] Payload)
{
    public const int Continuation = 0x0;
    public const int Text = 0x1;
    public const int Binary = 0x2;
    public const int Close = 0x8;
    public const int Ping = 0x9;
    public const int Pong = 0xA;

    /// <summary>The status code of a close frame.</summary>
    public int CloseCode => Payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(Payload) : 1005;

    /// <summary>The reason of a close frame.</summary>
    public string CloseReason => Payload.Length > 2 ? Encoding.UTF8.GetString(Payload, 2, Payload.Length - 2) : string.Empty;
}

/// <summary>
/// A WebSocket client written frame by frame over a raw TCP connection (RFC 6455): it sends masked
/// frames exactly as told — fragments, pings in the middle of a message, frames larger than the
/// server's limit — and reads the server's frames itself, so it can see pongs and can leave the
/// server's close frame unanswered.
/// </summary>
internal sealed class RawWebSocketPeer : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    private RawWebSocketPeer(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<RawWebSocketPeer> ConnectAsync(int port, string path)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var peer = new RawWebSocketPeer(tcp);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await peer._stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        var head = await peer.ReadHeadAsync(cts.Token);
        head.Should().StartWith("HTTP/1.1 101", "the server accepts the WebSocket upgrade");
        return peer;
    }

    /// <summary>Send one masked frame.</summary>
    public async Task SendFrameAsync(int opcode, bool fin, ReadOnlyMemory<byte> payload)
    {
        var length = payload.Length;
        var header = new List<byte> { (byte)((fin ? 0x80 : 0) | opcode) };
        if (length < 126)
        {
            header.Add((byte)(0x80 | length));
        }
        else if (length <= ushort.MaxValue)
        {
            header.Add(0x80 | 126);
            header.Add((byte)(length >> 8));
            header.Add((byte)length);
        }
        else
        {
            header.Add(0x80 | 127);
            for (var shift = 56; shift >= 0; shift -= 8)
                header.Add((byte)((long)length >> shift));
        }
        var mask = RandomNumberGenerator.GetBytes(4);
        header.AddRange(mask);
        var frame = new byte[header.Count + length];
        header.CopyTo(frame);
        var span = payload.Span;
        for (var i = 0; i < length; i++)
            frame[header.Count + i] = (byte)(span[i] ^ mask[i % 4]);
        using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        await _stream.WriteAsync(frame, cts.Token);
    }

    public Task SendTextAsync(string text, bool fin = true) => SendFrameAsync(RawFrame.Text, fin, Encoding.UTF8.GetBytes(text));

    /// <summary>Send a ping carrying <paramref name="payload"/> and wait for its pong: the server has read every frame sent before the ping.</summary>
    public async Task PingAsync(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await SendFrameAsync(RawFrame.Ping, fin: true, bytes);
        using var cts = new CancellationTokenSource(DeadlineKestrelHost.Guard);
        while (true)
        {
            var frame = await ReadFrameAsync(cts.Token);
            frame.Should().NotBeNull("the server answers the ping before the connection ends");
            if (frame!.Opcode == RawFrame.Pong && frame.Payload.AsSpan().SequenceEqual(bytes))
                return;
            frame.Opcode.Should().Be(RawFrame.Pong, "nothing but pongs is written to this peer before its message is complete");
        }
    }

    /// <summary>Read frames (skipping unsolicited pongs) until a close frame or the end of the connection (<c>null</c>).</summary>
    public async Task<RawFrame?> ReadCloseAsync(TimeSpan guard)
    {
        using var cts = new CancellationTokenSource(guard);
        while (true)
        {
            var frame = await ReadFrameAsync(cts.Token);
            if (frame is null || frame.Opcode == RawFrame.Close)
                return frame;
            frame.Opcode.Should().Be(RawFrame.Pong, "only pongs come before the close");
        }
    }

    /// <summary>
    /// Read and drop everything until the server ends the TCP connection (FIN or reset). Throws when
    /// <paramref name="guard"/> (a hang guard) runs out first: see <see cref="ConnectionEnd.ReadUntilServerEndsAsync"/>.
    /// </summary>
    public Task ReadToEndAsync(TimeSpan guard) => ConnectionEnd.ReadUntilServerEndsAsync(_stream, guard);

    /// <summary>Read one frame; <c>null</c> when the connection ended first.</summary>
    public async Task<RawFrame?> ReadFrameAsync(CancellationToken ct)
    {
        var head = await ReadExactAsync(2, ct);
        if (head is null)
            return null;
        var fin = (head[0] & 0x80) != 0;
        var opcode = head[0] & 0x0F;
        var masked = (head[1] & 0x80) != 0;
        long length = head[1] & 0x7F;
        if (length == 126)
        {
            var extended = await ReadExactAsync(2, ct);
            if (extended is null)
                return null;
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = await ReadExactAsync(8, ct);
            if (extended is null)
                return null;
            length = BinaryPrimitives.ReadInt64BigEndian(extended);
        }
        var mask = masked ? await ReadExactAsync(4, ct) : null;
        var payload = await ReadExactAsync(checked((int)length), ct);
        if (payload is null)
            return null;
        if (mask is not null)
        {
            for (var i = 0; i < payload.Length; i++)
                payload[i] ^= mask[i % 4];
        }
        return new RawFrame(opcode, fin, payload);
    }

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                return null; // a reset; a cancelled read (the caller's hang guard) propagates instead
            }
            if (n == 0)
                return null;
            read += n;
        }
        return buffer;
    }

    private async Task<string> ReadHeadAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await _stream.ReadAsync(one, ct) == 0)
                break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
