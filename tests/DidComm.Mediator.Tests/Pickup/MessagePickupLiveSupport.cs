using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Helpers of the QA-4 slice (the pipeline's entry, live delivery, the session lifecycle): the
/// common steps of a test, a poll with a deadline for what the product does on the thread pool, and
/// the "no DID, no id in any log line" check.
/// </summary>
internal static class MessagePickupLive
{
    /// <summary>How long a poll waits for something the product does in the background. Far longer than it ever takes; a poll ends as soon as its condition holds.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    /// <summary>Send <c>live-delivery-change</c> on <paramref name="session"/> and return the <c>status</c> it is answered with.</summary>
    public static Task<PickupStatus> SetLiveAsync(PickupFixture fixture, string peer, string mediator, IReturnRouteSession session, bool on = true)
        => fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, on), mediator, session);

    /// <summary>The peer's <c>status</c>, asked over <paramref name="session"/> (or a new request-scoped one).</summary>
    public static Task<PickupStatus> StatusAsync(PickupFixture fixture, string peer, string mediator, IReturnRouteSession? session = null, string? recipientDid = null)
        => fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(peer, mediator, recipientDid), mediator, session ?? fixture.Session(peer, mediator));

    /// <summary>Acknowledge <paramref name="ids"/> as <paramref name="peer"/> and return the <c>status</c> that answers it.</summary>
    public static Task<PickupStatus> AckAsync(PickupFixture fixture, string peer, string mediator, params string[] ids)
        => fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, ids), mediator, fixture.Session(peer, mediator));

    /// <summary>
    /// One <c>delivery-request</c> over a new request-scoped (HTTP-like) session: what is deliverable
    /// right now, in the order delivered. The lease it takes ends by time only, and the tests move
    /// the clock by hand, so a message fetched here is never handed out again by a later fetch.
    /// </summary>
    public static async Task<IReadOnlyList<DeliveredMessage>> FetchNowAsync(PickupFixture fixture, string peer, string mediator, int limit = 10, string? recipientDid = null)
    {
        var fetched = await fixture.FetchAsync(peer, mediator, fixture.Session(peer, mediator), limit, recipientDid);
        return fetched?.Messages ?? Array.Empty<DeliveredMessage>();
    }

    /// <summary>
    /// Fetch over new request-scoped sessions until every id of <paramref name="expected"/> has been
    /// delivered, without moving the clock: proof that none of them is still under a lease. Returns
    /// every id delivered, in order. Fails at the deadline.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchUntilDeliveredAsync(PickupFixture fixture, string peer, string mediator, IReadOnlyCollection<string> expected, string because)
    {
        var delivered = new List<string>();
        await UntilAsync(async () =>
        {
            delivered.AddRange((await FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id));
            return expected.All(delivered.Contains);
        }, because);
        return delivered;
    }

    /// <summary>
    /// Poll <paramref name="condition"/> until it holds; fail at the deadline. For effects the product
    /// produces on the thread pool (the lease release of a closed session), where there is nothing to
    /// rendezvous with. The pause between probes only yields the processor: no assertion depends on it.
    /// </summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed > Deadline)
                throw new TimeoutException("Not within the deadline: " + because);
            await Task.Delay(5);
        }
    }

    /// <summary>See <see cref="UntilAsync(Func{Task{bool}}, string)"/>.</summary>
    public static Task UntilAsync(Func<bool> condition, string because)
        => UntilAsync(() => Task.FromResult(condition()), because);

    /// <summary>
    /// Raise (never lower) the thread-pool floor before a test parks pool threads on a synchronous
    /// rendezvous: CI runners have two cores, and a pool that ramps up slowly would starve the tests
    /// running in parallel with this one.
    /// </summary>
    public static void RaiseThreadPoolFloor(int parties)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var needed = Environment.ProcessorCount + 2 * parties;
        if (worker < needed)
            ThreadPool.SetMinThreads(needed, io);
    }

    /// <summary>No captured line — rendered text or structured value, at any level — carries one of <paramref name="values"/>, or the text of a fake's exception.</summary>
    public static void ShouldNotLeak(IEnumerable<HandlerLogEntry> logs, params string?[] values)
    {
        foreach (var entry in logs)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                    entry.Everything.Should().NotContain(value, "log lines carry counts, type names and enum names only");
            }
            entry.Everything.Should().NotContain(HandlerBoomException.Marker, "an exception's text never reaches a log");
        }
    }

    /// <summary>The pickup queue's own lines at <paramref name="level"/>.</summary>
    public static IReadOnlyList<HandlerLogEntry> QueueLogs(PickupFixture fixture, LogLevel level)
        => fixture.PickupLogs.Where(e => e.Level == level && e.Category == typeof(MessagePickupQueue).FullName).ToArray();
}

/// <summary>
/// An <see cref="IMediationStore"/> over the real in-memory store whose reads a test can replace or
/// break (the lookup the pipeline's delivery makes is <see cref="FindGrantByRecipientDidAsync"/>).
/// </summary>
internal sealed class MessagePickupLiveMediationStore : IMediationStore
{
    public InMemoryMediationStore Inner { get; } = new();

    /// <summary>The method names called, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>The values <see cref="FindGrantByRecipientDidAsync"/> was asked about, in order.</summary>
    public ConcurrentQueue<string> Lookups { get; } = new();

    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnFind { get; set; }
    public Func<string, CancellationToken, ValueTask<MediationGrant?>>? OnGetGrant { get; set; }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GetGrantAsync));
        return OnGetGrant is { } hook ? hook(peerDid, ct) : Inner.GetGrantAsync(peerDid, ct);
    }

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct)
    {
        Calls.Enqueue(nameof(GrantAsync));
        return Inner.GrantAsync(grant, ct);
    }

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(RevokeAsync));
        return Inner.RevokeAsync(peerDid, ct);
    }

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
    {
        Calls.Enqueue(nameof(UpdateRecipientsAsync));
        return Inner.UpdateRecipientsAsync(peerDid, updates, ct);
    }

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
    {
        Calls.Enqueue(nameof(ListRecipientsAsync));
        return Inner.ListRecipientsAsync(peerDid, offset, limit, ct);
    }

    public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        Calls.Enqueue(nameof(FindGrantByRecipientDidAsync));
        Lookups.Enqueue(recipientDid);
        return OnFind is { } hook ? hook(recipientDid, ct) : Inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}

/// <summary>An <see cref="IMessageIdGenerator"/> a test scripts; a UUID when nothing is scripted.</summary>
internal sealed class MessagePickupLiveIdGenerator : IMessageIdGenerator
{
    private int _calls;

    /// <summary>Decides the id of the n-th call, counted from 1 (and may throw or answer <c>null</c>); unset for a fresh UUID.</summary>
    public Func<int, string?>? Next { get; set; }

    /// <summary>How many ids were asked for.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public string NewId()
    {
        var call = Interlocked.Increment(ref _calls);
        return Next is { } next ? next(call)! : Guid.NewGuid().ToString();
    }
}

/// <summary>
/// An <see cref="IReturnRouteSessionRegistry"/> whose answers a test decides: a registry that is
/// down, that answers <c>null</c>, or that files a session under the wrong peer.
/// </summary>
internal sealed class MessagePickupLiveRegistry : IReturnRouteSessionRegistry
{
    private readonly ConcurrentQueue<IReturnRouteSession> _registered = new();

    /// <summary>What <see cref="Find"/> answers (and it may throw); by default the registered sessions of that peer, oldest first.</summary>
    public Func<string, IReadOnlyList<IReturnRouteSession>?>? OnFind { get; set; }

    /// <summary>The DIDs <see cref="Find"/> was asked about, in order.</summary>
    public ConcurrentQueue<string> Finds { get; } = new();

    public int Count => _registered.Count;

#pragma warning disable CS0067 // The fake raises no notifications: the pickup role must not depend on them.
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionOpened;
    public event EventHandler<ReturnRouteSessionEventArgs>? SessionClosed;
#pragma warning restore CS0067

    public SessionRegistration Register(IReturnRouteSession session)
    {
        _registered.Enqueue(session);
        return new SessionRegistration(SessionRegistrationOutcome.Registered, null);
    }

    public bool Unregister(IReturnRouteSession session) => false;

    public IReadOnlyList<IReturnRouteSession> Find(string did)
    {
        Finds.Enqueue(did);
        if (OnFind is { } hook)
            return hook(did)!;
        return _registered.Where(s => string.Equals(s.PeerDid, did, StringComparison.Ordinal)).ToArray();
    }
}

/// <summary>
/// A live session whose parts move independently, for the cases <see cref="PickupFakeSession"/>
/// ties together: <see cref="FireClosed"/> cancels <see cref="Closed"/> and nothing else, so the
/// session still reports <see cref="ReturnRouteMode.All"/> and still accepts every send (a transport
/// that has not noticed yet, or a third-party session). What stops a push to it can then only be
/// the pickup role's own per-session state.
/// </summary>
internal sealed class MessagePickupLiveLooseSession(string peerDid, string localDid) : IReturnRouteSession
{
    private readonly CancellationTokenSource _closed = new();
    private int _mode = (int)ReturnRouteMode.All;

    public ConcurrentQueue<Message> Sent { get; } = new();

    /// <summary>Every bounded send attempted: the message and the bound.</summary>
    public ConcurrentQueue<(Message Message, int MaxPackedBytes)> Attempts { get; } = new();

    /// <summary>When set, decides the answer of a bounded send (and may throw or wait).</summary>
    public Func<Message, int, CancellationToken, ValueTask<ReturnRouteSendResult>>? OnSend { get; set; }

    public string PeerDid { get; } = peerDid;
    public string LocalDid { get; } = localDid;

    public ReturnRouteMode Mode
    {
        get => (ReturnRouteMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    public string? Thread => null;
    public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;
    public bool IsLive => true;
    public CancellationToken Closed => _closed.Token;

    /// <summary>Fire <see cref="Closed"/> — and only that.</summary>
    public void FireClosed() => _closed.Cancel();

    public async ValueTask<bool> TrySendAsync(Message message, CancellationToken ct)
        => await TrySendAsync(message, int.MaxValue, ct) == ReturnRouteSendResult.Sent;

    public async ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
    {
        Attempts.Enqueue((message, maxPackedBytes));
        var result = OnSend is { } hook ? await hook(message, maxPackedBytes, ct) : ReturnRouteSendResult.Sent;
        if (result == ReturnRouteSendResult.Sent)
            Sent.Enqueue(message);
        return result;
    }
}

/// <summary>
/// An <see cref="IMessageStore"/> over the real in-memory store whose <see cref="PurgeExpiredAsync"/>
/// a test can break or observe (the one method <see cref="PickupFakeStore"/> has no hook for).
/// </summary>
internal sealed class MessagePickupLivePurgeStore : IMessageStore
{
    public InMemoryMessageStore Inner { get; } = new();

    /// <summary>The instants <see cref="PurgeExpiredAsync"/> was called with, in order.</summary>
    public ConcurrentQueue<DateTimeOffset> PurgedAt { get; } = new();

    public Func<DateTimeOffset, CancellationToken, ValueTask<int>>? OnPurgeExpired { get; set; }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);

    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);

    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, ct);

    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);

    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);

    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);

    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);

    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        PurgedAt.Enqueue(now);
        return OnPurgeExpired is { } hook ? hook(now, ct) : Inner.PurgeExpiredAsync(now, ct);
    }
}
