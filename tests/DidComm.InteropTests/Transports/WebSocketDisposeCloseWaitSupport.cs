using System.Collections.Concurrent;
using System.Net.WebSockets;
using DidComm.Extensions.DependencyInjection;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator.Client;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// #100 OD-7: the product's in-memory store, with the next acknowledgement that names a given id
/// slowed by <see cref="Delay"/> before it removes anything. The delay honours the request's token,
/// as a real store's I/O does: a mediator that stops handling the message (its connection was
/// dropped) never applies it. The mediator handles one connection's messages in order, so it reads
/// a close frame that follows the acknowledgement only once the acknowledgement was applied.
/// Register before <c>AddDidComm</c> (<c>MediatorTestServerOptions.ConfigureServices</c>) or after it
/// (the last <see cref="IMessageStore"/> registration wins).
/// </summary>
internal sealed class AckDelayingStore(InMemoryMessageStore inner) : IMessageStore, IQa2WrappingStore
{
    /// <summary>
    /// How long the slowed acknowledgement takes: well below the transport's default close bound
    /// (<see cref="WebSocketTransportOptions.SendTimeout"/>, 30 s), and long enough that a dispose that
    /// does not wait for the mediator returns first.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly object _lock = new();
    private (string Id, DelayedAcknowledgement Ack)? _armed;

    public InMemoryMessageStore Inner { get; } = inner;

    public static void Register(IServiceCollection services, Action<AckDelayingStore> created)
        => services.AddSingleton<IMessageStore>(sp =>
        {
            var store = new AckDelayingStore(new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));
            created(store);
            return store;
        });

    /// <summary>Slow the next acknowledgement whose ids include <paramref name="id"/> by <see cref="Delay"/>.</summary>
    public DelayedAcknowledgement DelayNextAcknowledgementOf(string id)
    {
        lock (_lock)
        {
            if (_armed is not null)
                throw new InvalidOperationException("One delayed acknowledgement at a time.");
            var ack = new DelayedAcknowledgement();
            _armed = (id, ack);
            return ack;
        }
    }

    public async ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        DelayedAcknowledgement? ack = null;
        lock (_lock)
        {
            if (_armed is { } armed && messageIds.Contains(armed.Id, StringComparer.Ordinal))
            {
                ack = armed.Ack;
                _armed = null;
            }
        }
        if (ack is null)
            return await Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);

        ack.Started.TrySetResult();
        try
        {
            await Task.Delay(Delay, ct);
        }
        catch (OperationCanceledException)
        {
            ack.Cancelled.TrySetResult();
            throw;
        }
        var removed = await Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        ack.Applied.TrySetResult(removed);
        return removed;
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>What became of one acknowledgement <see cref="AckDelayingStore"/> slowed.</summary>
internal sealed class DelayedAcknowledgement
{
    /// <summary>The mediator started applying it (the delay is running).</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The mediator applied it; the value is how many messages it removed.</summary>
    public TaskCompletionSource<int> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The mediator stopped handling it before it was applied (its request token fired).</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A recipient's container against a mediator on real Kestrel (<see cref="DeadlineKestrelHost"/>):
/// <c>AddDidComm</c> with did:peer resolution, the recipient's secrets, the WebSocket transport over
/// real <see cref="ClientWebSocket"/>s (<c>ReceiveInbound</c> + <c>UseInboundDispatch()</c>; a custom
/// <c>Connect</c>, since the default one refuses loopback) and <c>AddMediatorClient()</c> speaking
/// Message Pickup 4.0. Everything the client logs goes to the host's capture.
/// </summary>
internal sealed class KestrelRecipient : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private KestrelRecipient(ServiceProvider services) => _services = services;

    public IServiceProvider Services => _services;

    public PickupClient Pickup => _services.GetRequiredService<PickupClient>();

    public WebSocketDidCommTransport Transport => _services.GetRequiredService<WebSocketDidCommTransport>();

    /// <summary>Every socket the transport created, in order.</summary>
    public ConcurrentQueue<ClientWebSocket> Sockets { get; } = new();

    public static KestrelRecipient Create(DeadlineKestrelHost host, TestPeer recipient, Action<WebSocketTransportOptions>? configure = null)
    {
        KestrelRecipient? self = null;
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(host.Logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(recipient.Secrets);
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws" };
                o.MaxReconnectAttempts = 0;
                o.ReceiveInbound = true;
                o.WebSocketFactory = () =>
                {
                    var socket = new ClientWebSocket();
                    self!.Sockets.Enqueue(socket);
                    return socket;
                };
                o.Connect = (socket, uri, ct) => ((ClientWebSocket)socket).ConnectAsync(uri, ct);
                configure?.Invoke(o);
            });
            b.UseInboundDispatch();
            b.AddMediatorClient(c => c.MessagePickupVersion = MessagePickupVersion.V4);
            b.Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"));
        });
        self = new KestrelRecipient(services.BuildServiceProvider());
        return self;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
