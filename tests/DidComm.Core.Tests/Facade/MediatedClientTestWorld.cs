using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using DidComm.Tests.Envelopes;
using DidComm.Transports;
using NetCrypto;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Tests.Facade;

/// <summary>
/// Real envelopes for the #96 recipient-side facade entry points
/// (<see cref="DidCommClient.UnpackDeliveredAsync"/>, <see cref="DidCommClient.ResolveDirectEndpointAsync"/>)
/// and the snapshot overload of the return-route identity rule. Bob is the recipient (he also holds
/// Carol's keys: two local DIDs in one agent); Alice and Mallory are senders; the mediator rewraps
/// with a real <see cref="ForwardProcessor"/>; Prior signs a <c>from_prior</c> termination.
/// </summary>
internal sealed class MediatedClientTestWorld
{
    public const string Alice = "did:example:alice";
    public const string Bob = "did:example:bob";
    public const string Carol = "did:example:carol";
    public const string Mallory = "did:example:mallory";
    public const string Mediator = "did:example:mediator";
    public const string Prior = "did:example:prior";
    public const string AppType = "https://example.org/notes/1.0/note";

    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public TestKeyMaterial AliceKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Alice + "#x1");
    public TestKeyMaterial AliceSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Alice + "#s1");
    public TestKeyMaterial BobKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Bob + "#x1");
    public TestKeyMaterial BobSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Bob + "#s1");
    public TestKeyMaterial CarolKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Carol + "#x1");
    public TestKeyMaterial CarolSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Carol + "#s1");
    public TestKeyMaterial MalloryKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mallory + "#x1");
    public TestKeyMaterial MediatorKey { get; } = TestKeyMaterial.Generate(KeyType.X25519, Mediator + "#x1");
    public TestKeyMaterial PriorSigningKey { get; } = TestKeyMaterial.Generate(KeyType.Ed25519, Prior + "#s1");

    public MediatedClientTestWorld(params (string Did, DidCommServiceInfo[] Services)[] services)
    {
        Resolver = new SpyDidResolver(Docs());
        Services = new StubServiceResolver(services);
    }

    public SpyDidResolver Resolver { get; }

    public StubServiceResolver Services { get; }

    public CapturingRouter Router { get; } = new();

    /// <summary>Bob's options: the clock, no DNS lookups for the example hosts.</summary>
    public DidCommOptions BobOptions { get; } = new()
    {
        Clock = () => Now,
        OutboundEndpointPolicy = new OutboundEndpointPolicy { ResolveDnsNames = false },
    };

    /// <summary>Bob's facade: Bob's and Carol's keys, the stub service resolver and the capturing router.</summary>
    public DidCommClient BobClient(IServiceEndpointResolver? services = null, bool noResolver = false)
        => new(new SpySecretsResolver(BobKey.PrivateJwk, BobSigningKey.PrivateJwk, CarolKey.PrivateJwk, CarolSigningKey.PrivateJwk),
            new NetDidKeyService(Resolver), noResolver ? null : services ?? Services, Router, BobOptions, new JoseCryptoProvider());

    /// <summary>A facade holding every sender's keys: packs anything any of them would send.</summary>
    public DidCommClient Senders()
        => new(new SpySecretsResolver(AliceKey.PrivateJwk, AliceSigningKey.PrivateJwk, MalloryKey.PrivateJwk, BobSigningKey.PrivateJwk, CarolSigningKey.PrivateJwk, PriorSigningKey.PrivateJwk),
            new NetDidKeyService(Resolver), new DidCommOptions { Clock = () => Now });

    /// <summary>The mediator's facade and a processor in the given mode.</summary>
    public ForwardProcessor MediatorProcessor(RewrapMode mode = RewrapMode.ReanoncryptToNext)
    {
        var options = new DidCommOptions { Clock = () => Now };
        var keyService = new NetDidKeyService(Resolver);
        var client = new DidCommClient(new SpySecretsResolver(MediatorKey.PrivateJwk), keyService, options);
        return new ForwardProcessor(client, keyService, new ForwardProcessorOptions(Mode: mode), new StaticRecipientRoutingKeyProvider(null), options);
    }

    /// <summary>An application message from <paramref name="from"/> (or none) to <paramref name="to"/>.</summary>
    public static Message Note(string? from = Alice, string to = Bob, string text = "hello", long? expires = null)
    {
        var builder = new MessageBuilder().WithType(AppType).WithTo(to).WithBody(new JsonObject { ["text"] = text });
        if (from is not null)
            builder.WithFrom(from);
        if (expires is { } e)
            builder.WithExpiresTime(e);
        return builder.Build();
    }

    /// <summary>A forward to <paramref name="to"/> with <paramref name="next"/> and the given attachments.</summary>
    public static Message Forward(string to, string next, params Attachment[] attachments)
    {
        var builder = new MessageBuilder().WithType(ForwardConstants.ForwardTypeUri).WithTo(to).WithBody(new JsonObject { ["next"] = next });
        foreach (var attachment in attachments)
            builder.WithAttachment(attachment);
        return builder.Build();
    }

    /// <summary>An attachment carrying <paramref name="packed"/> as <c>data.json</c>.</summary>
    public static Attachment JsonAttachment(string packed)
        => new() { Id = Guid.NewGuid().ToString("N"), MediaType = ForwardConstants.PayloadMediaType, Data = new AttachmentData { Json = JsonNode.Parse(packed) } };

    /// <summary>An attachment carrying <paramref name="base64"/> as <c>data.base64</c> as it is.</summary>
    public static Attachment Base64Attachment(string base64)
        => new() { Id = Guid.NewGuid().ToString("N"), MediaType = ForwardConstants.PayloadMediaType, Data = new AttachmentData { Base64 = base64 } };

    public static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<string> AnoncryptAsync(Message message, string to = Bob)
        => (await Senders().PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }))).Message;

    public async Task<string> AuthcryptAsync(Message message, string from = Alice, string to = Bob, string? signFrom = null)
        => (await Senders().PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: from, SignFrom: signFrom))).Message;

    public async Task<string> AnoncryptSignedAsync(Message message, string signFrom, string to = Bob)
        => (await Senders().PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, SignFrom: signFrom))).Message;

    /// <summary>What a re-wrapping mediator puts in its queue for Bob: <paramref name="layers"/> self-addressed anoncrypt forward layers around <paramref name="innerPacked"/>.</summary>
    public async Task<string> RewrappedAsync(string innerPacked, int layers, string to = Bob)
    {
        var packed = innerPacked;
        for (var i = 0; i < layers; i++)
            packed = await AnoncryptAsync(ForwardMessage.Create(to, to, new[] { packed }), to);
        return packed;
    }

    /// <summary>The real rewrap: Alice's forward to the mediator for Bob, processed by the mediator in <see cref="RewrapMode.ReanoncryptToNext"/>.</summary>
    public async Task<string> MediatorRewrapAsync(string innerPacked, string next = Bob)
    {
        var forward = ForwardMessage.Create(Mediator, next, new[] { innerPacked });
        var toMediator = await AnoncryptAsync(forward, Mediator);
        var result = await MediatorProcessor().ProcessAsync(toMediator);
        return Encoding.UTF8.GetString(result.OnwardPacked);
    }

    private NetDid.Core.Model.DidDocument[] Docs() => new[]
    {
        LengthCap.DocWithKeys(Alice, new[] { AliceSigningKey.PublicJwk }, new[] { AliceKey.PublicJwk }),
        LengthCap.DocWithKeys(Bob, new[] { BobSigningKey.PublicJwk }, new[] { BobKey.PublicJwk }),
        LengthCap.DocWithKeys(Carol, new[] { CarolSigningKey.PublicJwk }, new[] { CarolKey.PublicJwk }),
        LengthCap.Doc(Mallory, keyAgreement: MalloryKey.PublicJwk),
        LengthCap.Doc(Mediator, keyAgreement: MediatorKey.PublicJwk),
        LengthCap.Doc(Prior, authentication: PriorSigningKey.PublicJwk),
    };

    /// <summary>Serves fixed service entries per DID and counts reads.</summary>
    internal sealed class StubServiceResolver(params (string Did, DidCommServiceInfo[] Services)[] entries) : IServiceEndpointResolver
    {
        private readonly Dictionary<string, IReadOnlyList<DidCommServiceInfo>> _map = entries.ToDictionary(e => e.Did, e => (IReadOnlyList<DidCommServiceInfo>)e.Services, StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _resolved = new();

        public IReadOnlyCollection<string> Resolved => _resolved;

        public Task<IReadOnlyList<DidCommServiceInfo>> ResolveAsync(string did, CancellationToken ct = default)
        {
            _resolved.Enqueue(did);
            return Task.FromResult(_map.TryGetValue(did, out var s) ? s : Array.Empty<DidCommServiceInfo>());
        }
    }

    /// <summary>Records every transport request and answers 202.</summary>
    internal sealed class CapturingRouter : ITransportRouter
    {
        public ConcurrentQueue<TransportRequest> Requests { get; } = new();

        public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: 202));
        }
    }
}
