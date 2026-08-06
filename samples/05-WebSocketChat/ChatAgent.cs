using System.Text.Json.Nodes;
using System.Threading.Channels;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;

namespace DidComm.Samples.WebSocketChat;

/// <summary>
/// One self-contained chat participant: its own keys, its own <c>did:peer:2</c>, its own
/// ASP.NET Core host receiving DIDComm over WebSocket (<c>MapDidCommWebSocket</c>), and its
/// own WebSocket transport for outbound sends. Two of these talk to each other in
/// <see cref="Program"/> — the same shape two real agents on two machines would have.
/// </summary>
internal sealed class ChatAgent : IAsyncDisposable
{
    private readonly Narrator _narrator;
    private readonly Channel<UnpackResult> _inbound = Channel.CreateUnbounded<UnpackResult>();

    private ChatAgent(string name, Narrator narrator)
    {
        Name = name;
        _narrator = narrator;
        Chat = new ChatLog();
    }

    /// <summary>Display name used in the narration ("alice" / "bob").</summary>
    public string Name { get; }

    /// <summary>This agent's private keys — in-memory here; your KMS in production.</summary>
    public InMemorySecretsResolver Secrets { get; } = new();

    /// <summary>This agent's minted identity.</summary>
    public PeerIdentity Identity { get; private set; } = null!;

    /// <summary>The running host. Replaced on <see cref="RestartAsync"/>.</summary>
    public WebApplication App { get; private set; } = null!;

    /// <summary>This agent's WebSocket receive endpoint (<c>ws://127.0.0.1:port/didcomm</c>).</summary>
    public Uri Endpoint { get; private set; } = null!;

    /// <summary>Where this agent sends outbound messages — the OTHER agent's endpoint.</summary>
    public Uri PeerEndpoint { get; set; } = null!;

    /// <summary>Received chat lines + the scripted replies this agent will answer with.</summary>
    public ChatLog Chat { get; }

    /// <summary>The wired facade from this agent's own container.</summary>
    public DidCommClient Client => App.Services.GetRequiredService<DidCommClient>();

    /// <summary>Boot an agent: build the host on a dynamic loopback port, start it, mint the identity.</summary>
    public static async Task<ChatAgent> StartAsync(string name, Narrator narrator)
    {
        var agent = new ChatAgent(name, narrator);
        agent.App = agent.BuildApp(port: 0);
        await agent.App.StartAsync().ConfigureAwait(false);
        // Port 0 asked the OS for a free port; read back what it actually bound, as ws://.
        agent.Endpoint = new UriBuilder(agent.App.Urls.Single()) { Scheme = "ws", Path = "/didcomm" }.Uri;

        var sp = agent.App.Services;
        agent.Identity = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>()).ConfigureAwait(false);
        foreach (var key in agent.Identity.Privates)
            agent.Secrets.Add(key);
        return agent;
    }

    /// <summary>
    /// Bring the agent back after <see cref="StopAsync"/> — same identity, same keys, same
    /// port, a fresh host. What a crashed-and-restarted process looks like to its peer.
    /// </summary>
    public async Task RestartAsync()
    {
        App = BuildApp(Endpoint.Port);
        await App.StartAsync().ConfigureAwait(false);
    }

    /// <summary>Stop the host, aborting open WebSocket connections after a short grace period.</summary>
    public async Task StopAsync()
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await App.StopAsync(grace.Token).ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Build and send one chat line to the peer over the WebSocket transport.</summary>
    public Task<SendResult> SendChatAsync(string toDid, string content, CancellationToken ct = default)
    {
        var message = new MessageBuilder()
            .WithType(ChatHandler.MessageType)
            .WithFrom(Identity.Did)
            .WithTo(toDid)
            .WithBody(new JsonObject { ["content"] = content })
            .Build();
        return Client.SendAsync(message, new SendOptions(
            Recipients: new[] { toDid },
            From: Identity.Did,
            ServiceEndpointOverride: PeerEndpoint), ct);
    }

    /// <summary>
    /// Await the next inbound message matching <paramref name="predicate"/> — a concrete
    /// signal, never a sleep (FR-DX-02). Non-matching arrivals are consumed and skipped.
    /// </summary>
    public async Task<UnpackResult> WaitForInboundAsync(Func<UnpackResult, bool> predicate, string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var candidate = await _inbound.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (predicate(candidate))
                    return candidate;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"[{Name}] timed out waiting for {what}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await App.DisposeAsync().ConfigureAwait(false);

    private WebApplication BuildApp(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // keep the sample's console output to the narration
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services.AddSingleton(Chat); // the ChatHandler's constructor dependency
        builder.Services.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver(Secrets)
            // The client side of the transport. Production uses wss (the default AllowedSchemes);
            // plaintext ws is a loopback-demo choice. The reconnect knobs shorten the sample's
            // exponential backoff — the library defaults are 1 s base / 30 s cap / 0.5 jitter
            // with 5 attempts (DD-05).
            .UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws", "wss" };
                o.ReconnectBaseDelay = TimeSpan.FromMilliseconds(100);
                o.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
                o.MaxReconnectAttempts = 2;
            })
            // Trust Ping + Discover Features handlers (and the DiscoverFeaturesClient initiator).
            .AddBuiltInProtocols()
            // The chat protocol is just a custom IProtocolHandler (FR-PROTO-03).
            .AddProtocol<ChatHandler>()
            // WebSocket endpoints resolved for a peer are counterparty-influenced data, so the
            // outbound guard blocks loopback by default (SSRF defense). This demo runs entirely
            // on 127.0.0.1 — allow exactly that host, keep the rest of the policy intact.
            .Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1")));

        var app = builder.Build();
        app.UseWebSockets();
        // One packed envelope per WebSocket message (FR-TRN-09); fragmented frames are
        // reassembled before unpacking; the connection is one-way (FR-TRN-10).
        app.MapDidCommWebSocket("/didcomm", OnReceiveAsync);
        return app;
    }

    private async Task OnReceiveAsync(UnpackResult unpacked, CancellationToken ct)
    {
        _inbound.Writer.TryWrite(unpacked);

        // Run the inbound through this agent's protocol dispatcher — the same registry-driven
        // routing every agent does (trust-ping handler, discover-features handler, chat handler).
        var sp = App.Services;
        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;
        var outcome = await dispatcher.DispatchAsync(unpacked, client, options, ct).ConfigureAwait(false);

        // The receive side is one-way (FR-TRN-10): replies do not go back on the inbound
        // socket. This agent delivers any handler reply out of band, over its OWN transport,
        // to the peer's receive endpoint — under its own egress policy.
        if (outcome.Reply is { From: not null, To.Count: > 0 } reply)
        {
            _narrator.Step($"[{Name}] handler produced {Short(reply.Type)} — sending it out of band.");
            await client.SendAsync(reply, new SendOptions(
                Recipients: reply.To.ToArray(),
                From: reply.From,
                ServiceEndpointOverride: PeerEndpoint), ct).ConfigureAwait(false);
        }
    }

    private static string Short(string type)
    {
        var slash = type.LastIndexOf('/');
        return slash < 0 ? type : type[(slash + 1)..];
    }
}

/// <summary>
/// The chat protocol handler — a plain <see cref="IProtocolHandler"/> for Basic Message 2.0
/// (FR-PROTO-03). Records every inbound line and, when a scripted reply is queued, answers
/// with it threaded to the inbound message.
/// </summary>
internal sealed class ChatHandler : IProtocolHandler
{
    /// <summary>Basic Message 2.0 protocol identifier.</summary>
    public const string ProtocolUriValue = "https://didcomm.org/basicmessage/2.0";

    /// <summary>The single message type Basic Message 2.0 defines.</summary>
    public const string MessageType = ProtocolUriValue + "/message";

    private readonly ChatLog _log;

    public ChatHandler(ChatLog log) => _log = log;

    /// <inheritdoc />
    public string ProtocolUri => ProtocolUriValue;

    /// <inheritdoc />
    public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
    {
        if (!string.Equals(message.Type, MessageType, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<Message?>(null);
        if (string.IsNullOrEmpty(message.From) || message.To is not { Count: > 0 })
            return Task.FromResult<Message?>(null);

        var content = message.Body?["content"]?.GetValue<string>() ?? "<no content>";
        _log.Record(content);

        // Only speak when the script says so — a real handler would consult its application.
        if (!_log.TryDequeueScriptedReply(out var line))
            return Task.FromResult<Message?>(null);

        var reply = new MessageBuilder()
            .WithType(MessageType)
            .WithFrom(message.To[0])
            .WithTo(message.From)
            .WithThid(message.Thid ?? message.Id) // stay in the same conversation thread
            .WithBody(new JsonObject { ["content"] = line })
            .Build();
        return Task.FromResult<Message?>(reply);
    }
}

/// <summary>
/// Chat plumbing shared between the handler and the script: received lines are published to a
/// channel the script can await (deterministic — no polling), and scripted replies queue up
/// for the handler to answer with.
/// </summary>
internal sealed class ChatLog
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Queue<string> _scriptedReplies = new();
    private readonly object _gate = new();

    /// <summary>Invoked for every recorded line (narration hook).</summary>
    public Action<string>? OnLine { get; set; }

    /// <summary>Queue a line this agent will use to answer the next inbound chat message.</summary>
    public void EnqueueScriptedReply(string line)
    {
        lock (_gate) _scriptedReplies.Enqueue(line);
    }

    /// <summary>Handler-side: take the next scripted reply, if any.</summary>
    public bool TryDequeueScriptedReply(out string line)
    {
        lock (_gate)
        {
            if (_scriptedReplies.Count > 0)
            {
                line = _scriptedReplies.Dequeue();
                return true;
            }
        }
        line = string.Empty;
        return false;
    }

    /// <summary>Handler-side: record an inbound line.</summary>
    public void Record(string content)
    {
        OnLine?.Invoke(content);
        _lines.Writer.TryWrite(content);
    }

    /// <summary>Script-side: await the next inbound line (bounded — a hang fails loudly).</summary>
    public async Task<string> NextLineAsync(string what)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await _lines.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for chat line: {what}.");
        }
    }
}
