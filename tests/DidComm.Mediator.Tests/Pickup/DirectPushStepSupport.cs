using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The host's directory of recipient endpoints, scripted (#110): answers <see cref="Endpoint"/>
/// (changeable at any moment), or throws <see cref="Fault"/>, or runs <see cref="OnFind"/>; records
/// every call with the DID and the token it was given.
/// </summary>
internal sealed class ScriptedEndpointDirectory : IRecipientEndpointDirectory
{
    private Uri? _endpoint;

    public ScriptedEndpointDirectory(Uri? endpoint) => _endpoint = endpoint;

    /// <summary>What the next call answers.</summary>
    public Uri? Endpoint
    {
        get => Volatile.Read(ref _endpoint);
        set => Volatile.Write(ref _endpoint, value);
    }

    /// <summary>When set, every call throws it.</summary>
    public Exception? Fault { get; set; }

    /// <summary>When set, runs inside the call (after it is recorded), before the answer: it may park, throw or change the world.</summary>
    public Func<string, CancellationToken, Task>? OnFind { get; set; }

    /// <summary>Every call: the grant DID and the token, in order.</summary>
    public ConcurrentQueue<(string GrantPeerDid, CancellationToken Token)> Calls { get; } = new();

    public async ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct)
    {
        Calls.Enqueue((grantPeerDid, ct));
        if (OnFind is { } hook)
            await hook(grantPeerDid, ct);
        if (Fault is { } fault)
            throw fault;
        return Endpoint;
    }
}

/// <summary>How a <see cref="ScriptedDirectPushSender"/> answers one send.</summary>
public enum SendAnswer
{
    /// <summary>The chain ended in a 2xx: <see cref="DirectSendResult.Delivered"/>.</summary>
    Accept,

    /// <summary>The endpoint answered a 4xx other than 408 and 429: <see cref="DirectSendResult.Refused"/>.</summary>
    Refuse,

    /// <summary>The endpoint answered otherwise (a 5xx, 408, 429, a redirect not followed): <see cref="DirectSendResult.Failed"/>.</summary>
    Fail,

    /// <summary>A transport fault: throws a <see cref="HandlerBoomException"/> carrying <see cref="HandlerBoomException.Marker"/>.</summary>
    Throw,
}

/// <summary>
/// The single-attempt sender, scripted (#110). <see cref="CheckTargetAsync"/> accepts an absolute
/// http/https URI unless <see cref="RefuseTarget"/> names a reason (with the verdict <see cref="RefuseAs"/>). Each <see cref="SendAsync"/> is
/// first recorded in <see cref="Posts"/> (the endpoint, a copy of the bytes, the token) — so a send
/// that then fails or is cancelled is still a POST — then signals <see cref="Entered"/>, waits on
/// the <see cref="Gate"/> that was set when it began (honouring the token), and answers the next entry of
/// <see cref="Script"/> (<see cref="SendAnswer.Accept"/> when it is empty).
/// </summary>
internal sealed class ScriptedDirectPushSender : IDirectPushSender
{
    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, <see cref="CheckTargetAsync"/> refuses every endpoint with this reason word.</summary>
    public string? RefuseTarget { get; set; }

    /// <summary>The verdict a refusal by <see cref="RefuseTarget"/> carries (<see cref="PushTarget.NotUsable"/> by default).</summary>
    public PushTarget RefuseAs { get; set; } = PushTarget.NotUsable;

    /// <summary>Every endpoint <see cref="CheckTargetAsync"/> was asked about.</summary>
    public ConcurrentQueue<Uri?> TargetChecks { get; } = new();

    /// <summary>Every send, in order: where, what (a copy) and under which token.</summary>
    public ConcurrentQueue<(Uri Endpoint, byte[] Payload, CancellationToken Token)> Posts { get; } = new();

    /// <summary>The answers of the next sends, in order.</summary>
    public ConcurrentQueue<SendAnswer> Script { get; } = new();

    /// <summary>When set, a send that begins from now on waits on it (with its token) after it is recorded and before it answers; read once, when the send begins.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, runs after a send is recorded and before the gate: it may change the world.</summary>
    public Func<CancellationToken, Task>? OnSend { get; set; }

    /// <summary>Completes when the next send has been recorded (reset by <see cref="ExpectSend"/>).</summary>
    public Task Entered => Volatile.Read(ref _entered).Task;

    /// <summary>A fresh <see cref="Entered"/> for the next send: call before the trigger.</summary>
    public void ExpectSend() => Volatile.Write(ref _entered, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>The payloads posted, in order.</summary>
    public IReadOnlyList<byte[]> Bodies => Posts.Select(p => p.Payload).ToArray();

    public ValueTask<PushTargetCheck> CheckTargetAsync(Uri? endpoint, CancellationToken ct)
    {
        TargetChecks.Enqueue(endpoint);
        if (endpoint is null)
            return ValueTask.FromResult(new PushTargetCheck(PushTarget.NoEndpoint, "None"));
        if (RefuseTarget is { } refused)
            return ValueTask.FromResult(new PushTargetCheck(RefuseAs, refused));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
            return ValueTask.FromResult(new PushTargetCheck(PushTarget.NotUsable, "Scheme"));
        return ValueTask.FromResult(new PushTargetCheck(PushTarget.Accepted, string.Empty));
    }

    public async Task<DirectSendResult> SendAsync(Uri endpoint, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // The gate is read before the send is signalled: a test that changes Gate once Entered has
        // completed changes the next send, never this one (L-065: Entered means "parked or not, decided").
        var gate = Gate;
        Posts.Enqueue((endpoint, payload.ToArray(), ct));
        Volatile.Read(ref _entered).TrySetResult();
        if (OnSend is { } hook)
            await hook(ct);
        if (gate is not null)
            await gate.Task.WaitAsync(ct);
        var answer = Script.TryDequeue(out var next) ? next : SendAnswer.Accept;
        return answer switch
        {
            SendAnswer.Accept => DirectSendResult.Delivered,
            SendAnswer.Refuse => DirectSendResult.Refused,
            SendAnswer.Fail => DirectSendResult.Failed,
            _ => throw new HandlerBoomException(HandlerBoomException.Marker),
        };
    }
}

/// <summary>
/// An <see cref="IMessageStore"/> over any store, recording every call in order (with the lease ids
/// of the give-backs) and with a hook after a take: what a test does "between the take and the
/// send" (move the clock, cancel the deadline, change the directory). <see cref="FailAcknowledge"/>
/// makes the removal throw.
/// </summary>
internal sealed class HookedStore(IMessageStore inner) : IMessageStore
{
    public IMessageStore Inner { get; } = inner;

    /// <summary>Every call: the method name and, for a give-back, the lease id named.</summary>
    public ConcurrentQueue<(string Method, string? LeaseId)> Calls { get; } = new();

    /// <summary>Every lease request, in order.</summary>
    public ConcurrentQueue<MessageLeaseRequest> LeaseRequests { get; } = new();

    /// <summary>Runs after the inner store's take, with what it took, before the records are returned.</summary>
    public Func<MessageLeaseRequest, IReadOnlyList<QueuedMessage>, Task>? AfterLease { get; set; }

    /// <summary>When set, <see cref="AcknowledgeAsync"/> throws a fresh <see cref="HandlerBoomException"/> (nothing is removed).</summary>
    public bool FailAcknowledge { get; set; }

    /// <summary>
    /// When set, the store breaks the contract as a host store that lost the push mark would: a push
    /// lease's request is passed on as a plain one (same lease id, no mark filter, no mark set), so a
    /// record an earlier push took is handed out again, still carrying its mark.
    /// </summary>
    public bool IgnorePushMark { get; set; }

    /// <summary>The methods called, by name, in order.</summary>
    public IReadOnlyList<string> Methods => Calls.Select(c => c.Method).ToArray();

    /// <summary>How many calls of <paramref name="method"/> there were.</summary>
    public int CountOf(string method) => Calls.Count(c => c.Method == method);

    /// <summary>Forget the calls so far (a test's baseline before the trigger).</summary>
    public void Reset()
    {
        Calls.Clear();
        LeaseRequests.Clear();
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue((nameof(EnqueueAsync), lease?.Id));
        return Inner.EnqueueAsync(message, lease, now, ct);
    }

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        Calls.Enqueue((nameof(LeaseAsync), request.Lease.Id));
        LeaseRequests.Enqueue(request);
        var asked = IgnorePushMark ? request with { Lease = request.Lease with { ForPush = false } } : request;
        var taken = await Inner.LeaseAsync(asked, ct);
        if (AfterLease is { } hook)
            await hook(request, taken);
        return taken;
    }

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
    {
        Calls.Enqueue((nameof(AcknowledgeAsync), null));
        if (FailAcknowledge)
            throw new HandlerBoomException(HandlerBoomException.Marker);
        return Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    }

    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
    {
        Calls.Enqueue((nameof(ReleaseAsync), leaseId));
        return Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    }

    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct)
    {
        Calls.Enqueue((nameof(ReturnUnsentAsync), leaseId));
        return Inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    }

    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct)
    {
        Calls.Enqueue((nameof(PassOverAsync), leaseId));
        return Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    }

    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct)
    {
        Calls.Enqueue((nameof(ReleaseLeasesAsync), null));
        return Inner.ReleaseLeasesAsync(leaseOwner, ct);
    }

    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue((nameof(GetStatusAsync), null));
        return Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    }

    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct)
    {
        Calls.Enqueue((nameof(PurgeRecipientAsync), null));
        return Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    }

    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct)
    {
        Calls.Enqueue((nameof(PurgeGrantAsync), null));
        return Inner.PurgeGrantAsync(grantPeerDid, ct);
    }

    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        Calls.Enqueue((nameof(PurgeExpiredAsync), null));
        return Inner.PurgeExpiredAsync(now, ct);
    }
}

/// <summary>
/// The pickup coordinator built by hand over a store of a <see cref="StoreKind"/> (wrapped in a
/// <see cref="HookedStore"/>), the in-memory mediation and session registries, a manual clock, a
/// scripted directory and sender, and every log line captured — the vehicle of the direct step's
/// tests (#110). No container and no keys: the step needs none, and a live session for the shared
/// mark tests is a <see cref="PickupFakeSession"/> turned live through the coordinator itself.
/// <see cref="Restart"/> builds a second coordinator over the same stores, as a restarted mediator.
/// </summary>
internal sealed class DirectPushWorld : IDisposable
{
    public const string MediatorDid = "did:example:direct-push-mediator";

    /// <summary>The endpoint the directory answers unless a test changes it.</summary>
    public static readonly Uri Inbox = new("https://inbox-one.direct-push.example/didcomm");

    private readonly MessagePickupOptions _options;
    private readonly DidCommOptions _didComm;

    private DirectPushWorld(StoreKind kind, MessagePickupOptions options)
    {
        Kind = kind;
        _options = options;
        Store = new HookedStore(StoreKinds.Create(kind, options));
        _didComm = new DidCommOptions { Clock = () => Clock.GetUtcNow() };
        Coordinator = NewCoordinator();
    }

    public StoreKind Kind { get; }
    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
    public HookedStore Store { get; }
    public ScriptedMediationStore Mediation { get; } = new();
    public InMemoryReturnRouteSessionRegistry Sessions { get; } = new();
    public HandlerLogSink Logs { get; } = new();
    public LetterIds Ids { get; } = new("direct-push-record");
    public ScriptedEndpointDirectory Directory { get; } = new(Inbox);
    public ScriptedDirectPushSender Sender { get; } = new();
    public PickupCoordinator Coordinator { get; private set; }

    /// <summary>The lease timeout in effect.</summary>
    public TimeSpan LeaseFor => _options.LeaseTimeout;

    public static DirectPushWorld Create(StoreKind kind, Action<MessagePickupOptions>? configure = null)
    {
        var options = new MessagePickupOptions();
        configure?.Invoke(options);
        return new DirectPushWorld(kind, options);
    }

    /// <summary>A restarted mediator: a new coordinator (nothing of the old one's memory) over the same stores and clock.</summary>
    public PickupCoordinator Restart() => Coordinator = NewCoordinator();

    /// <summary>A coordinator over the same stores, alongside the current one.</summary>
    public PickupCoordinator NewCoordinator()
        => new(Store, Mediation, Sessions, _options, _didComm, Ids.NewId, new HandlerCapturingLogger<PickupCoordinator>(Logs));

    /// <summary>Enrol a peer (its own DID is its one recipient DID unless <paramref name="recipient"/> is given) and return its DID.</summary>
    public async Task<string> GrantAsync(string label, string? recipient = null)
    {
        var peer = $"did:example:direct-push-{label}";
        (await Mediation.GrantAsync(new MediationGrant(peer, new[] { MediatorDid }), CancellationToken.None)).Should().NotBeNull();
        var outcomes = await Mediation.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(recipient ?? peer, RecipientUpdateAction.Add) }, CancellationToken.None);
        outcomes.Should().OnlyContain(o => o == RecipientStoreOutcome.Added || o == RecipientStoreOutcome.AlreadyRegistered);
        return peer;
    }

    /// <summary>Queue a stand-in JWE of <paramref name="size"/> bytes, seeded by <paramref name="seed"/>, for <paramref name="recipient"/>, nothing pushed; returns its record id.</summary>
    public async Task<string> QueueAsync(string recipient, byte seed, int size = 64, long? expiresTime = null, DateTimeOffset? notBefore = null)
    {
        var result = await Coordinator.DeliverAsync(recipient, StandInJwe.OfSize(size, seed), new PickupDeliveryOptions(notBefore, expiresTime, AllowLive: false), CancellationToken.None);
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        return result.MessageId!;
    }

    /// <summary>One direct push of <paramref name="peer"/>'s grant through the world's directory and sender.</summary>
    public ValueTask<DirectStep> PushAsync(string peer, CancellationToken ct = default) => PushAsync(Coordinator, peer, ct);

    /// <summary>One direct push through <paramref name="coordinator"/>.</summary>
    public ValueTask<DirectStep> PushAsync(PickupCoordinator coordinator, string peer, CancellationToken ct = default)
        => coordinator.PushNextDirectAsync(peer, Directory, Sender, ct);

    /// <summary>
    /// What a <c>delivery-request</c> would be handed now — under a lease that is not a push lease
    /// and ends at once, so it changes nothing (no lease left behind, no mark set or cleared).
    /// </summary>
    public async Task<IReadOnlyList<QueuedMessage>> PeekPickupAsync(string peer)
    {
        var now = Clock.GetUtcNow();
        return await Store.Inner.LeaseAsync(new MessageLeaseRequest(peer, null, 100, long.MaxValue, now, new MessageLease(null, now)), CancellationToken.None);
    }

    /// <summary>How many records of <paramref name="peer"/>'s grant the store reports now (its status: leased and marked records included).</summary>
    public async Task<int> StatusCountAsync(string peer)
        => (await Store.Inner.GetStatusAsync(peer, null, Clock.GetUtcNow(), CancellationToken.None)).MessageCount;

    /// <summary>A live session of <paramref name="peer"/>, registered, with live delivery turned on through the coordinator; every record pushed to it is recorded in <paramref name="pushed"/>.</summary>
    public async Task<PickupFakeSession> GoLiveAsync(string peer, ConcurrentQueue<string> pushed)
    {
        var session = new PickupFakeSession(peer, MediatorDid, live: true);
        Sessions.Register(session);
        var outcome = await Coordinator.SetLiveDeliveryAsync(
            peer,
            liveDelivery: true,
            session,
            (local, to, delivered) =>
            {
                pushed.Enqueue(delivered.Id);
                return new Message { Id = "live-" + delivered.Id, Type = "https://didcomm.org/messagepickup/3.0/delivery", From = local, To = new[] { to } };
            },
            "live-on",
            CancellationToken.None);
        outcome.Reply.Should().Be(PickupReply.Status, "precondition: live delivery is on");
        outcome.Status!.LiveDelivery.Should().BeTrue();
        return session;
    }

    /// <summary>The seed a posted body was made with (<see cref="StandInJwe.SeedOf"/>).</summary>
    public static byte SeedOf(byte[] body) => StandInJwe.SeedOf(body);

    public void Dispose() => Sessions.Dispose();
}
