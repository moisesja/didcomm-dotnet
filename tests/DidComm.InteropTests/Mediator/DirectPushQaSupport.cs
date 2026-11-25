using System.Collections.Concurrent;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator;

// The pieces of the #110 end-to-end tests: recipients' own HTTP endpoints with a behaviour per
// path, the host's endpoint directory with a parkable lookup, a store that says which records a
// direct push took, and a mediator built with AddMediator(o => o.DirectPush = true) around them.

/// <summary>
/// Recipients' own HTTP endpoints on <c>inbox.test</c>, one per path: each records every POST body
/// as it arrives (before any gate), counts the POSTs in flight, and answers with its own status,
/// after its own gate when one is set. A held POST ends when its gate opens, or unanswered when its
/// client goes away.
/// </summary>
internal sealed class DirectPushInbox : IAsyncDisposable
{
    public const string Host = "inbox.test";

    private readonly ConcurrentDictionary<string, InboxPath> _paths = new(StringComparer.Ordinal);

    private DirectPushInbox(WebApplication app) => App = app;

    public WebApplication App { get; }

    /// <summary>The endpoint at <paramref name="path"/> (created on first use: it answers 202).</summary>
    public InboxPath this[string path] => _paths.GetOrAdd(path, p => new InboxPath(new Uri($"http://{Host}{p}")));

    /// <summary>Every body posted to any path, in arrival order per path.</summary>
    public int TotalPosts => _paths.Values.Sum(p => p.Posts);

    public static async Task<DirectPushInbox> StartAsync(MediatorNetwork network)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        DirectPushInbox? inbox = null;
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            var endpoint = inbox![context.Request.Path.Value ?? "/"];
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            endpoint.Arrive(ms.ToArray(), context.Request.ContentType);
            try
            {
                if (endpoint.Gate is { } gate)
                {
                    // Held until the gate opens (disposing the inbox opens every gate) or the client
                    // goes away, never for a length of time.
                    try
                    {
                        await gate.Task.WaitAsync(context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                    }
                    // A client that went away (a push abandoned at its deadline) gets no answer at
                    // all, as from a server that never answers: never a status it could read as one.
                    if (context.RequestAborted.IsCancellationRequested)
                    {
                        context.Abort();
                        return Results.Empty;
                    }
                }
                return Results.StatusCode(endpoint.Status);
            }
            finally
            {
                endpoint.Leave();
            }
        });
        inbox = new DirectPushInbox(app);
        await app.StartAsync();
        network.Add(app.GetTestServer());
        return inbox;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var path in _paths.Values)
            path.Gate?.TrySetResult();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>One recipient endpoint of a <see cref="DirectPushInbox"/>.</summary>
internal sealed class InboxPath(Uri endpoint)
{
    private readonly ConcurrentQueue<byte[]> _bodies = new();
    private readonly ConcurrentQueue<string?> _contentTypes = new();
    private int _inFlight;
    private int _maxInFlight;
    private int _ended;

    public Uri Endpoint { get; } = endpoint;

    /// <summary>The status every POST here is answered with (202 by default).</summary>
    public int Status { get; set; } = StatusCodes.Status202Accepted;

    /// <summary>When set, each POST waits for it (or for its client to go away) before it is answered.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<byte[]> Bodies => _bodies.ToArray();

    public IReadOnlyList<string?> ContentTypes => _contentTypes.ToArray();

    public int Posts => _bodies.Count;

    /// <summary>POSTs that were answered or abandoned.</summary>
    public int Ended => Volatile.Read(ref _ended);

    /// <summary>The most POSTs ever in flight here at once.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public TaskCompletionSource Hold() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Arrive(byte[] body, string? contentType)
    {
        _bodies.Enqueue(body);
        _contentTypes.Enqueue(contentType);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxInFlight)) < now && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }
    }

    internal void Leave()
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _ended);
    }
}

/// <summary>The host's <see cref="IRecipientEndpointDirectory"/>: one endpoint per grant; the next lookup of a grant can be parked.</summary>
internal sealed class DirectPushEndpoints : IRecipientEndpointDirectory
{
    private readonly ConcurrentDictionary<string, Uri> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ParkedCall> _parked = new(StringComparer.Ordinal);
    private int _asked;

    public int Asked => Volatile.Read(ref _asked);

    public void Set(string grantPeerDid, Uri endpoint) => _endpoints[grantPeerDid] = endpoint;

    /// <summary>Park the next lookup of <paramref name="grantPeerDid"/> until the call is released (it honours the push's deadline).</summary>
    public ParkedCall ParkNext(string grantPeerDid)
    {
        var call = new ParkedCall();
        _parked[grantPeerDid] = call;
        return call;
    }

    public async ValueTask<Uri?> FindAsync(string grantPeerDid, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        if (_parked.TryRemove(grantPeerDid, out var park))
            await park.ArriveAsync(ct);
        return _endpoints.TryGetValue(grantPeerDid, out var endpoint) ? endpoint : null;
    }
}

/// <summary>
/// The product's in-memory store, recording the id of every record handed to a direct push (a push
/// lease with no owner) — what a test acknowledges by id, or counts across a restart.
/// </summary>
internal sealed class DirectTakeRecordingStore(InMemoryMessageStore inner) : IMessageStore
{
    public InMemoryMessageStore Inner { get; } = inner;

    public ConcurrentQueue<string> DirectTakes { get; } = new();

    public async ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct)
    {
        var leased = await Inner.LeaseAsync(request, ct);
        if (request.Lease is { ForPush: true, Owner: null })
        {
            foreach (var record in leased)
                DirectTakes.Enqueue(record.Id);
        }
        return leased;
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> ReturnUnsentAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReturnUnsentAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);
}

/// <summary>
/// A mediator (<c>AddMediator(o =&gt; o.DirectPush = true)</c> + <c>MapDidCommMediator</c>, the HTTP
/// transport over the test network) whose directory, store and push clock the test holds, and the
/// recipients' inbox on the same network.
/// </summary>
internal sealed class DirectPushWorld : IAsyncDisposable
{
    private readonly bool _ownsInbox;

    private DirectPushWorld(MediatorTestServer mediator, DirectPushInbox inbox, bool ownsInbox, DirectPushEndpoints endpoints, DirectTakeRecordingStore store, QaManualTimeProvider time)
    {
        _ownsInbox = ownsInbox;
        Mediator = mediator;
        Inbox = inbox;
        Endpoints = endpoints;
        Store = store;
        Time = time;
    }

    public MediatorTestServer Mediator { get; }
    public DirectPushInbox Inbox { get; }
    public DirectPushEndpoints Endpoints { get; }
    public DirectTakeRecordingStore Store { get; }

    /// <summary>The push workers' clock (deadlines and cooldowns); the message clock is <see cref="MediatorTestServer.Clock"/>.</summary>
    public QaManualTimeProvider Time { get; }

    internal PushDispatcher Pushes => Mediator.Pushes;

    /// <param name="configure">More server settings; a <see cref="MediatorTestServerOptions.Mediator"/> or <see cref="MediatorTestServerOptions.ConfigureServices"/> given here runs after the world's own.</param>
    /// <param name="network">The network to join (a restart joins the first one's).</param>
    /// <param name="inbox">An inbox already on <paramref name="network"/>.</param>
    /// <param name="store">A store to run over (a restart passes the first one's).</param>
    /// <param name="mediation">A mediation store to run over (a restart passes the first one's).</param>
    public static async Task<DirectPushWorld> StartAsync(
        Action<MediatorTestServerOptions>? configure = null,
        MediatorNetwork? network = null,
        DirectPushInbox? inbox = null,
        DirectTakeRecordingStore? store = null,
        IMediationStore? mediation = null)
    {
        network ??= new MediatorNetwork();
        var ownsInbox = inbox is null;
        inbox ??= await DirectPushInbox.StartAsync(network);
        var endpoints = new DirectPushEndpoints();
        var time = new QaManualTimeProvider();
        store ??= new DirectTakeRecordingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            configure?.Invoke(o);
            o.Network = network;
            var more = o.Mediator;
            o.Mediator = m =>
            {
                m.DirectPush = true;
                more?.Invoke(m);
            };
            var services = o.ConfigureServices;
            o.ConfigureServices = s =>
            {
                s.AddSingleton<IRecipientEndpointDirectory>(endpoints);
                s.AddSingleton<TimeProvider>(time);
                s.AddSingleton<IMessageStore>(store);
                if (mediation is not null)
                    s.AddSingleton<IMediationStore>(mediation);
                services?.Invoke(s);
            };
        });
        return new DirectPushWorld(mediator, inbox, ownsInbox, endpoints, store, time);
    }

    /// <summary>A peer enrolled over HTTP with one recipient DID reached through the mediator, and its endpoint at <c>/{label}</c>.</summary>
    public async Task<(TestPeer Peer, TestPeer Recipient, InboxPath Endpoint)> EnrolAsync(string label)
    {
        var peer = await Mediator.NewPeerAsync();
        var recipient = await Mediator.NewRecipientAsync();
        await Mediator.EnrolAsync(peer, recipient.Did);
        var endpoint = Inbox["/" + label];
        Endpoints.Set(peer.Did, endpoint.Endpoint);
        return (peer, recipient, endpoint);
    }

    /// <summary>How many records the store holds for <paramref name="peerDid"/>'s grant now.</summary>
    public async Task<int> StoredAsync(string peerDid)
        => (await Store.Inner.GetStatusAsync(peerDid, null, Mediator.Clock.Now, CancellationToken.None)).MessageCount;

    /// <summary>Wait until <paramref name="count"/> pushes ended with <paramref name="outcome"/> (the record the assertion reads), then for the workers to be idle.</summary>
    public async Task OutcomesAsync(string outcome, int count)
    {
        await MediatorTestServer.WaitUntilAsync(() => Mediator.PushOutcome(outcome) >= count, $"{count} push(es) ended {outcome}");
        await Mediator.PushesIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Mediator.DisposeAsync();
        if (_ownsInbox)
            await Inbox.DisposeAsync();
    }
}
