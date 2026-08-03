using System.Text.Json.Nodes;
using DidComm.Adapters.NetDid;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Rotation;
using DidComm.Resolution;
using DidComm.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetCrypto;
using NetDid.Core;
using NetDid.Core.Model;
using NetDid.Method.Peer;
using DpSig = DataProofsDotnet.Jose.Signing;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Owns the key-custody seam. The library never stores private keys; it asks the
/// <c>ISecretsResolver</c> you register. This section shows both ways to satisfy that
/// contract: writing your own resolver (a tiny in-memory "mock KMS" — the same shape a
/// Vault/HSM/cloud-KMS adapter takes), and reusing keys already held in a NetCrypto
/// <c>IKeyStore</c> via the ready-made <c>NetDidKeyStoreSecretsResolver</c> bridge — which
/// never exposes a private byte, yet still packs, signs, and unpacks.
/// </summary>
/// <remarks>
/// <para>
/// The contract is just two lookups: <c>FindAsync(kid)</c> returns the private JWK for one
/// key id (or <c>null</c> when not held — non-fatal on unpack, an error on pack), and
/// <c>FindPresentAsync(kids)</c> answers "which of these do I hold?" so a multi-recipient
/// unpack doesn't need N round-trips to your KMS.
/// </para>
/// <para>
/// The keystore bridge goes further: because <c>IKeyStore</c> exposes signing and ECDH as
/// operations rather than key bytes, the bridge surfaces public-only JWKs (watch <c>D</c>
/// come back null) and the facade routes the actual crypto through opaque handles — private
/// scalars never leave the store (FR-SEC-06).
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>Y</strong> (FR-SEC-01/04/06 — custom resolver + net-did IKeyStore bridge).
/// </para>
/// </remarks>
public static class Section_Y_CustomSecretsResolver
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("Y", "Custom ISecretsResolver (mock KMS) & the net-did IKeyStore bridge");

        var keyService = ctx.ServiceProvider.GetRequiredService<IDidKeyService>();

        // --- Half 1: your own resolver ------------------------------------------------
        // MockKmsSecretsResolver (below) is the whole contract: two lookups over whatever
        // backing your organization uses. Seed it with the shared identities' keys and drive
        // a full round-trip through a client built on it.
        ctx.Narrator.Step("Custom resolver: a dictionary-backed 'mock KMS' implementing the two-method contract.");
        var mockKms = new MockKmsSecretsResolver();
        foreach (var jwk in ctx.Alice.Privates) mockKms.Store(jwk);
        foreach (var jwk in ctx.Bob.Privates) mockKms.Store(jwk);

        // Hand-assembled clients can also take an ILogger<DidCommClient> to surface the same
        // structured diagnostics the DI-built client emits (redacted per NFR-04).
        var kmsClient = new DidCommClient(mockKms, keyService, new DidCommOptions(),
            NullLogger<DidCommClient>.Instance);
        var viaKms = await kmsClient.PackEncryptedAsync(
            NewMessage(ctx.Alice.Did, ctx.Bob.Did, "Keys served by the mock KMS."),
            new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did));
        var kmsUnpacked = await kmsClient.UnpackAsync(viaKms.Message);
        ctx.Narrator.Value("Mock-KMS round-trip Authenticated", kmsUnpacked.Authenticated);
        ctx.Narrator.Value("Mock-KMS FindAsync(unknown kid)", await mockKms.FindAsync("did:example:nobody#key-1") is null ? "null (not held — non-fatal on unpack)" : "held");

        // --- Half 2: the net-did IKeyStore bridge -------------------------------------
        // An app that already keeps keys in a NetCrypto IKeyStore doesn't write a resolver at
        // all: NetDidKeyStoreSecretsResolver adapts the store directly. Mint a fresh identity
        // whose key pairs go INTO a keystore (aliased by kid) instead of into a JWK map.
        ctx.Narrator.Step("Bridge: mint an identity whose keys live in a NetCrypto IKeyStore, aliased by kid.");
        var vault = await MintKeystoreIdentityAsync(ctx.ServiceProvider);
        var store = new InMemoryKeyStore(new DefaultKeyGenerator(), new DefaultCryptoProvider());
        await store.ImportAsync(vault.KeyAgreementKid, vault.KeyAgreementPair);
        await store.ImportAsync(vault.SigningKid, vault.SigningPair);
        var bridge = new NetDidKeyStoreSecretsResolver(store);
        ctx.Narrator.Value("Keystore identity", Truncate(vault.Did));

        // The custody invariant, visible: the bridge serves public-only JWKs — no 'd', ever.
        var surfaced = await bridge.FindAsync(vault.KeyAgreementKid);
        ctx.Narrator.Value("Bridge FindAsync -> Crv", surfaced?.Crv);
        ctx.Narrator.Value("Bridge FindAsync -> D", surfaced?.D ?? "<null> (private scalar never leaves the store)");

        // And yet the facade signs AND authcrypts through it: the crypto runs inside the
        // keystore via opaque handles (FR-SEC-06). Bob unpacks with the shared client.
        ctx.Narrator.Step("Sign-then-authcrypt through the keystore; Bob unpacks as usual.");
        var opaqueClient = new DidCommClient(bridge, keyService, new DidCommOptions());
        var viaStore = await opaqueClient.PackEncryptedAsync(
            NewMessage(vault.Did, ctx.Bob.Did, "Signed and encrypted without extracting a key."),
            new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: vault.Did, SignFrom: vault.Did));
        var bobView = await ctx.Client.UnpackAsync(viaStore.Message);
        ctx.Narrator.Value("Keystore round-trip Authenticated", bobView.Authenticated);
        ctx.Narrator.Value("Keystore round-trip NonRepudiation", bobView.NonRepudiation);
        ctx.Narrator.Value("SenderKid is the keystore identity", bobView.SenderKid?.StartsWith(vault.Did, StringComparison.Ordinal));

        // A resolver that cannot serve a kid the PACK side needs is an error, and it has a name:
        // SecretNotFoundException, carrying the exact kid that was missing — the string to hand
        // your KMS operator.
        ctx.Narrator.Step("Pack with a key the resolver does not hold ⇒ SecretNotFoundException.");
        var emptyKms = new MockKmsSecretsResolver();   // nothing stored
        var brokenClient = new DidCommClient(emptyKms, keyService, new DidCommOptions());
        try
        {
            _ = await brokenClient.PackEncryptedAsync(
                NewMessage(ctx.Alice.Did, ctx.Bob.Did, "This cannot be packed."),
                new PackEncryptedOptions(Recipients: new[] { ctx.Bob.Did }, From: ctx.Alice.Did));
            ctx.Narrator.Note("UNEXPECTED — the empty KMS packed a message.");
        }
        catch (SecretNotFoundException ex)
        {
            ctx.Narrator.Value("SecretNotFoundException.Kid (the key your KMS must serve)", Truncate(ex.Kid));
        }

        // Beyond FindAsync, the bridge also implements the opaque-key surface (IOpaqueKeyResolver):
        // it hands out operation handles instead of key bytes. ResolveKeyAgreementAsync returns the
        // ECDH handle the facade decrypts with; ResolveSignerAsync returns a JWS signer handle you
        // can use directly wherever the library accepts one — here, signing Section N's rotation
        // JWTs without the authentication key ever leaving the store.
        ctx.Narrator.Step("Opaque handles: ECDH + JWS signing without extracting a private key.");
        var ecdhHandle = await bridge.ResolveKeyAgreementAsync(vault.KeyAgreementKid);
        ctx.Narrator.Value("ResolveKeyAgreementAsync handle held", ecdhHandle is not null);
        var rawSigner = await bridge.ResolveSignerAsync(vault.SigningKid)
            ?? throw new InvalidOperationException("The keystore should hold the signing kid.");
        // A JwsSigner is just "an opaque signing operation + the kid it acts as".
        var signerHandle = new DpSig.JwsSigner(rawSigner, vault.SigningKid);
        var rotationClaims = new FromPriorClaims(
            Sub: ctx.Alice2.Did, Iss: vault.Did, Iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var viaHandle = await FromPriorBuilder.BuildAsync(rotationClaims, signerHandle);
        // The lifetime overload stamps exp = iat + lifetime in the same call (FR-ROT-05)...
        var bounded = await FromPriorBuilder.BuildAsync(rotationClaims, signerHandle, TimeSpan.FromMinutes(10));
        // ...and the termination form ("this DID retires, nothing replaces it") signs the same way.
        var farewell = await FromPriorBuilder.BuildTerminationAsync(
            vault.Did, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), signerHandle);
        ctx.Narrator.Value("Rotation JWT via keystore handle", viaHandle.Length > 0);
        ctx.Narrator.Value("Replay-bounded JWT via keystore handle", bounded.Length > 0);
        ctx.Narrator.Value("Termination JWT via keystore handle", farewell.Length > 0);

        ctx.Narrator.Note("Write a resolver when your KMS speaks its own API; use NetDidKeyStoreSecretsResolver when keys already live in a NetCrypto IKeyStore — either way the library holds no keys (DD-02).");
    }

    private static Message NewMessage(string from, string to, string content) => new MessageBuilder()
        .WithType("https://didcomm.org/basicmessage/2.0/message")
        .WithFrom(from)
        .WithTo(to)
        .WithBody(new JsonObject { ["content"] = content })
        .Build();

    private static string Truncate(string did) => did.Length <= 64 ? did : did[..61] + "…";

    /// <summary>
    /// The complete <see cref="ISecretsResolver"/> contract in ~20 lines — the same shape a
    /// Vault / HSM / cloud-KMS adapter takes, with the dictionary standing in for the vendor
    /// SDK call. <c>FindAsync</c> serves one kid; <c>FindPresentAsync</c> filters a candidate
    /// list so multi-recipient unpacks cost one query, not N (FR-SEC-01).
    /// </summary>
    private sealed class MockKmsSecretsResolver : ISecretsResolver
    {
        private readonly Dictionary<string, Jwk> _vault = new(StringComparer.Ordinal);

        public void Store(Jwk privateJwk) =>
            _vault[privateJwk.Kid ?? throw new ArgumentException("JWK 'kid' is required.", nameof(privateJwk))] = privateJwk;

        public Task<Jwk?> FindAsync(string kid, CancellationToken ct = default)
            => Task.FromResult(_vault.GetValueOrDefault(kid));

        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(kids.Where(_vault.ContainsKey).ToArray());
    }

    /// <summary>An identity whose private keys exist only as keystore entries, aliased by kid.</summary>
    private sealed record KeystoreIdentity(
        string Did,
        string KeyAgreementKid,
        KeyPair KeyAgreementPair,
        string SigningKid,
        KeyPair SigningPair);

    /// <summary>
    /// Mint a fresh <c>did:peer:2</c> like <see cref="DidComm.Samples.Shared.PeerIdentityFactory"/>
    /// does, but keep the raw <see cref="KeyPair"/>s so they can be imported into an
    /// <see cref="IKeyStore"/> instead of being converted to private JWKs.
    /// </summary>
    private static async Task<KeystoreIdentity> MintKeystoreIdentityAsync(IServiceProvider sp)
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

        return new KeystoreIdentity(
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
}
