using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using DataProofsDotnet.Jose.Encryption;
using DidComm.Adapters.NetDid;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using DpJwkConversion = DataProofsDotnet.Jose.JwkConversion;

namespace DidComm.Samples.Extensibility;

/// <summary>
/// The three extension points, end to end (PRD §14.3 sample 08, tasks Y/Z): (1) a custom
/// <c>ISecretsResolver</c> modeling a mock KMS whose keys are non-extractable — they sign and
/// derive through <c>IOpaqueKeyResolver</c> operation handles without ever surfacing a private
/// scalar (FR-SEC-01/06); (2) the net-did <c>IKeyStore</c>→<c>ISecretsResolver</c> bridge
/// (<c>NetDidKeyStoreSecretsResolver</c>), including the <c>kidToAlias</c> constructor mapping
/// for stores that do not alias keys by DID URL (FR-SEC-04); and (3) a custom
/// <c>IDidCommTransport</c> registered through DI and chosen by the transport router purely by
/// URI scheme (FR-TRN-01). Each half deliberately uses a different flavor of the DI surface:
/// the generic <c>UseSecretsResolver&lt;T&gt;()</c> overload, the instance overload, and the
/// builder's raw <c>Services</c> collection.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02, no process spawn, fully offline).
/// </summary>
public static class Program
{
    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Extensibility failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole tour, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // ── Bob: a conventional agent, and the second DI flavor ─────────────────────────
        // Bob's container uses the INSTANCE overload — UseSecretsResolver(resolver) — with the
        // in-memory resolver samples use everywhere. He is the counterparty every extension
        // below talks to, proving each custom seam interoperates with a stock agent.
        var bobSecrets = new InMemorySecretsResolver();
        var bobServices = new ServiceCollection();
        bobServices.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(bobSecrets));
        await using var bobSp = bobServices.BuildServiceProvider();
        var bob = await PeerIdentityFactory.CreateAsync(
            bobSp.GetRequiredService<IDidManager>(),
            bobSp.GetRequiredService<IKeyGenerator>(),
            bobSp.GetRequiredService<ICryptoProvider>());
        foreach (var key in bob.Privates)
            bobSecrets.Add(key);
        var bobClient = bobSp.GetRequiredService<DidCommClient>();

        // ── Alice: the mock-KMS container, and the first DI flavor ──────────────────────
        // Alice's container registers her resolver by TYPE — UseSecretsResolver<T>() — so DI
        // constructs it. Because MockKmsSecretsResolver ALSO implements IOpaqueKeyResolver,
        // the same builder call surfaces that capability automatically (FR-SEC-06), and the
        // facade will route signing and ECDH through the KMS's opaque handles.
        // The container also registers a custom transport for section 3: the real registration
        // surface for transports is the builder's Services collection —
        // AddSingleton<IDidCommTransport, T>() — which is exactly what the packaged
        // UseHttpTransport()/UseWebSocketTransport() extensions do under the hood.
        var aliceServices = new ServiceCollection();
        aliceServices.AddSingleton<MemoryQueueTransport>();
        aliceServices.AddSingleton<IDidCommTransport>(sp => sp.GetRequiredService<MemoryQueueTransport>());
        aliceServices.AddDidComm(b => b
            .UseNetDidResolver()
            .UseSecretsResolver<MockKmsSecretsResolver>());
        await using var aliceSp = aliceServices.BuildServiceProvider();

        var alice = await SectionOneMockKmsAsync(narrator, aliceSp, bob, bobClient);
        await SectionTwoKeyStoreBridgeAsync(narrator, bob, bobClient);
        await SectionThreeCustomTransportAsync(narrator, aliceSp, alice, bob, bobClient);
    }

    // ── 1. A custom ISecretsResolver: the mock KMS ──────────────────────────────────────

    private static async Task<RawIdentity> SectionOneMockKmsAsync(
        Narrator narrator, ServiceProvider aliceSp, PeerIdentity bob, DidCommClient bobClient)
    {
        narrator.Section("1", "A custom ISecretsResolver — the mock KMS (FR-SEC-01/06)");

        // The library holds no keys (DD-02); it asks whatever ISecretsResolver you register.
        // DI constructed the KMS (generic overload); resolve it back and confirm the container
        // surfaced the SAME singleton as both contracts — that is what lets the facade find
        // the opaque handles.
        var kms = (MockKmsSecretsResolver)aliceSp.GetRequiredService<ISecretsResolver>();
        var opaque = aliceSp.GetRequiredService<IOpaqueKeyResolver>();
        narrator.Value("ISecretsResolver is the KMS", true);
        narrator.Value("IOpaqueKeyResolver is the SAME instance", ReferenceEquals(kms, opaque));

        // Mint Alice's did:peer:2 keeping the raw key pairs, and enroll them in the KMS. In a
        // real deployment this is your HSM/KMS provisioning step.
        var alice = await MintRawIdentityAsync(aliceSp);
        kms.Enroll(alice.KeyAgreementKid, alice.KeyAgreementPair);
        kms.Enroll(alice.SigningKid, alice.SigningPair);
        narrator.Step($"Enrolled two keys for {Trunc(alice.Did, 60)}");

        // The custody invariant, visible: lookups serve PUBLIC-only JWKs. No 'd', ever.
        var surfaced = await kms.FindAsync(alice.KeyAgreementKid);
        narrator.Value("FindAsync → Kty/Crv", $"{surfaced?.Kty}/{surfaced?.Crv}");
        narrator.Value("FindAsync → D", surfaced?.D ?? "<null> (private scalar never leaves the KMS)");
        narrator.Value("FindAsync(unknown kid)", await kms.FindAsync("did:example:nobody#key-1") is null ? "null (not held)" : "held");
        var present = await kms.FindPresentAsync(new[] { alice.KeyAgreementKid, "did:example:nobody#key-1" });
        narrator.Value("FindPresentAsync filters to held kids", present.Count == 1 && present[0] == alice.KeyAgreementKid);

        // The opaque handles, called explicitly. ResolveSignerAsync returns a signer that
        // signs INSIDE the KMS boundary; ResolveKeyAgreementAsync returns an ECDH handle that
        // derives the shared secret Z inside it. The JOSE layer only ever sees the results.
        narrator.Step("Call the opaque handles directly (what the facade does internally).");
        var signer = await kms.ResolveSignerAsync(alice.SigningKid);
        var signature = await signer!.SignAsync(Encoding.UTF8.GetBytes("signed without extracting the key"));
        narrator.Value("ResolveSignerAsync signature bytes", signature.Length);

        var ecdh = await kms.ResolveKeyAgreementAsync(alice.KeyAgreementKid);
        narrator.Value("ResolveKeyAgreementAsync → Crv", ecdh!.Crv);
        var ephemeral = new DefaultKeyGenerator().Generate(KeyType.X25519);
        var sharedSecret = await ecdh.DeriveAsync(ephemeral.PublicKey);
        narrator.Value("DeriveAsync shared-secret bytes", sharedSecret.Length);
        narrator.Value("Signing kid resolves no ECDH handle", await kms.ResolveKeyAgreementAsync(alice.SigningKid) is null);

        // Now the same KMS drives the full facade: sign-then-encrypt out, authcrypt back in.
        // Both directions run their private-key math through the opaque handles.
        narrator.Step("Drive the facade through the KMS: Alice → Bob, sign-then-encrypt.");
        var aliceClient = aliceSp.GetRequiredService<DidCommClient>();
        var outbound = await aliceClient.PackEncryptedAsync(
            NewBasicMessage(alice.Did, bob.Did, "Signed and encrypted by a KMS that never exposes keys."),
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did, SignFrom: alice.Did));
        var bobView = await bobClient.UnpackAsync(outbound.Message);
        narrator.Value("Bob sees Authenticated", bobView.Authenticated);
        narrator.Value("Bob sees NonRepudiation", bobView.NonRepudiation);
        narrator.Value("SenderKid is Alice's KMS key", bobView.SenderKid == alice.KeyAgreementKid);

        narrator.Step("And the receive path: Bob → Alice, decrypted through the opaque ECDH handle.");
        var inbound = await bobClient.PackEncryptedAsync(
            NewBasicMessage(bob.Did, alice.Did, "Round trip complete."),
            new PackEncryptedOptions(Recipients: new[] { alice.Did }, From: bob.Did));
        var aliceView = await aliceClient.UnpackAsync(inbound.Message);
        narrator.Value("Alice unpacked content", aliceView.Message.Body?["content"]?.GetValue<string>());
        narrator.Value("Alice's receive was authenticated", aliceView.Authenticated);
        return alice;
    }

    // ── 2. The net-did IKeyStore bridge ─────────────────────────────────────────────────

    private static async Task SectionTwoKeyStoreBridgeAsync(Narrator narrator, PeerIdentity bob, DidCommClient bobClient)
    {
        narrator.Section("2", "The net-did IKeyStore bridge — NetDidKeyStoreSecretsResolver (FR-SEC-04)");

        // An app already keeping keys in a NetCrypto IKeyStore writes NO resolver at all: the
        // shipped bridge adapts the store directly. Carol's keys go into a keystore under
        // human-friendly aliases — deliberately NOT the DID-URL kids — to show the
        // kidToAlias constructor parameter earning its keep.
        // Mint Carol first (throwaway container) — the bridge's alias map needs her kids
        // before her own container can be composed around it.
        await using var mintSp = BuildMintContainer();
        var carol = await MintRawIdentityAsync(mintSp);

        var store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        await store.ImportAsync("carol-key-agreement", carol.KeyAgreementPair);
        await store.ImportAsync("carol-signing", carol.SigningPair);
        narrator.Step("Imported Carol's key pairs under store aliases, not DID URLs.");

        // kid → alias: the bridge consults this map on every lookup, so a store keyed by
        // 'carol-signing' serves the DID URL 'did:peer:2...#key-2' transparently.
        var aliasByKid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [carol.KeyAgreementKid] = "carol-key-agreement",
            [carol.SigningKid] = "carol-signing",
        };
        var bridge = new NetDidKeyStoreSecretsResolver(store, kid => aliasByKid.GetValueOrDefault(kid, kid));
        narrator.Value("Bridge built with kidToAlias mapping", aliasByKid.Count + " entries");

        // The instance overload again, this time with the bridge — which implements
        // IOpaqueKeyResolver, so the one call wires the opaque path too.
        var carolServices = new ServiceCollection();
        carolServices.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(bridge));
        await using var carolSp = carolServices.BuildServiceProvider();
        narrator.Value("Container surfaced the bridge as IOpaqueKeyResolver",
            ReferenceEquals(carolSp.GetRequiredService<IOpaqueKeyResolver>(), bridge));

        // Same custody invariant as the KMS: public-only JWKs out of FindAsync…
        var surfaced = await bridge.FindAsync(carol.KeyAgreementKid);
        narrator.Value("Bridge FindAsync → Crv", surfaced?.Crv);
        narrator.Value("Bridge FindAsync → D", surfaced?.D ?? "<null> (keystore-held)");
        // …and held-ness answered from the store's alias list, THROUGH the mapping.
        var present = await bridge.FindPresentAsync(new[] { carol.SigningKid, "did:example:nobody#key-1" });
        narrator.Value("FindPresentAsync via kidToAlias", present.Count == 1 && present[0] == carol.SigningKid);

        // The facade neither knows nor cares that a keystore is behind the resolver.
        narrator.Step("Carol signs-then-encrypts to Bob through the keystore.");
        var packed = await carolSp.GetRequiredService<DidCommClient>().PackEncryptedAsync(
            NewBasicMessage(carol.Did, bob.Did, "Brought to you by an IKeyStore and one adapter."),
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: carol.Did, SignFrom: carol.Did));
        var bobView = await bobClient.UnpackAsync(packed.Message);
        narrator.Value("Bob sees Authenticated", bobView.Authenticated);
        narrator.Value("Bob sees NonRepudiation", bobView.NonRepudiation);
        narrator.Note("Write a resolver when your KMS speaks its own API; use the bridge when keys already live in a NetCrypto IKeyStore. Either way: no keys in the library (DD-02).");
    }

    // ── 3. A custom IDidCommTransport ───────────────────────────────────────────────────

    private static async Task SectionThreeCustomTransportAsync(
        Narrator narrator, ServiceProvider aliceSp, RawIdentity alice, PeerIdentity bob, DidCommClient bobClient)
    {
        narrator.Section("3", "A custom IDidCommTransport, registered through DI (FR-TRN-01)");

        // A transport is three members: Scheme, CanHandle, SendAsync. Alice's container
        // registered MemoryQueueTransport (below) as an IDidCommTransport singleton at
        // composition time, so the TransportRouter inside her facade already knows it —
        // SendAsync picks it the moment an endpoint URI matches the scheme.
        var transport = aliceSp.GetRequiredService<MemoryQueueTransport>();
        var aliceClient = aliceSp.GetRequiredService<DidCommClient>();
        narrator.Value("Registered scheme", transport.Scheme);

        narrator.Step("SendAsync to a memq:// endpoint — the router matches by scheme.");
        var sent = await aliceClient.SendAsync(
            NewBasicMessage(alice.Did, bob.Did, "Delivered over a transport invented in this file."),
            new SendOptions(
                Recipients: new[] { bob.Did },
                From: alice.Did,
                ServiceEndpointOverride: new Uri("memq://bob-inbox")));
        narrator.Value("EndpointUsed", sent.EndpointUsed);
        narrator.Value("Transport accepted", sent.Transport.Accepted);
        narrator.Value("Queue depth", transport.Delivered.Count);
        narrator.Value("Delivered media type", transport.Delivered[0].MediaType);

        // The bytes the queue carried are a normal packed envelope; Bob unpacks them exactly
        // as if they had arrived over HTTP.
        var bobView = await bobClient.UnpackAsync(Encoding.UTF8.GetString(transport.Delivered[0].Payload.Span));
        narrator.Value("Bob unpacked content", bobView.Message.Body?["content"]?.GetValue<string>());

        // Scheme dispatch cuts both ways: this container registered ONLY memq, so an https
        // endpoint has no taker and the send is refused with the scheme named.
        narrator.Step("An endpoint scheme no registered transport handles is refused.");
        try
        {
            await aliceClient.SendAsync(
                NewBasicMessage(alice.Did, bob.Did, "This one goes nowhere."),
                new SendOptions(
                    Recipients: new[] { bob.Did },
                    From: alice.Did,
                    ServiceEndpointOverride: new Uri("https://agents.example/inbox")));
            narrator.Value("https send", "UNEXPECTED — not refused");
        }
        catch (TransportException ex)
        {
            narrator.Value("https send", $"refused (TransportException: {Trunc(ex.Message, 80)})");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>A minimal container used only to mint DIDs (net-did's manager + key services).</summary>
    private static ServiceProvider BuildMintContainer()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(new InMemorySecretsResolver()));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Mint a fresh <c>did:peer:2</c> like <see cref="PeerIdentityFactory"/> does, but keep the
    /// raw <see cref="KeyPair"/>s so they can be enrolled in a KMS or imported into an
    /// <see cref="IKeyStore"/> instead of being converted to private JWKs.
    /// </summary>
    private static async Task<RawIdentity> MintRawIdentityAsync(IServiceProvider sp)
    {
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var manager = sp.GetRequiredService<IDidManager>();

        var kxPair = keyGen.Generate(KeyType.X25519);
        var authPair = keyGen.Generate(KeyType.Ed25519);

        var created = await manager.CreateAsync(new DidPeerCreateOptions
        {
            Numalgo = PeerNumalgo.Two,
            Keys = new[]
            {
                new PeerKeyPurpose(new KeyPairSigner(kxPair, crypto), PeerPurpose.KeyAgreement),
                new PeerKeyPurpose(new KeyPairSigner(authPair, crypto), PeerPurpose.Authentication),
            },
        });
        var did = created.Did.Value
            ?? throw new InvalidOperationException("DID manager returned a DID with no Value.");

        return new RawIdentity(
            did,
            KidOf(created.DidDocument, did, kxPair),
            kxPair,
            KidOf(created.DidDocument, did, authPair),
            authPair);
    }

    private static string KidOf(DidDocument doc, string did, KeyPair pair)
    {
        var match = doc.VerificationMethod?.FirstOrDefault(vm =>
                string.Equals(vm.PublicKeyMultibase, pair.MultibasePublicKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Key pair is not represented in the resolved DID Document for {did}.");
        // numalgo 2 emits relative ids like "#key-1"; the envelope layer keys by absolute DID URL.
        return match.Id.StartsWith('#') ? did + match.Id : match.Id;
    }

    private static Message NewBasicMessage(string from, string to, string content) => new MessageBuilder()
        .WithType("https://didcomm.org/basicmessage/2.0/message")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(new JsonObject { ["content"] = content })
        .Build();

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}

/// <summary>An identity whose private keys exist only as raw pairs inside a KMS or keystore, addressed by kid.</summary>
/// <param name="Did">The minted <c>did:peer:2</c>.</param>
/// <param name="KeyAgreementKid">Absolute DID URL of the X25519 key-agreement key.</param>
/// <param name="KeyAgreementPair">The X25519 pair (held by the KMS/keystore, never the library).</param>
/// <param name="SigningKid">Absolute DID URL of the Ed25519 authentication key.</param>
/// <param name="SigningPair">The Ed25519 pair (held by the KMS/keystore, never the library).</param>
public sealed record RawIdentity(
    string Did,
    string KeyAgreementKid,
    KeyPair KeyAgreementPair,
    string SigningKid,
    KeyPair SigningPair);

/// <summary>
/// A mock KMS implementing BOTH resolver contracts (FR-SEC-01 + FR-SEC-06). The
/// <c>ISecretsResolver</c> half answers selection questions — which kids do I hold, and what
/// are their public shapes — while the <c>IOpaqueKeyResolver</c> half performs the two
/// private-key operations (JWS signing, ECDH key agreement) inside the "KMS boundary". The
/// private scalars live in this class's vault and are never returned to a caller: the same
/// shape a Vault / HSM / cloud-KMS adapter takes, with a dictionary standing in for the
/// vendor SDK.
/// </summary>
public sealed class MockKmsSecretsResolver : ISecretsResolver, IOpaqueKeyResolver
{
    private readonly Dictionary<string, KeyPair> _vault = new(StringComparer.Ordinal);
    private readonly ICryptoProvider _crypto = new DefaultCryptoProvider();

    /// <summary>Enroll a key pair under its DID-URL kid (the KMS provisioning step).</summary>
    /// <param name="kid">Absolute DID URL identifying the key.</param>
    /// <param name="pair">The raw pair; held privately by the KMS from here on.</param>
    public void Enroll(string kid, KeyPair pair)
    {
        ArgumentException.ThrowIfNullOrEmpty(kid);
        ArgumentNullException.ThrowIfNull(pair);
        _vault[kid] = pair;
    }

    /// <summary>Serve the PUBLIC JWK shape only — no <c>d</c> member, ever (the custody invariant).</summary>
    /// <param name="kid">Key identifier to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
        => Task.FromResult(_vault.TryGetValue(kid, out var pair)
            ? DpJwkConversion.ToPublicJwk(pair.KeyType, pair.PublicKey, kid)
            : null);

    /// <summary>Answer "which of these do I hold?" in one query (multi-recipient unpacks stay cheap).</summary>
    /// <param name="kids">Candidate kids.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(kids.Where(_vault.ContainsKey).ToArray());

    /// <summary>An opaque signer for Ed25519 keys: signatures are produced inside the KMS boundary.</summary>
    /// <param name="kid">Key identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<ISigner?> ResolveSignerAsync(string kid, CancellationToken ct = default)
        => Task.FromResult<ISigner?>(_vault.TryGetValue(kid, out var pair) && pair.KeyType == KeyType.Ed25519
            ? new KeyPairSigner(pair, _crypto)
            : null);

    /// <summary>An opaque ECDH handle for X25519 keys: the shared secret Z is derived inside the KMS boundary.</summary>
    /// <param name="kid">Key identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IEcdhKey?> ResolveKeyAgreementAsync(string kid, CancellationToken ct = default)
        => Task.FromResult<IEcdhKey?>(_vault.TryGetValue(kid, out var pair) && pair.KeyType == KeyType.X25519
            ? new MockKmsEcdhKey(pair, _crypto)
            : null);

    /// <summary>
    /// The ECDH handle: self-describing curve + one derive callback. The JOSE layer feeds it
    /// peer public keys and receives raw shared secrets; everything after Z is public-data
    /// math that stays outside the KMS.
    /// </summary>
    private sealed class MockKmsEcdhKey : IEcdhKey
    {
        private readonly KeyPair _pair;
        private readonly ICryptoProvider _crypto;

        public MockKmsEcdhKey(KeyPair pair, ICryptoProvider crypto)
        {
            _pair = pair;
            _crypto = crypto;
        }

        public string Crv => "X25519";

        public ValueTask<byte[]> DeriveAsync(ReadOnlyMemory<byte> peerPublicKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<byte[]>(
                _crypto.DeriveSharedSecret(KeyType.X25519, _pair.PrivateKey, peerPublicKey.Span));
        }
    }
}

/// <summary>
/// A complete custom transport (FR-TRN-01): bind a scheme, accept matching endpoints, move
/// the bytes. This one appends to an in-memory queue; a real one would speak Bluetooth,
/// libp2p, a message bus — anything that can carry an opaque payload. Transports are
/// delivery-only: they report acceptance, never a protocol reply (FR-TRN-03).
/// </summary>
public sealed class MemoryQueueTransport : IDidCommTransport
{
    /// <summary>Everything "delivered" so far — the sample reads it back as the recipient.</summary>
    public List<TransportRequest> Delivered { get; } = new();

    /// <inheritdoc />
    public string Scheme => "memq";

    /// <inheritdoc />
    public bool CanHandle(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        Delivered.Add(request);
        return Task.FromResult(new TransportResult(Accepted: true, HttpStatusCode: null));
    }
}
