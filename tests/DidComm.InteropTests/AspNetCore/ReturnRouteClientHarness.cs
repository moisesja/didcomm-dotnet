using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Secrets;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using DidComm.Protocols.TrustPing;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// The #92 client side of the return-route tests: a DI container built the way an application would
/// (<c>AddDidComm</c>, did:peer resolution, <c>UseHttpTransport</c>) whose facade holds the secrets of
/// one or more of the <see cref="ReturnRouteTestHost"/>'s peers, shares the host's injected clock
/// (so its FR-RR-09 stamps are what the server judges) and writes to the host's capturing logger (so
/// <see cref="ReturnRouteTestHost.LeakingLogLines"/> scans the client too). The HTTP transport's
/// primary handler is the TestServer's by default, or a stub a test supplies.
/// </summary>
internal sealed class ReturnRouteClientHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private ReturnRouteClientHarness(ServiceProvider services, ReturnRouteTestHost host)
    {
        _services = services;
        Host = host;
        Client = services.GetRequiredService<DidCommClient>();
    }

    public ReturnRouteTestHost Host { get; }

    /// <summary>The client's facade (from its own container).</summary>
    public DidCommClient Client { get; }

    public IServiceProvider Services => _services;

    /// <summary>The server's registry-aware HTTP endpoint.</summary>
    public Uri Endpoint => new(Host.Server.BaseAddress, "/didcomm");

    /// <summary>
    /// Build a client holding the secrets of <paramref name="holders"/> (Alice when none is given).
    /// </summary>
    /// <param name="host">The server under test.</param>
    /// <param name="primaryHandler">The HTTP transport's primary handler; <c>null</c> sends to the TestServer.</param>
    /// <param name="configureHttp">Extra HTTP transport options.</param>
    /// <param name="configureDidComm">Extra <c>AddDidComm</c> configuration (for example the client's own <c>ReplayProtectionOptions</c>).</param>
    /// <param name="clockOffset">How far the client's clock is from the host's (a second machine whose clock runs ahead or behind); zero shares it.</param>
    /// <param name="holders">The peers whose private keys the client holds.</param>
    public static ReturnRouteClientHarness Create(
        ReturnRouteTestHost host,
        Func<HttpMessageHandler>? primaryHandler = null,
        Action<HttpTransportOptions>? configureHttp = null,
        Action<DidCommBuilder>? configureDidComm = null,
        TimeSpan clockOffset = default,
        params TestPeer[] holders)
    {
        var peers = holders.Length == 0 ? new[] { host.Alice } : holders;
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(host.Logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new CompositeSecrets(peers.Select(p => (ISecretsResolver)p.Secrets).ToArray()));
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.MaxRetryAttempts = 0;
                o.CircuitBreakerFailureThreshold = 100;
                configureHttp?.Invoke(o);
            });
            configureDidComm?.Invoke(b);
            b.Configure(o => o.Clock = () => host.Clock.Now + clockOffset);
        });
        // Registered after UseHttpTransport, so this primary handler replaces its socket handler.
        services.AddHttpClient(HttpDidCommTransport.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => primaryHandler?.Invoke() ?? host.Server.CreateHandler());
        return new ReturnRouteClientHarness(services.BuildServiceProvider(), host);
    }

    /// <summary>Send options from <paramref name="from"/> to the server, delivered directly to <see cref="Endpoint"/>.</summary>
    public SendOptions ToServer(TestPeer? from = null)
        => new(Recipients: new[] { Host.ServerDid }, From: (from ?? Host.Alice).Did, ServiceEndpointOverride: Endpoint);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>Several peers' secrets as one resolver (the client holds Alice's and Carol's keys).</summary>
internal sealed class CompositeSecrets : ISecretsResolver
{
    private readonly ISecretsResolver[] _inner;

    public CompositeSecrets(ISecretsResolver[] inner) => _inner = inner;

    public async Task<DataProofsDotnet.Jose.Jwk?> FindAsync(string kid, CancellationToken ct = default)
    {
        foreach (var resolver in _inner)
        {
            if (await resolver.FindAsync(kid, ct) is { } found)
                return found;
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
    {
        var asked = kids.ToList();
        var present = new List<string>();
        foreach (var resolver in _inner)
            present.AddRange(await resolver.FindPresentAsync(asked, ct));
        return present.Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// The #92 WebSocket client of the return-route tests: a container built the way an application would
/// (<c>AddDidComm</c>, did:peer resolution, <c>UseWebSocketTransport</c> with
/// <see cref="WebSocketTransportOptions.ReceiveInbound"/>, <c>UseInboundDispatch</c>,
/// <c>AddBuiltInProtocols</c>) plus client-side recording handlers for the test protocol, Message
/// Pickup 3.0 / 4.0 and Trust Ping 2.0, so every message the client dispatches is recorded in
/// <see cref="Recorder"/>. It shares the host's clock and log, and holds the secrets of the given
/// peers. Sockets are TestServer WebSockets connected asynchronously inside <c>Connect</c> (no
/// thread blocks), counted in <see cref="Connects"/>.
/// </summary>
internal sealed class ReturnRouteWebSocketClientHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ConcurrentQueue<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> _events = new();
    private readonly ConnectCounter _connects;

    private ReturnRouteWebSocketClientHarness(ServiceProvider services, ReturnRouteTestHost host, RecordingHandlerState recorder, ConnectCounter connects)
    {
        _services = services;
        _connects = connects;
        Host = host;
        Recorder = recorder;
        Client = services.GetRequiredService<DidCommClient>();
        Transport = services.GetRequiredService<WebSocketDidCommTransport>();
        Transport.Lifecycle += (_, e) => _events.Enqueue((e.Kind, e.Endpoint));
    }

    public ReturnRouteTestHost Host { get; }

    /// <summary>The client's facade.</summary>
    public DidCommClient Client { get; }

    /// <summary>The client's WebSocket transport (the DI singleton).</summary>
    public WebSocketDidCommTransport Transport { get; }

    /// <summary>What the client's handlers saw (client-side state, separate from the host's).</summary>
    public RecordingHandlerState Recorder { get; }

    public IServiceProvider Services => _services;

    /// <summary>The server's registry-aware WebSocket endpoint.</summary>
    public Uri Endpoint => Host.WebSocketUri("/ws");

    /// <summary>Sockets the transport connected so far.</summary>
    public int Connects => Volatile.Read(ref _connects.Count);

    /// <summary>The transport's lifecycle events so far.</summary>
    public IReadOnlyList<(WebSocketLifecycleEventKind Kind, Uri Endpoint)> Events => _events.ToArray();

    /// <summary>How many <c>Disconnected</c> events were raised so far.</summary>
    public int Disconnects => _events.Count(e => e.Kind == WebSocketLifecycleEventKind.Disconnected);

    /// <summary>Build a WebSocket client holding the secrets of <paramref name="holders"/> (Alice when none is given).</summary>
    /// <param name="host">The server under test.</param>
    /// <param name="configure">Extra WebSocket transport options (applied last).</param>
    /// <param name="configureDidComm">Extra <c>AddDidComm</c> configuration.</param>
    /// <param name="clockOffset">How far the client's clock is from the host's (a second machine whose clock runs ahead or behind); zero shares it.</param>
    /// <param name="holders">The peers whose private keys the client holds.</param>
    public static ReturnRouteWebSocketClientHarness Create(
        ReturnRouteTestHost host,
        Action<WebSocketTransportOptions>? configure = null,
        Action<DidCommBuilder>? configureDidComm = null,
        TimeSpan clockOffset = default,
        params TestPeer[] holders)
    {
        var peers = holders.Length == 0 ? new[] { host.Alice } : holders;
        var recorder = new RecordingHandlerState();
        var connects = new ConnectCounter();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(host.Logs);
        });
        services.AddSingleton(recorder);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new CompositeSecrets(peers.Select(p => (ISecretsResolver)p.Secrets).ToArray()));
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws" };
                o.MaxReconnectAttempts = 0;
                o.ReceiveInbound = true;
                o.WebSocketFactory = () => new TestServerClientWebSocket();
                o.Connect = async (socket, uri, ct) =>
                {
                    Interlocked.Increment(ref connects.Count);
                    await ((TestServerClientWebSocket)socket).ConnectAsync(host.Server, uri, ct).ConfigureAwait(false);
                };
                configure?.Invoke(o);
            });
            b.UseInboundDispatch();
            b.AddBuiltInProtocols();
            b.AddProtocol<RecordingHandler>();
            b.AddProtocol<PickupRecordingHandler>();
            b.AddProtocol<Pickup40RecordingHandler>();
            // After AddBuiltInProtocols: last registration wins, so the client records ping-responses.
            b.AddProtocol<TrustPingRecordingHandler>();
            configureDidComm?.Invoke(b);
            b.Configure(o => o.Clock = () => host.Clock.Now + clockOffset);
        });
        return new ReturnRouteWebSocketClientHarness(services.BuildServiceProvider(), host, recorder, connects);
    }

    /// <summary>Send options from <paramref name="from"/> to the server, delivered directly to its WebSocket endpoint.</summary>
    public SendOptions ToServer(TestPeer? from = null)
        => new(Recipients: new[] { Host.ServerDid }, From: (from ?? Host.Alice).Did, ServiceEndpointOverride: Endpoint);

    /// <summary>Complete when the transport has raised <paramref name="count"/> <c>Disconnected</c> events, or fail after the timeout.</summary>
    public async Task<bool> WaitForDisconnectsAsync(int count, TimeSpan timeout)
        => await ReturnRouteTestHost.WaitUntilAsync(() => Disconnects >= count, timeout);

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class ConnectCounter
    {
        public int Count;
    }
}

/// <summary>A recording handler for Trust Ping 2.0 on the client (it records ping-responses; it never replies).</summary>
internal sealed class TrustPingRecordingHandler : RecordingHandlerBase
{
    public TrustPingRecordingHandler(RecordingHandlerState state) : base(state) { }

    protected override string Uri => TrustPing.ProtocolUri;
}

/// <summary>
/// A <see cref="WebSocket"/> the transport's factory can create before it is connected: <c>Connect</c>
/// opens a TestServer WebSocket asynchronously and every member then forwards to it.
/// </summary>
internal sealed class TestServerClientWebSocket : WebSocket
{
    private WebSocket? _inner;

    public async Task ConnectAsync(TestServer server, Uri uri, CancellationToken ct)
        => _inner = await server.CreateWebSocketClient().ConnectAsync(uri, ct).ConfigureAwait(false);

    private WebSocket Inner => _inner ?? throw new InvalidOperationException("The socket is not connected.");

    public override WebSocketCloseStatus? CloseStatus => _inner?.CloseStatus;

    public override string? CloseStatusDescription => _inner?.CloseStatusDescription;

    public override WebSocketState State => _inner?.State ?? WebSocketState.None;

    public override string? SubProtocol => _inner?.SubProtocol;

    public override void Abort() => _inner?.Abort();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => Inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => Inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => _inner?.Dispose();

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => Inner.ReceiveAsync(buffer, cancellationToken);

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => Inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
}
