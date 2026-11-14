using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Transports;
using DidComm.Mediator;
using DidComm.Mediator.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using DidComm.Transports.ReturnRoute;
using DidComm.Transports.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// What the mediator received of the pickup client's requests, through a protocol observer on the
/// mediator's own dispatcher: the type and the <c>created_time</c> of every Message Pickup message,
/// so a test can show each one was stamped from the client's injected clock by the send path
/// (FR-RR-09), never by hand.
/// </summary>
internal sealed class PickupRequestObserver : IProtocolObserver
{
    private readonly ConcurrentQueue<ObservedPickup> _seen = new();

    public string? ProtocolUriFilter => MessagePickup.ProtocolUri;

    public IReadOnlyList<ObservedPickup> Seen => _seen.ToArray();

    public Task OnMessageReceivedAsync(InboundObservation observation, CancellationToken ct)
    {
        var message = observation.Message;
        bool? live = MessagePickup.TryParseLiveDeliveryChange(message, out var on) ? on : null;
        _seen.Enqueue(new ObservedPickup(message.Type, message.CreatedTime, message.From, live));
        return Task.CompletedTask;
    }

    /// <summary>The pickup messages observed from <paramref name="from"/>, by kind (<c>live-on</c>, <c>live-off</c>, the message name otherwise).</summary>
    public IReadOnlyList<string> KindsFrom(string from) => Seen.Where(s => s.From == from).Select(s => s.Kind).ToArray();

    /// <summary>Wait until at least <paramref name="count"/> pickup messages were observed.</summary>
    public Task WaitForAsync(int count) => MediatorTestServer.WaitUntilAsync(() => _seen.Count >= count, $"the mediator observed {count} pickup message(s)");

    /// <summary>Wait until at least <paramref name="count"/> pickup messages from <paramref name="from"/> were observed (each one already answered).</summary>
    public Task WaitForAsync(string from, int count)
        => MediatorTestServer.WaitUntilAsync(() => _seen.Count(s => s.From == from) >= count, $"the mediator observed {count} pickup message(s) of the DID");
}

/// <summary>One Message Pickup message the mediator received.</summary>
/// <param name="Type">Its type.</param>
/// <param name="CreatedTime">Its <c>created_time</c>.</param>
/// <param name="From">Its <c>from</c>.</param>
/// <param name="Live">For a <c>live-delivery-change</c>, the requested state.</param>
internal sealed record ObservedPickup(string Type, long? CreatedTime, string? From, bool? Live)
{
    public string Kind => Live switch
    {
        true => "live-on",
        false => "live-off",
        null => Type[(Type.LastIndexOf('/') + 1)..],
    };
}

/// <summary>Shared steps of the #96 pickup end-to-end tests.</summary>
internal static class PickupE2E
{
    /// <summary>Start a mediator whose dispatcher reports the pickup requests it receives to <paramref name="observer"/>.</summary>
    public static Task<MediatorTestServer> StartMediatorAsync(PickupRequestObserver observer, Action<MediatorTestServerOptions>? configure = null)
        => MediatorTestServer.StartAsync(o =>
        {
            configure?.Invoke(o);
            var post = o.PostConfigureServices;
            o.PostConfigureServices = services =>
            {
                post?.Invoke(services);
                services.AddSingleton<IProtocolObserver>(observer);
            };
        });

    /// <summary>
    /// Enrol: Bob (whose keys the harness holds) asks <paramref name="mediator"/> for mediation, a
    /// recipient DID is minted with the service entry <c>DidCommServiceBuilder.ForMediation(grant)</c>
    /// builds, and Bob registers it. The enrolment is hand-rolled over HTTP (the #95 helpers); the
    /// pickup under test is the harness's.
    /// </summary>
    public static async Task<(TestPeer Bob, TestPeer Recipient)> EnrolAsync(MediatorTestServer mediator, MediatorClientHarness harness)
    {
        var bob = await harness.NewPeerAsync();
        var routingDids = await mediator.RequestMediationAsync(bob);
        var recipient = await NewRecipientForAsync(mediator, harness, new MediationGrant(mediator.Did, routingDids));
        await mediator.AddRecipientsAsync(bob, new[] { recipient.Did });
        return (bob, recipient);
    }

    /// <summary>A DID whose service is <c>ForMediation(grant)</c>, its keys held by the harness.</summary>
    public static async Task<TestPeer> NewRecipientForAsync(MediatorTestServer mediator, MediatorClientHarness harness, MediationGrant grant)
    {
        var service = DidCommServiceBuilder.ForMediation(grant).Build();
        var recipient = mediator.Own(TestPeer.Create(await MediatorCast.MintWithServiceEntryAsync(service)));
        harness.Hold(recipient);
        return recipient;
    }

    /// <summary>Alice sends a note to <paramref name="recipient"/> with <c>SendAsync</c> (one forward for the mediator, FR-ROUTE-04) and the mediator stores it.</summary>
    public static async Task<Message> SendAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer recipient, string content = "hello")
    {
        var note = alice.Note(recipient.Did, content);
        (await alice.Client.SendAsync(note, new SendOptions(new[] { recipient.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await mediator.FlushAsync();
        return note;
    }

    /// <summary>A forward for <paramref name="recipient"/> whose payload is encrypted to someone else: it is stored for the recipient but cannot be opened with its keys.</summary>
    public static async Task PostUnopenableAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer recipient)
    {
        var stranger = mediator.Own(await MediatorCast.MintPeerAsync());
        var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, await alice.AuthcryptAsync(alice.Note(stranger.Did), stranger.Did));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }

    /// <summary>The checks every item that unpacked passes: Alice's note, authenticated, for the recipient.</summary>
    public static void ShouldBeAlicesNote(PickupItem item, MediatorSender alice, TestPeer recipient, Message note)
    {
        item.Error.Should().BeNull();
        var unpacked = item.Unpacked!;
        unpacked.Message.Id.Should().Be(note.Id);
        unpacked.Message.From.Should().Be(alice.Did);
        unpacked.Authenticated.Should().BeTrue("Alice's authcrypt survives the relay");
        unpacked.SenderKid.Should().StartWith(alice.Did + "#");
        unpacked.RecipientKid.Should().StartWith(recipient.Did + "#");
        MediatorRecipient.IsRandomQueueId(item.Id).Should().BeTrue("the item id is the mediator's queue id");
    }

    /// <summary>FR-RR-09: every pickup request the mediator received carries <c>created_time</c> = the client's clock, from <paramref name="from"/>.</summary>
    public static void ShouldAllBeStampedWith(PickupRequestObserver observer, string from, DateTimeOffset clientNow)
    {
        var requests = observer.Seen.Where(s => s.From == from).ToArray();
        requests.Should().NotBeEmpty();
        requests.Should().OnlyContain(s => s.CreatedTime == clientNow.ToUnixTimeSeconds(),
            "the send path stamps created_time from DidCommOptions.Clock on every return-route request (FR-RR-09)");
    }

    /// <summary>
    /// Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in
    /// time. A task that times out is still observed (<see cref="Observed"/>).
    /// </summary>
    public static Task<T> Within<T>(Task<T> task) => Observed(task).WaitAsync(MediatorTestServer.Timeout);

    /// <summary>Await <paramref name="task"/>, failing the test instead of hanging when it does not finish in time.</summary>
    public static Task<T> Within<T>(ValueTask<T> task) => Within(task.AsTask());

    /// <summary>Ask for the next live item without awaiting it yet; the move is <see cref="Observed"/>.</summary>
    public static Task<bool> MoveNext(IAsyncEnumerator<PickupItem> stream) => Observed(stream.MoveNextAsync().AsTask());

    /// <summary>
    /// <paramref name="task"/>, with a failure it ends with observed even when the test stops before
    /// awaiting it (a failed assertion, a timeout): the harness's disposal then fails a pending live
    /// move with an <see cref="ObjectDisposedException"/>, which would otherwise reach
    /// <see cref="TaskScheduler.UnobservedTaskException"/> and fail whichever later test watches it.
    /// </summary>
    private static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>The next live item (bounded in time).</summary>
    public static async Task<PickupItem> NextAsync(IAsyncEnumerator<PickupItem> stream)
    {
        (await Within(stream.MoveNextAsync())).Should().BeTrue("an item was expected");
        return stream.Current;
    }

    /// <summary>End a stream whose MoveNextAsync is pending: cancel its token, observe the cancellation, dispose.</summary>
    public static async Task StopAsync(IAsyncEnumerator<PickupItem> stream, Task<bool> pending, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => Within(pending)).Should().ThrowAsync<OperationCanceledException>();
        await stream.DisposeAsync();
    }
}

/// <summary>
/// A host <see cref="IMessageStore"/> around the product's in-memory store that loses the next
/// acknowledgement (answers "nothing removed"), so a message the client returned is delivered
/// again after its lease.
/// </summary>
internal sealed class AckLosingStore(InMemoryMessageStore inner) : IMessageStore
{
    private int _lose;

    public InMemoryMessageStore Inner { get; } = inner;

    public void LoseNextAcknowledgement() => Volatile.Write(ref _lose, 1);

    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct)
        => Interlocked.Exchange(ref _lose, 0) == 1 ? ValueTask.FromResult(0) : Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);

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

/// <summary>
/// A host <see cref="IMessageStore"/> around the product's in-memory store that can hold the next
/// status read at the door, so a <c>status-request</c> stays unanswered while the test does
/// something else.
/// </summary>
internal sealed class StatusParkingStore(InMemoryMessageStore inner) : IMessageStore
{
    private Parked? _next;

    public InMemoryMessageStore Inner { get; } = inner;

    public Parked ParkNextStatus()
    {
        var parked = new Parked();
        Interlocked.Exchange(ref _next, parked).Should().BeNull();
        return parked;
    }

    public async ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _next, null) is { } parked)
            await parked.ArriveAsync();
        return await Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
    }

    public ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct) => Inner.EnqueueAsync(message, lease, now, ct);
    public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
    public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
    public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
    public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
    public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
    public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
    public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
    public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);

    public sealed class Parked
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public void Release() => _gate.TrySetResult();

        public async Task ArriveAsync()
        {
            _arrived.TrySetResult();
            await _gate.Task.WaitAsync(MediatorTestServer.Timeout);
        }
    }
}

/// <summary>
/// A host that is both a mediator and a mediator's recipient (#96 acceptance): one TestServer
/// <c>WebApplication</c> with <c>AddDidComm(… AddMediator() … AddMediatorClient())</c>,
/// <c>MapDidCommMediator("/")</c> on <c>m2.test</c>, the HTTP transport over the test network, and the
/// WebSocket transport (<c>ReceiveInbound</c> + <c>UseInboundDispatch()</c>) connected to an upstream
/// mediator's TestServer, so the upstream mediator's answers reach this host's dispatcher — where its
/// own mediator handlers see them too.
/// </summary>
internal sealed class MediatorAndRecipientHost : IAsyncDisposable
{
    public const string Host = "m2.test";

    private MediatorAndRecipientHost(WebApplication app, PeerIdentity identity, CapturingLoggerProvider logs, TestClock clock)
    {
        App = app;
        Did = identity.Did;
        Identity = identity;
        Logs = logs;
        Clock = clock;
    }

    public WebApplication App { get; }

    /// <summary>The host's DID: its routing DID as a mediator, and the DID it picks up as upstream.</summary>
    public string Did { get; }

    public PeerIdentity Identity { get; }

    /// <summary>This host's own log lines (the upstream mediator's are in its <see cref="MediatorTestServer.Logs"/>).</summary>
    public CapturingLoggerProvider Logs { get; }

    public TestClock Clock { get; }

    public TestServer Server => App.GetTestServer();

    public PickupClient Pickup => App.Services.GetRequiredService<PickupClient>();

    public InMemoryMessageStore Store => (InMemoryMessageStore)App.Services.GetRequiredService<IMessageStore>();

    public ForwardHandler Forwards => App.Services.GetRequiredService<ForwardHandler>();

    public static async Task<MediatorAndRecipientHost> StartAsync(MediatorTestServer upstream)
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec($"http://{Host}/", Accept: new[] { "didcomm/v2" }));
        var secrets = new InMemorySecretsResolver();
        foreach (var jwk in identity.Privates)
            secrets.Add(jwk);
        // Its own log capture, so a test can tell this host's lines from the upstream mediator's.
        var logs = new CapturingLoggerProvider();
        var clock = upstream.Clock;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{Host}/"));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport(MediatorTestServer.ConfigureHttp);
            b.UseWebSocketTransport(o =>
            {
                o.AllowedSchemes = new[] { "ws" };
                o.MaxReconnectAttempts = 0;
                o.ReceiveInbound = true;
                o.WebSocketFactory = () => new TestServerClientWebSocket();
                o.Connect = (socket, uri, ct) => ((TestServerClientWebSocket)socket).ConnectAsync(upstream.Server, uri, ct);
            });
            b.UseInboundDispatch();
            b.AddMediator(m => m.CoordinateMediation.RoutingDids.Add(identity.Did));
            b.AddMediatorClient();
            b.Configure(o =>
            {
                o.Clock = () => clock.Now;
                foreach (var host in MediatorNetwork.KnownHosts)
                    o.OutboundEndpointPolicy.AllowedHosts.Add(host);
            });
        });
        builder.Services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(upstream.Network.CreateHandler);
        builder.Services.AddOptions<DidCommReceiveOptions>().Configure(o => o.AllowSameSocketReplies = false);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapDidCommMediator("/");
        upstream.Network.Add(app.GetTestServer());
        await app.StartAsync();
        return new MediatorAndRecipientHost(app, identity, logs, clock);
    }

    /// <summary>POST a packed envelope to the host's mediator endpoint.</summary>
    public async Task<HttpStatusCode> PostAsync(string packed)
    {
        using var client = Server.CreateClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await client.PostAsync("/", content);
        return response.StatusCode;
    }

    /// <summary>A Coordinate Mediation or pickup request from <paramref name="peer"/> to this host, stamped and authcrypted, posted; the answer unpacked.</summary>
    public async Task<Message> AskAsync(TestPeer peer, Message request)
    {
        request.CreatedTime = Clock.Now.ToUnixTimeSeconds();
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.All, thread: null);
        var packed = (await peer.Client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { Did }, From: peer.Did, Forward: false))).Message;
        using var client = Server.CreateClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await client.PostAsync("/", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await peer.Client.UnpackAsync(await response.Content.ReadAsStringAsync())).Message;
    }

    /// <summary>Enrol <paramref name="peer"/> at this host and register <paramref name="recipientDid"/>.</summary>
    public async Task EnrolAsync(TestPeer peer, string recipientDid)
    {
        CoordinateMediation.TryParseGrant(await AskAsync(peer, CoordinateMediation.CreateMediateRequest(peer.Did, Did)), out var routing).Should().BeTrue();
        routing.Should().Equal(Did);
        CoordinateMediation.TryParseRecipientUpdateResponse(
            await AskAsync(peer, CoordinateMediation.CreateRecipientUpdate(peer.Did, Did, new[] { new RecipientUpdate(recipientDid, RecipientUpdateAction.Add) })),
            out var results).Should().BeTrue();
        results!.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
