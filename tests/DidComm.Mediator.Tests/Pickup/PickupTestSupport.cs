using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>A clock the test moves by hand; wired into <c>DidCommOptions.Clock</c>.</summary>
internal sealed class PickupClock
{
    private long _ticks = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero).UtcTicks;

    public DateTimeOffset Now
    {
        get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>
/// A return-route session a test scripts: live or not, open or closed, and what its size-bounded
/// send answers. By default it accepts every message (<see cref="ReturnRouteSendResult.Sent"/>) and
/// records it. With <see cref="PackWith"/> set it packs each message exactly as the real sessions
/// do (authcrypt local → peer, no forward) and applies the bound to the real packed size.
/// </summary>
internal sealed class PickupFakeSession : IReturnRouteSession
{
    private readonly CancellationTokenSource _closed = new();
    private int _mode = (int)ReturnRouteMode.All;

    public PickupFakeSession(string peerDid, string localDid, bool live)
    {
        PeerDid = peerDid;
        LocalDid = localDid;
        IsLive = live;
    }

    /// <summary>Every message the session accepted, in order.</summary>
    public ConcurrentQueue<Message> Sent { get; } = new();

    /// <summary>Every bounded send attempted: the message, the bound, the answer and (when packing) the packed size.</summary>
    public ConcurrentQueue<(Message Message, int MaxPackedBytes, ReturnRouteSendResult Result, int? PackedBytes)> Attempts { get; } = new();

    /// <summary>When set, decides the answer of a bounded send (and may throw). Runs before <see cref="PackWith"/>.</summary>
    public Func<Message, int, ReturnRouteSendResult?>? OnSend { get; set; }

    /// <summary>When set, messages are really packed with this client and the bound applies to the packed size.</summary>
    public DidCommClient? PackWith { get; set; }

    public string PeerDid { get; }
    public string LocalDid { get; }
    public ReturnRouteMode Mode => (ReturnRouteMode)Volatile.Read(ref _mode);
    public string? Thread => null;
    public ReturnRouteTransport Transport => IsLive ? ReturnRouteTransport.WebSocket : ReturnRouteTransport.Http;
    public bool IsLive { get; }
    public CancellationToken Closed => _closed.Token;

    /// <summary>The peer downgraded the session (<c>return_route: none</c>): pushes are refused from now on.</summary>
    public void Downgrade() => Volatile.Write(ref _mode, (int)ReturnRouteMode.None);

    /// <summary>End the session: fires <see cref="Closed"/> and refuses every later send.</summary>
    public void Close()
    {
        Volatile.Write(ref _mode, (int)ReturnRouteMode.None);
        _closed.Cancel();
    }

    public async ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
        => await TrySendAsync(message, int.MaxValue, ct) == ReturnRouteSendResult.Sent;

    public async ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPackedBytes, 1);
        ct.ThrowIfCancellationRequested();

        int? packedBytes = null;
        ReturnRouteSendResult result;
        if (Mode != ReturnRouteMode.All)
        {
            result = ReturnRouteSendResult.NotSent;
        }
        else if (OnSend?.Invoke(message, maxPackedBytes) is { } scripted)
        {
            result = scripted;
        }
        else if (PackWith is { } client)
        {
            // What SessionOutbound does: stamp created_time if absent, authcrypt local → peer, no forward.
            message.CreatedTime ??= client.Options.Clock?.Invoke().ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { PeerDid }, From: LocalDid, Forward: false), ct);
            packedBytes = Encoding.UTF8.GetByteCount(packed.Message);
            result = packedBytes > maxPackedBytes ? ReturnRouteSendResult.TooLarge : ReturnRouteSendResult.Sent;
        }
        else
        {
            result = ReturnRouteSendResult.Sent;
        }

        Attempts.Enqueue((message, maxPackedBytes, result, packedBytes));
        if (result == ReturnRouteSendResult.Sent)
            Sent.Enqueue(message);
        return result;
    }
}

/// <summary>
/// An <see cref="IMessageStore"/> that records every call and lets a test replace or break any
/// method. Everything else goes to a real <see cref="InMemoryMessageStore"/>.
/// </summary>
internal sealed class PickupFakeStore : IMessageStore
{
    public PickupFakeStore(MessagePickupOptions? options = null) => Inner = new InMemoryMessageStore(options ?? new MessagePickupOptions());

    public InMemoryMessageStore Inner { get; }

    /// <summary>The method names called, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<MessageLeaseRequest> LeaseRequests { get; } = new();
    public ConcurrentQueue<(QueuedMessage Message, MessageLease? Lease)> Enqueued { get; } = new();
    public ConcurrentQueue<(string GrantPeerDid, IReadOnlyList<string> Ids)> Acknowledged { get; } = new();
    public ConcurrentQueue<(string GrantPeerDid, IReadOnlyList<string> Ids)> Released { get; } = new();
    public ConcurrentQueue<string> ReleasedLeaseIds { get; } = new();
    public ConcurrentQueue<(string GrantPeerDid, IReadOnlyList<string> Ids, string LeaseId, DateTimeOffset Until)> PassedOver { get; } = new();
    public ConcurrentQueue<string> ReleasedOwners { get; } = new();
    public ConcurrentQueue<(string GrantPeerDid, string? RecipientDid)> StatusRequests { get; } = new();
    public ConcurrentQueue<(string GrantPeerDid, string RecipientDid)> PurgedRecipients { get; } = new();

    public Func<QueuedMessage, MessageLease?, DateTimeOffset, CancellationToken, ValueTask<EnqueueOutcome>>? OnEnqueue { get; set; }
    public Func<MessageLeaseRequest, CancellationToken, ValueTask<IReadOnlyList<QueuedMessage>>>? OnLease { get; set; }
    public Func<string, IReadOnlyList<string>, CancellationToken, ValueTask<int>>? OnAcknowledge { get; set; }
    public Func<string, IReadOnlyList<string>, string, CancellationToken, ValueTask<int>>? OnRelease { get; set; }
    public Func<string, CancellationToken, ValueTask<int>>? OnReleaseLeases { get; set; }
    public Func<string, IReadOnlyList<string>, string, DateTimeOffset, CancellationToken, ValueTask<int>>? OnPassOver { get; set; }
    public Func<string, string?, DateTimeOffset, CancellationToken, ValueTask<QueueStatus>>? OnGetStatus { get; set; }
    public Func<string, string, CancellationToken, ValueTask<int>>? OnPurgeRecipient { get; set; }
    public Func<string, CancellationToken, ValueTask<int>>? OnPurgeGrant { get; set; }

    public int CountOf(string method) => Calls.Count(c => c == method);

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue(nameof(EnqueueAsync));
        Enqueued.Enqueue((message, lease));
        return OnEnqueue?.Invoke(message, lease, now, ct) ?? Inner.EnqueueAsync(message, lease, now, ct);
    }

    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        Calls.Enqueue(nameof(LeaseAsync));
        LeaseRequests.Enqueue(request);
        return OnLease?.Invoke(request, ct) ?? Inner.LeaseAsync(request, ct);
    }

    public ValueTask<QueuedMessage?> LeaseByIdAsync(string grantPeerDid, string messageId, MessageLease lease, DateTimeOffset now, CancellationToken ct)
        => Inner.LeaseByIdAsync(grantPeerDid, messageId, lease, now, ct);

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        Calls.Enqueue(nameof(AcknowledgeAsync));
        Acknowledged.Enqueue((grantPeerDid, messageIds.ToArray()));
        return OnAcknowledge?.Invoke(grantPeerDid, messageIds, ct) ?? Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    }

    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
    {
        Calls.Enqueue(nameof(ReleaseAsync));
        Released.Enqueue((grantPeerDid, messageIds.ToArray()));
        ReleasedLeaseIds.Enqueue(leaseId);
        return OnRelease?.Invoke(grantPeerDid, messageIds, leaseId, ct) ?? Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    }

    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct)
    {
        Calls.Enqueue(nameof(PassOverAsync));
        PassedOver.Enqueue((grantPeerDid, messageIds.ToArray(), leaseId, until));
        return OnPassOver?.Invoke(grantPeerDid, messageIds, leaseId, until, ct) ?? Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    }

    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        Calls.Enqueue(nameof(ReleaseLeasesAsync));
        ReleasedOwners.Enqueue(leaseOwner);
        return OnReleaseLeases?.Invoke(leaseOwner, ct) ?? Inner.ReleaseLeasesAsync(leaseOwner, ct);
    }

    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GetStatusAsync));
        StatusRequests.Enqueue((grantPeerDid, recipientDid));
        return OnGetStatus?.Invoke(grantPeerDid, recipientDid, now, ct) ?? Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    }

    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(PurgeRecipientAsync));
        PurgedRecipients.Enqueue((grantPeerDid, recipientDid));
        return OnPurgeRecipient?.Invoke(grantPeerDid, recipientDid, ct) ?? Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    }

    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(PurgeGrantAsync));
        return OnPurgeGrant?.Invoke(grantPeerDid, ct) ?? Inner.PurgeGrantAsync(grantPeerDid, ct);
    }

    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue(nameof(PurgeExpiredAsync));
        return Inner.PurgeExpiredAsync(now, ct);
    }
}

/// <summary>
/// A real DI container with <c>AddCoordinateMediation()</c> and <c>AddMessagePickup()</c>, real
/// did:peer identities, the real dispatcher, a hand-moved clock and every log line captured. No
/// transport: a test passes the return-route session it wants the request to arrive on.
/// </summary>
internal sealed class PickupFixture : IAsyncDisposable
{
    private PickupFixture(MediatorTestHost host, HandlerLogSink logs, PickupClock clock, PickupFakeStore? fakeStore, HandlerFakeStore? fakeMediation)
    {
        Host = host;
        Logs = logs;
        Clock = clock;
        FakeStore = fakeStore;
        FakeMediation = fakeMediation;
    }

    public MediatorTestHost Host { get; }
    public HandlerLogSink Logs { get; }
    public PickupClock Clock { get; }
    public PickupFakeStore? FakeStore { get; }
    public HandlerFakeStore? FakeMediation { get; }

    public MessagePickupQueue Queue => Host.Services.GetRequiredService<MessagePickupQueue>();
    public IMessageStore Store => Host.Services.GetRequiredService<IMessageStore>();
    public IReturnRouteSessionRegistry Sessions => Host.Services.GetRequiredService<IReturnRouteSessionRegistry>();

    /// <summary>The real in-memory message store: the container's, or the one inside the fake.</summary>
    public InMemoryMessageStore Memory => FakeStore?.Inner ?? (InMemoryMessageStore)Store;

    /// <summary>Every captured line of the pickup classes (handler and queue categories).</summary>
    public IReadOnlyList<HandlerLogEntry> PickupLogs => Logs.Entries.Where(e => e.Category.StartsWith("DidComm.Mediator.Pickup.", StringComparison.Ordinal)).ToArray();

    public static PickupFixture Create(
        Action<MessagePickupOptions>? pickup = null,
        Action<CoordinateMediationOptions>? mediation = null,
        Action<DidCommOptions>? didComm = null,
        bool fakeStore = false,
        bool fakeMediation = false,
        bool throwingLogger = false,
        Action<IServiceCollection>? services = null)
    {
        var logs = new HandlerLogSink();
        var clock = new PickupClock();
        PickupFakeStore? store = null;
        if (fakeStore)
        {
            var bounds = new MessagePickupOptions();
            pickup?.Invoke(bounds);
            store = new PickupFakeStore(bounds);
        }
        HandlerFakeStore? mediationStore = null;
        if (fakeMediation)
        {
            var bounds = new CoordinateMediationOptions();
            mediation?.Invoke(bounds);
            mediationStore = new HandlerFakeStore(bounds);
        }

        var host = MediatorTestHost.Create(
            mediation,
            before: collection =>
            {
                collection.AddSingleton(logs);
                collection.AddSingleton(typeof(ILogger<>), throwingLogger ? typeof(HandlerThrowingLogger<>) : typeof(HandlerCapturingLogger<>));
                if (store is not null)
                    collection.AddSingleton<IMessageStore>(store);
                if (mediationStore is not null)
                    collection.AddSingleton<IMediationStore>(mediationStore);
                services?.Invoke(collection);
            },
            after: builder =>
            {
                builder.Configure(o => o.Clock = () => clock.Now);
                if (didComm is not null)
                    builder.Configure(didComm);
                builder.AddMessagePickup(pickup);
            });
        return new PickupFixture(host, logs, clock, store, mediationStore);
    }

    public Task<PeerIdentity> NewPeerAsync() => Host.NewPeerAsync();

    /// <summary>A mediator identity plus an enrolled recipient that registered <paramref name="recipientDids"/> (its own DID when none are given).</summary>
    public async Task<(string Mediator, string Peer)> EnrolledAsync(params string[] recipientDids)
    {
        var mediator = (await NewPeerAsync()).Did;
        var peer = (await NewPeerAsync()).Did;
        await EnrolAsync(peer, mediator, recipientDids.Length == 0 ? new[] { peer } : recipientDids);
        return (mediator, peer);
    }

    /// <summary>Enrol <paramref name="peerDid"/> with the mediator (straight through the store) and register the DIDs.</summary>
    public async Task EnrolAsync(string peerDid, string mediatorDid, params string[] recipientDids)
    {
        var mediationStore = Host.Store;
        var grant = await mediationStore.GrantAsync(new MediationGrant(peerDid, new[] { mediatorDid }), CancellationToken.None);
        if (grant is null)
            throw new InvalidOperationException("The mediation store is full.");
        if (recipientDids.Length > 0)
        {
            var outcomes = await mediationStore.UpdateRecipientsAsync(peerDid, recipientDids.Select(d => new RecipientUpdate(d, RecipientUpdateAction.Add)).ToArray(), CancellationToken.None);
            if (outcomes.Any(o => o is not (RecipientStoreOutcome.Added or RecipientStoreOutcome.AlreadyRegistered)))
                throw new InvalidOperationException("A recipient DID could not be registered: " + string.Join(",", outcomes));
        }
    }

    /// <summary>A payload of <paramref name="size"/> bytes that looks like nothing in particular (the mediator relays bytes).</summary>
    public static byte[] Payload(int size, byte seed = 1)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
            bytes[i] = (byte)(seed + i * 31);
        return bytes;
    }

    /// <summary>Queue a message for <paramref name="recipientDid"/> and require that it was retained.</summary>
    public async Task<string> QueueAsync(string recipientDid, byte[] payload, PickupDeliveryOptions? options = null)
    {
        var result = await Queue.DeliverAsync(recipientDid, payload, options);
        if (result.Outcome is not (PickupDeliveryOutcome.Queued or PickupDeliveryOutcome.PushedLive) || result.MessageId is null)
            throw new InvalidOperationException($"The message was not retained ({result.Outcome}).");
        return result.MessageId;
    }

    /// <summary>A session for the pair, as the HTTP endpoint (not live) or the WebSocket endpoint (live) would open it.</summary>
    public PickupFakeSession Session(string peerDid, string mediatorDid, bool live = false, bool register = true, bool pack = false)
    {
        var session = new PickupFakeSession(peerDid, mediatorDid, live);
        if (pack)
            session.PackWith = Host.Client;
        if (live && register)
            Sessions.Register(session);
        return session;
    }

    /// <summary>Pack <paramref name="message"/> in <paramref name="shape"/> and unpack it as the mediator.</summary>
    public async Task<UnpackResult> ReceiveAsync(Message message, string mediatorDid, HandlerEnvelope shape = HandlerEnvelope.Authcrypt, string? sender = null)
    {
        var client = Host.Client;
        var from = sender ?? message.From ?? throw new InvalidOperationException("The test message needs a sender.");
        var to = new[] { mediatorDid };
        var packed = shape switch
        {
            HandlerEnvelope.Plaintext => await client.PackPlaintextAsync(message),
            HandlerEnvelope.SignedOnly => await client.PackSignedAsync(message, from),
            HandlerEnvelope.Anoncrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to))).Message,
            HandlerEnvelope.AnoncryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: null, SignFrom: from))).Message,
            HandlerEnvelope.Authcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from))).Message,
            HandlerEnvelope.AuthcryptOfSigned => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from, SignFrom: from))).Message,
            HandlerEnvelope.AnoncryptOfAuthcrypt => (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: to, From: from, ProtectSender: true))).Message,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        return await client.UnpackAsync(packed);
    }

    /// <summary>
    /// Pack, unpack and dispatch through the real dispatcher, with <paramref name="session"/> as the
    /// request's return-route session (<c>null</c>: none, as for a request without <c>return_route: all</c>).
    /// </summary>
    public async Task<DispatchOutcome> SendAsync(Message message, string mediatorDid, IReturnRouteSession? session = null, HandlerEnvelope shape = HandlerEnvelope.Authcrypt, string? sender = null, CancellationToken ct = default)
    {
        var received = await ReceiveAsync(message, mediatorDid, shape, sender);
        return await Host.Dispatcher.DispatchAsync(received, Host.Client, Host.Options, Task.CompletedTask, session, ct);
    }

    /// <summary>Send a request and parse the <c>status</c> it must be answered with.</summary>
    public async Task<PickupStatus> StatusOfAsync(Message request, string mediatorDid, IReturnRouteSession? session = null)
    {
        var outcome = await SendAsync(request, mediatorDid, session);
        if (outcome.Reply is null || !MessagePickup.TryParseStatus(outcome.Reply, out var status))
            throw new InvalidOperationException($"The request was not answered with a status ({outcome.Result}).");
        return status;
    }

    /// <summary>Send a <c>delivery-request</c> on <paramref name="session"/> and return the <c>delivery</c> pushed onto it, or <c>null</c> when none was.</summary>
    public async Task<(IReadOnlyList<DeliveredMessage> Messages, Message Delivery)?> FetchAsync(string peerDid, string mediatorDid, PickupFakeSession session, int limit, string? recipientDid = null)
    {
        var before = session.Sent.Count;
        var outcome = await SendAsync(MessagePickup.CreateDeliveryRequest(peerDid, mediatorDid, limit, recipientDid), mediatorDid, session);
        var pushed = session.Sent.Skip(before).ToArray();
        if (pushed.Length == 0)
            return null;
        if (pushed.Length > 1 || outcome.Reply is not null)
            throw new InvalidOperationException("A delivery-request must produce exactly one message.");
        if (!MessagePickup.TryParseDelivery(pushed[0], out var messages, out _))
            throw new InvalidOperationException("The pushed message is not a delivery.");
        return (messages, pushed[0]);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

/// <summary>Raw message builders for shapes the public <c>Create…</c> helpers refuse to produce.</summary>
internal static class PickupMessages
{
    public const string Piuri = MessagePickup.ProtocolUri;

    public static Message Raw(string type, string from, string mediator, JsonObject? body, string? id = null, string? thid = null)
    {
        var builder = new MessageBuilder().WithType(type).WithFrom(from).WithTo(mediator);
        if (id is not null)
            builder.WithId(id);
        if (thid is not null)
            builder.WithThid(thid);
        if (body is not null)
            builder.WithBody(body);
        return builder.Build();
    }
}
