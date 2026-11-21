using System.Net.WebSockets;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #100 P2-2 (PR #143 review): the client's side of every WebSocket its transport opens, in the
/// order they were created, each counting the whole messages the transport wrote on it. The
/// mediator's side (<see cref="CountingSockets"/>) only counts what the mediator read, and a
/// mediator busy with one message does not read the next; this shows that the client has already
/// sent it. Install with <c>o.WebSocket = sockets.Install</c> on the harness options: the harness's
/// own connect (and its <see cref="MediatorClientHarness.Connects"/> count) is kept.
/// </summary>
internal sealed class ClientSockets
{
    private readonly object _lock = new();
    private readonly List<ClientCountingWebSocket> _created = new();

    /// <summary>Wrap the harness's socket factory and connect.</summary>
    public void Install(WebSocketTransportOptions options)
    {
        var factory = options.WebSocketFactory ?? throw new InvalidOperationException("The harness sets a socket factory.");
        var connect = options.Connect ?? throw new InvalidOperationException("The harness sets a connect.");
        options.WebSocketFactory = () => Add(factory());
        options.Connect = (socket, uri, ct) => connect(((ClientCountingWebSocket)socket).Inner, uri, ct);
    }

    /// <summary>The sockets created so far, in order.</summary>
    public IReadOnlyList<ClientCountingWebSocket> All
    {
        get
        {
            lock (_lock)
                return _created.ToArray();
        }
    }

    /// <summary>The whole messages the client wrote on its <paramref name="index"/>-th socket (from 0); 0 before that socket exists.</summary>
    public int SentOn(int index)
    {
        lock (_lock)
            return index < _created.Count ? _created[index].MessagesSent : 0;
    }

    private ClientCountingWebSocket Add(WebSocket inner)
    {
        var socket = new ClientCountingWebSocket(inner);
        lock (_lock)
            _created.Add(socket);
        return socket;
    }
}

/// <summary>The client's side of one WebSocket: forwards everything to the harness's socket and counts the whole data messages sent on it.</summary>
internal sealed class ClientCountingWebSocket(WebSocket inner) : WebSocket
{
    private int _sent;

    /// <summary>The harness's socket (what its connect connects).</summary>
    public WebSocket Inner { get; } = inner;

    /// <summary>Whole data messages written on this socket (their last frame sent).</summary>
    public int MessagesSent => Volatile.Read(ref _sent);

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await Inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        if (endOfMessage && messageType != WebSocketMessageType.Close)
            Interlocked.Increment(ref _sent);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => new(SendAsync(MemoryMarshal.TryGetArray(buffer, out var segment) ? segment : new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
        => SendAsync(buffer, messageType, messageFlags.HasFlag(WebSocketMessageFlags.EndOfMessage), cancellationToken);

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => Inner.ReceiveAsync(buffer, cancellationToken);

    public override WebSocketCloseStatus? CloseStatus => Inner.CloseStatus;

    public override string? CloseStatusDescription => Inner.CloseStatusDescription;

    public override WebSocketState State => Inner.State;

    public override string? SubProtocol => Inner.SubProtocol;

    public override void Abort() => Inner.Abort();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose() => Inner.Dispose();
}

/// <summary>
/// #100 P2-2: the product's in-memory store with the next acknowledgement that names a given id
/// held before it removes anything (<see cref="HoldNextAcknowledgementOf"/>). The mediator's
/// dispatch of that <c>messages-received</c> waits, and with it the reading of the connection it
/// came on: the mediator handles one connection's messages in order. Register with
/// <c>o.ConfigureServices = s =&gt; AckHoldingStore.Register(s, x =&gt; store = x)</c>; read the
/// count with <see cref="Qa2.MessageCount"/>.
/// </summary>
internal sealed class AckHoldingStore(InMemoryMessageStore inner) : IMessageStore, IQa2WrappingStore
{
    private readonly object _lock = new();
    private (string Id, ParkedCall Hold)? _armed;

    public InMemoryMessageStore Inner { get; } = inner;

    public static void Register(IServiceCollection services, Action<AckHoldingStore> created)
        => services.AddSingleton<IMessageStore>(sp =>
        {
            var store = new AckHoldingStore(new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));
            created(store);
            return store;
        });

    /// <summary>Hold the next acknowledgement whose ids include <paramref name="id"/>: it arrives (<see cref="ParkedCall.Arrived"/>) and waits for <see cref="ParkedCall.Release"/> before it removes anything.</summary>
    public ParkedCall HoldNextAcknowledgementOf(string id)
    {
        lock (_lock)
        {
            _armed.Should().BeNull("one hold at a time");
            var hold = new ParkedCall();
            _armed = (id, hold);
            return hold;
        }
    }

    public async ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        ParkedCall? hold = null;
        lock (_lock)
        {
            if (_armed is { } armed && messageIds.Contains(armed.Id, StringComparer.Ordinal))
            {
                hold = armed.Hold;
                _armed = null;
            }
        }
        if (hold is not null)
            await hold.ArriveAsync(ct);
        return await Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>#100 P2-2 steps.</summary>
internal static class PickupV4AckOrder
{
    /// <summary>
    /// <paramref name="task"/>, with a failure it ends with observed even when the test stops before
    /// awaiting it (a failed assertion while it runs): the harness's disposal then fails it.
    /// </summary>
    public static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}
