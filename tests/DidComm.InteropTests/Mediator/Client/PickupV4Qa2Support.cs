using System.Net.WebSockets;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#100): every WebSocket the mediator accepts, server side, in accept order, each wrapped in
/// a <see cref="CountingWebSocket"/> that counts the whole messages it received and sent. A test
/// reads on which of the mediator's sockets a message arrived (which socket carried a one-way
/// acknowledgement) and how many messages the mediator wrote on it (whether anything answered it).
/// Install with <c>o.ConfigurePipeline = sockets.Install</c>.
/// </summary>
internal sealed class CountingSockets
{
    private readonly object _lock = new();
    private readonly List<CountingWebSocket> _accepted = new();
    private readonly HashSet<int> _holdReceives = new();

    /// <summary>The sockets accepted so far.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _accepted.Count;
        }
    }

    /// <summary>
    /// The socket accepted as number <paramref name="index"/> (from 0) starts with its receives held:
    /// the mediator reads nothing from it until <see cref="CountingWebSocket.ReleaseReceives"/>.
    /// Call before that socket is accepted.
    /// </summary>
    public void HoldReceivesOf(int index)
    {
        lock (_lock)
            _holdReceives.Add(index);
    }

    public void Install(WebApplication app)
        => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (context.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } inner)
                context.Features.Set<IHttpWebSocketFeature>(new Feature(inner, this));
            await next(context);
        });

    /// <summary>The <paramref name="index"/>-th accepted socket (waits, bounded, for it to be recorded).</summary>
    public async Task<CountingWebSocket> AcceptedAsync(int index)
    {
        await MediatorTestServer.WaitUntilAsync(() => Count > index, "the server accepted the socket");
        lock (_lock)
            return _accepted[index];
    }

    /// <summary>The accepted sockets, in accept order.</summary>
    public IReadOnlyList<CountingWebSocket> All
    {
        get
        {
            lock (_lock)
                return _accepted.ToArray();
        }
    }

    private CountingWebSocket Add(WebSocket inner)
    {
        lock (_lock)
        {
            var socket = new CountingWebSocket(inner, hold: _holdReceives.Contains(_accepted.Count));
            _accepted.Add(socket);
            return socket;
        }
    }

    private sealed class Feature(IHttpWebSocketFeature inner, CountingSockets owner) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => owner.Add(await inner.AcceptAsync(context));
    }
}

/// <summary>The mediator's side of one WebSocket: counts whole messages received and sent; its receives can start held.</summary>
internal sealed class CountingWebSocket : WebSocket
{
    private readonly WebSocket _inner;
    private readonly TaskCompletionSource _receives = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _received;
    private int _sent;

    public CountingWebSocket(WebSocket inner, bool hold)
    {
        _inner = inner;
        if (!hold)
            _receives.TrySetResult();
    }

    /// <summary>Whole data messages (text or binary, up to their last frame) the mediator read from this socket.</summary>
    public int MessagesReceived => Volatile.Read(ref _received);

    /// <summary>Whole data messages the mediator wrote on this socket (their last frame sent).</summary>
    public int MessagesSent => Volatile.Read(ref _sent);

    /// <summary>Let the mediator read from this socket (its receives started held).</summary>
    public void ReleaseReceives() => _receives.TrySetResult();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await _receives.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await _inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.EndOfMessage && result.MessageType != WebSocketMessageType.Close)
            Interlocked.Increment(ref _received);
        return result;
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await _receives.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await _inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.EndOfMessage && result.MessageType != WebSocketMessageType.Close)
            Interlocked.Increment(ref _received);
        return result;
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        if (endOfMessage)
            Interlocked.Increment(ref _sent);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => new(SendAsync(MemoryMarshal.TryGetArray(buffer, out var segment) ? segment : new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
        => SendAsync(buffer, messageType, messageFlags.HasFlag(WebSocketMessageFlags.EndOfMessage), cancellationToken);

    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

    public override string? CloseStatusDescription => _inner.CloseStatusDescription;

    public override WebSocketState State => _inner.State;

    public override string? SubProtocol => _inner.SubProtocol;

    public override void Abort()
    {
        _receives.TrySetResult();
        _inner.Abort();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => _inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose()
    {
        _receives.TrySetResult();
        _inner.Dispose();
    }
}

/// <summary>
/// QA-2 (#100): the product's in-memory store with the next live-push lease parkable before it reads
/// the store (as <see cref="LeaseGatedStore"/>), and every owner release recorded with how many
/// records it gave back (as <see cref="LeaseRecordingStore"/>), each record written after the call
/// returned (L-065). Register with <c>o.ConfigureServices = s =&gt; Qa2LiveStore.Register(s, x =&gt; store = x)</c>;
/// read the count with <see cref="Qa2.MessageCount"/> (not <see cref="MediatorTestServer.Store"/>).
/// </summary>
internal sealed class Qa2LiveStore(InMemoryMessageStore inner) : IMessageStore, IQa2WrappingStore
{
    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _ownerReleases = new();
    private ParkedCall? _armed;

    public InMemoryMessageStore Inner { get; } = inner;

    /// <summary>How many records each owner release (a session's close) gave back, in order.</summary>
    public IReadOnlyList<int> OwnerReleases => _ownerReleases.ToArray();

    public static void Register(IServiceCollection services, Action<Qa2LiveStore> created)
        => services.AddSingleton<IMessageStore>(sp =>
        {
            var store = new Qa2LiveStore(new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));
            created(store);
            return store;
        });

    /// <summary>Park the next live-push lease (the only caller that leases with an arrival mark) before it reads the store.</summary>
    public ParkedCall ParkNextLiveLease()
    {
        lock (_lock)
        {
            _armed.Should().BeNull("one park at a time");
            return _armed = new ParkedCall();
        }
    }

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        ParkedCall? park = null;
        if (request.AfterSequence is not null)
        {
            lock (_lock)
            {
                park = _armed;
                _armed = null;
            }
        }
        if (park is not null)
            await park.ArriveAsync(ct);
        return await Inner.LeaseAsync(request, ct);
    }

    public async ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        var released = await Inner.ReleaseLeasesAsync(leaseOwner, ct);
        _ownerReleases.Enqueue(released);
        return released;
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>QA-2 (#100): the status code of every POST the mediator answered, in the order the answers were written.</summary>
internal sealed class Qa2PostRecorder
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _codes = new();

    /// <summary>The POSTs answered so far.</summary>
    public int Count => _codes.Count;

    /// <summary>The status codes of the POSTs answered after the first <paramref name="mark"/>.</summary>
    public IReadOnlyList<int> Since(int mark) => _codes.Skip(mark).ToArray();

    public void Install(WebApplication app) => app.Use(async (HttpContext context, RequestDelegate next) =>
    {
        var post = HttpMethods.IsPost(context.Request.Method);
        await next(context);
        if (post)
            _codes.Enqueue(context.Response.StatusCode);
    });
}

/// <summary>QA-2 (#100) steps over a raw socket in Message Pickup 4.0.</summary>
internal static class PickupV4Qa2
{
    /// <summary>Turn live delivery on in 4.0 over <paramref name="socket"/>; returns on the <c>status</c> that answers it, then waits for the wake-up it caused to be served.</summary>
    public static async Task GoLive40Async(MediatorTestServer mediator, WebSocket socket, TestPeer peer)
    {
        var live = MessagePickup40.CreateLiveDeliveryChange(peer.Did, mediator.Did, true);
        (await mediator.AskOverSocketAsync(socket, peer, live)).Status40(live.Id).LiveDelivery.Should().BeTrue();
        await mediator.PushesIdleAsync();
    }

    /// <summary>A 4.0 <c>messages-received</c> on <paramref name="socket"/> as 4.0 sends it (stamped, no <c>return_route</c>), then a 4.0 <c>status-request</c>: its status (the first frame back, so nothing answered the acknowledgement).</summary>
    public static async Task<PickupStatus40> Ack40OverSocketAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, params string[] ids)
    {
        var ack = PickupV4E2E.Stamped(mediator, MessagePickup40.CreateMessagesReceived(peer.Did, mediator.Did, ids));
        await PickupV4E2E.SendAsync(socket, await PickupV4E2E.PackAsIsAsync(mediator, peer, ack));
        var status = MessagePickup40.CreateStatusRequest(peer.Did, mediator.Did);
        return (await mediator.AskOverSocketAsync(socket, peer, status)).Status40(status.Id);
    }

    /// <summary>A 4.0 <c>delivery-request</c> on <paramref name="socket"/>: the delivered messages (an empty list for an empty delivery), on the request's thread.</summary>
    public static async Task<IReadOnlyList<DeliveredMessage>> Fetch40OverSocketAsync(MediatorTestServer mediator, WebSocket socket, TestPeer peer, int limit = 10)
    {
        var request = MessagePickup40.CreateDeliveryRequest(peer.Did, mediator.Did, limit);
        return (await mediator.AskOverSocketAsync(socket, peer, request)).Delivery40(request.Id);
    }

    /// <summary>A 4.0 <c>delivery-request</c> over HTTP: the delivered messages (an empty list for an empty delivery).</summary>
    public static async Task<IReadOnlyList<DeliveredMessage>> Fetch40Async(MediatorTestServer mediator, TestPeer peer, int limit = 10)
    {
        var request = MessagePickup40.CreateDeliveryRequest(peer.Did, mediator.Did, limit);
        return (await mediator.AskAsync(peer, request)).Delivery40(request.Id);
    }

    /// <summary>The inner message ids of delivered messages, opened as <paramref name="recipient"/>.</summary>
    public static async Task<IReadOnlyList<string>> InnerIdsAsync(TestPeer recipient, IEnumerable<DeliveredMessage> delivered)
    {
        var ids = new List<string>();
        foreach (var message in delivered)
            ids.Add((await recipient.Client.UnpackAsync(System.Text.Encoding.UTF8.GetString(message.Payload.Span))).Message.Id);
        return ids;
    }
}
