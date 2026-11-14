using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>A grant of a <see cref="LivePushWorld"/>: a peer with real keys and the one recipient DID registered under it.</summary>
internal sealed record LivePushGrant(PeerIdentity Identity, string Recipient)
{
    public string Peer => Identity.Did;
}

/// <summary>
/// A container (<c>AddMediator()</c>, no generic host) whose routing DID holds real keys, with grants
/// minted on demand and live sessions turned on through the real protocol dispatcher — the vehicle
/// for every test of the push workers, whose only push is the live one.
/// </summary>
internal sealed class LivePushWorld : IAsyncDisposable
{
    private LivePushWorld(PipelinePeers peers, PeerIdentity mediator)
    {
        Peers = peers;
        Mediator = mediator;
    }

    public PipelinePeers Peers { get; }
    public PeerIdentity Mediator { get; }
    public PushFixture Fixture { get; private set; } = null!;

    public static async Task<LivePushWorld> StartAsync(
        Action<MediatorOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<DidCommOptions>? didComm = null)
    {
        var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var world = new LivePushWorld(peers, mediator);
        world.Fixture = PushFixture.Create(
            configure: configure,
            services: services,
            didComm: didComm,
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        return world;
    }

    /// <summary>A new peer, enrolled, with one recipient DID.</summary>
    public async Task<LivePushGrant> GrantAsync(string label)
    {
        var peer = await Peers.NewAsync();
        var recipient = $"did:example:live-{label}-recipient";
        await Fixture.EnrolAsync(peer.Did, recipient);
        return new LivePushGrant(peer, recipient);
    }

    /// <summary>A new live session of <paramref name="grant"/>'s peer, live delivery on; returns once the live-on wake-up was served.</summary>
    public async Task<LiveTestSession> LiveAsync(LivePushGrant grant)
    {
        var session = new LiveTestSession(grant.Peer, Mediator.Did);
        await Fixture.GoLiveAsync(session);
        return session;
    }

    /// <summary>A new grant with a live session.</summary>
    public async Task<(LivePushGrant Grant, LiveTestSession Session)> LiveGrantAsync(string label)
    {
        var grant = await GrantAsync(label);
        return (grant, await LiveAsync(grant));
    }

    /// <summary>Store a message for <paramref name="grant"/> as the sink does, without waking anything.</summary>
    public Task<StoredMessage> StoreAsync(LivePushGrant grant, byte index = 0, int size = 64)
        => Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(size, Seed(index)));

    /// <summary>What the forward pump does with a relayed forward: hand it to the mediator's sink.</summary>
    public Task ForwardAsync(string recipient, byte index, string? forwardId = null)
        => Fixture.Sink.AcceptAsync(
            new ForwardProcessingResult(recipient, PushFixture.Payload(64, Seed(index)), null, Delay: null),
            Context(forwardId ?? $"fwd-live-{index}"),
            CancellationToken.None);

    /// <summary>The ids a delivery-request on another connection would be handed now (under a lease of its own).</summary>
    public Task<IReadOnlyList<string>> PickupAsync(string peer) => PickupFromAsync(Fixture.Store, peer);

    public async Task<IReadOnlyList<string>> PickupFromAsync(IMessageStore store, string peer)
    {
        var now = Fixture.Time.GetUtcNow();
        var leased = await store.LeaseAsync(new MessageLeaseRequest(peer, null, 32, int.MaxValue, now, new MessageLease("live-pickup-" + Guid.NewGuid().ToString("N"), now + TimeSpan.FromMinutes(1))), CancellationToken.None);
        return leased.Select(m => m.Id).ToList();
    }

    public async Task<int> StatusAsync(string peer)
        => (await Fixture.Services.GetRequiredService<IMessageStore>().GetStatusAsync(peer, null, Fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount;

    /// <summary>The first payload byte of the message stored with <paramref name="i"/>.</summary>
    public static byte Seed(int i) => (byte)(7 + i);

    /// <summary>What a live session was pushed, in order.</summary>
    public static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();

    private ProtocolContext Context(string forwardId)
        => new(
            new UnpackResult(
                new Message { Id = forwardId, Type = ForwardConstants.ForwardTypeUri },
                new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
                Encrypted: true,
                Authenticated: false,
                NonRepudiation: false,
                AnonymousSender: true,
                ContentEncryption: null,
                KeyWrap: null,
                SignatureAlgorithm: null,
                SignerKid: null,
                SenderKid: null,
                RecipientKid: Mediator.Did + "#key-1",
                AllRecipientKids: new[] { Mediator.Did + "#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forwardId),
            Client: null,
            Fixture.Options,
            new InMemoryThreadStateStore());

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
        await Peers.DisposeAsync();
    }
}
