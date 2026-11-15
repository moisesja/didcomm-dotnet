using System.Net.WebSockets;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.InteropTests.Mediator;

// Gates that let a mediator test force the interleaving it means (#119, L-044) instead of hoping the
// scheduler produces it: a server-side WebSocket whose sends can be held, and a message store whose
// live-push leases can be parked.

/// <summary>
/// Every WebSocket the mediator accepts, server side, in accept order, each wrapped in a
/// <see cref="GatedWebSocket"/>. Install with <c>o.ConfigurePipeline = sockets.Install</c>: a
/// middleware that wraps the request's <see cref="IHttpWebSocketFeature"/>.
/// </summary>
internal sealed class GatedSockets
{
    private readonly object _lock = new();
    private readonly List<GatedWebSocket> _accepted = new();

    /// <summary>The sockets accepted so far.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _accepted.Count;
        }
    }

    public void Install(WebApplication app)
        => app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (context.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } inner)
                context.Features.Set<IHttpWebSocketFeature>(new Feature(inner, this));
            await next(context);
        });

    /// <summary>
    /// The <paramref name="index"/>-th accepted socket. The client's <c>ConnectAsync</c> can return a
    /// moment before the wrapper is recorded, so this waits for it (bounded).
    /// </summary>
    public async Task<GatedWebSocket> AcceptedAsync(int index)
    {
        await MediatorTestServer.WaitUntilAsync(() => Count > index, "the server accepted the socket");
        lock (_lock)
            return _accepted[index];
    }

    private void Add(GatedWebSocket socket)
    {
        lock (_lock)
            _accepted.Add(socket);
    }

    private sealed class Feature(IHttpWebSocketFeature inner, GatedSockets owner) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;

        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            var socket = new GatedWebSocket(await inner.AcceptAsync(context));
            owner.Add(socket);
            return socket;
        }
    }
}

/// <summary>
/// The mediator's side of one WebSocket, with a send gate. TestHost's own send never waits (it
/// copies the frame into an unbounded in-memory queue), so a client that stops reading never slows
/// the mediator down. While this gate holds, a send waits for a permit instead — what a real socket's
/// send does once a peer stopped reading and the TCP window is full. The wait is asynchronous: the
/// session's writer awaits its send outside its own send lock.
/// </summary>
internal sealed class GatedWebSocket(WebSocket inner) : WebSocket
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private bool _holding;
    private int _permits;
    private int _held;
    private int _completed;

    /// <summary>Sends that had to wait at the gate, so far.</summary>
    public int SendsHeld
    {
        get
        {
            lock (_lock)
                return _held;
        }
    }

    /// <summary>Sends waiting at the gate right now.</summary>
    public int SendsWaiting
    {
        get
        {
            lock (_lock)
                return _waiting.Count;
        }
    }

    /// <summary>Sends that went through to the underlying socket and returned.</summary>
    public int SendsCompleted => Volatile.Read(ref _completed);

    /// <summary>From now on every send waits for a permit.</summary>
    public void Hold()
    {
        lock (_lock)
            _holding = true;
    }

    /// <summary>Let <paramref name="sends"/> more sends through (the waiting ones first).</summary>
    public void Allow(int sends)
    {
        lock (_lock)
        {
            for (var i = 0; i < sends; i++)
            {
                if (_waiting.TryDequeue(out var waiter))
                    waiter.TrySetResult();
                else
                    _permits++;
            }
        }
    }

    /// <summary>Stop holding: every waiting send goes on, and later ones do not wait.</summary>
    public void Open()
    {
        lock (_lock)
        {
            _holding = false;
            _permits = 0;
            while (_waiting.TryDequeue(out var waiter))
                waiter.TrySetResult();
        }
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        TaskCompletionSource? wait = null;
        lock (_lock)
        {
            if (_holding)
            {
                if (_permits > 0)
                {
                    _permits--;
                }
                else
                {
                    wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiting.Enqueue(wait);
                    _held++;
                }
            }
        }
        if (wait is not null)
            await wait.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _completed);
    }

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => new(SendAsync(MemoryMarshal.TryGetArray(buffer, out var segment) ? segment : new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken)
        => SendAsync(buffer, messageType, messageFlags.HasFlag(WebSocketMessageFlags.EndOfMessage), cancellationToken);

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => inner.ReceiveAsync(buffer, cancellationToken);

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;

    public override string? CloseStatusDescription => inner.CloseStatusDescription;

    public override WebSocketState State => inner.State;

    public override string? SubProtocol => inner.SubProtocol;

    public override void Abort()
    {
        Open();
        inner.Abort();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose()
    {
        Open();
        inner.Dispose();
    }
}

/// <summary>
/// The product's in-memory store with the live push's leases parkable, one at a time. A live push
/// is the only caller that leases with <see cref="MessageLeaseRequest.AfterSequence"/> set; a push
/// operation parked there has started (its grant is busy, its wake-up consumed) and has not read the
/// store yet. Register with <c>o.ConfigureServices = s =&gt; LeaseGatedStore.Register(s, x =&gt; store = x)</c>
/// (before <c>AddMediator</c>, which only adds a store when none is registered);
/// <see cref="MediatorTestServer.Store"/> then returns <see cref="Inner"/>.
/// </summary>
internal sealed class LeaseGatedStore(InMemoryMessageStore inner) : IMessageStore
{
    private readonly object _lock = new();
    private int _liveLeases;
    private (int At, ParkedCall Call)? _armed;

    public InMemoryMessageStore Inner { get; } = inner;

    public static void Register(IServiceCollection services, Action<LeaseGatedStore> created)
        => services.AddSingleton<IMessageStore>(sp =>
        {
            var store = new LeaseGatedStore(new InMemoryMessageStore(sp.GetRequiredService<IOptions<MessagePickupOptions>>().Value));
            created(store);
            return store;
        });

    /// <summary>Park the live-push lease that is the (<paramref name="skip"/> + 1)-th from now, before it reads the store.</summary>
    public ParkedCall ParkLiveLease(int skip = 0)
    {
        lock (_lock)
        {
            _armed.Should().BeNull("one park at a time");
            var call = new ParkedCall();
            _armed = (_liveLeases + skip + 1, call);
            return call;
        }
    }

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        ParkedCall? park = null;
        if (request.AfterSequence is not null)
        {
            lock (_lock)
            {
                var n = ++_liveLeases;
                if (_armed is { } armed && armed.At == n)
                {
                    park = armed.Call;
                    _armed = null;
                }
            }
        }
        if (park is not null)
            await park.ArriveAsync(ct);
        return await Inner.LeaseAsync(request, ct);
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>A call parked at a gate: <see cref="Arrived"/> completes once it is there; it goes on at <see cref="Release"/>.</summary>
internal sealed class ParkedCall
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Arrived => _arrived.Task;

    public void Release() => _gate.TrySetResult();

    public async Task ArriveAsync(CancellationToken ct)
    {
        _arrived.TrySetResult();
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
    }
}
