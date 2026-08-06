using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using NetDid.Method.Key;
using NetDid.Method.Peer;
using JwkConversion = DataProofsDotnet.Jose.JwkConversion;

namespace DidComm.Samples.NetDidIntegration;

/// <summary>
/// The net-did integration story (PRD §14.3 sample 09, task AA): DID resolution is delegated
/// to net-did (DD-01), so this library mints and resolves <c>did:key</c> and <c>did:peer</c>
/// (numalgos 0 and 2) for free through <c>UseNetDidResolver()</c>. The tour mints all three
/// shapes, watches an Ed25519 <c>did:key</c> derive its X25519 keyAgreement key, messages
/// across method boundaries (a <c>did:key</c> sender authcrypting to a <c>did:peer:2</c>
/// recipient), drives the FR-API-05 <c>expires_time</c> check with an injected clock — no
/// real sleeps — and finishes on the deliberate <c>did:web</c> refusal:
/// <c>UnsupportedDidMethodException</c> from every entry point (FR-DID-06, DD-08).
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
            Console.Error.WriteLine($"NetDidIntegration failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole tour, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // UseNetDidResolver() is the whole integration: it registers net-did's composite
        // resolver (did:key + did:peer by default) and the NetDidKeyService adapter that turns
        // resolved documents into the JWKs the JOSE layer consumes (FR-DID-01).
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        await using var sp = services.BuildServiceProvider();

        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var keyService = sp.GetRequiredService<IDidKeyService>();
        var client = sp.GetRequiredService<DidCommClient>();

        var didKey = await SectionOneDidKeyAsync(narrator, manager, keyGen, crypto, keyService, secrets);
        await SectionTwoDidPeerZeroAsync(narrator, manager, keyGen, crypto, keyService, secrets, client, didKey);
        var didPeer2 = await SectionThreeAcrossMethodsAsync(narrator, manager, keyGen, crypto, secrets, client, didKey);
        await SectionFourClockAsync(narrator, sp, client, didKey, didPeer2);
        await SectionFiveDidWebAsync(narrator, keyService, client, didKey, didPeer2);
        await SectionSixAdapterAsync(narrator, sp, didKey, didPeer2);
    }

    // ── 1. did:key — one Ed25519 key, two capabilities ──────────────────────────────────

    private static async Task<string> SectionOneDidKeyAsync(
        Narrator narrator, IDidManager manager, IKeyGenerator keyGen, ICryptoProvider crypto,
        IDidKeyService keyService, InMemorySecretsResolver secrets)
    {
        narrator.Section("1", "Mint a did:key — Ed25519 in, X25519 keyAgreement derived (FR-DID-05)");

        // A did:key IS its public key: the DID string encodes the key, so resolution needs no
        // network and no registry. We mint from an existing Ed25519 pair (any ISigner works —
        // including HSM-backed ones) and let net-did derive the X25519 encryption key from it
        // via the birational Ed25519→X25519 map.
        var edPair = keyGen.Generate(KeyType.Ed25519);
        var created = await manager.CreateAsync(new DidKeyCreateOptions
        {
            KeyType = KeyType.Ed25519,
            ExistingKey = new KeyPairSigner(edPair, crypto),
            EnableEncryptionKeyDerivation = true, // the default, spelled out for the narrative
        });
        var did = created.Did.Value ?? throw new InvalidOperationException("did:key mint returned no DID.");
        narrator.Value("did:key", Trunc(did, 64));

        // Resolve it back through the SAME pipeline the facade uses on every pack/unpack and
        // print what each verification relationship carries.
        var authKeys = await keyService.GetVerificationMethodsAsync(did, VerificationRelationship.Authentication);
        var kaKeys = await keyService.GetVerificationMethodsAsync(did, VerificationRelationship.KeyAgreement);
        narrator.Step("Resolved verification methods (IDidKeyService.GetVerificationMethodsAsync):");
        foreach (var jwk in authKeys)
            narrator.Value("- authentication", $"crv={jwk.Crv} kid={Trunc(jwk.Kid, 72)}");
        foreach (var jwk in kaKeys)
            narrator.Value("- keyAgreement", $"crv={jwk.Crv} kid={Trunc(jwk.Kid, 72)}");
        narrator.Value("keyAgreement crv is X25519", kaKeys.Count == 1 && kaKeys[0].Crv == "X25519");

        // Prove the derivation is the real map, not decoration: derive X25519 from the same
        // Ed25519 pair locally and compare public keys with what the DID document advertises.
        var derived = keyGen.DeriveX25519FromEd25519(edPair);
        var derivedJwk = JwkConversion.ToPrivateJwk(derived, kaKeys[0].Kid!);
        narrator.Value("Locally-derived X25519 matches the DID document", derivedJwk.X == kaKeys[0].X);

        // The holder side: load both private halves so this did:key can sign AND decrypt.
        secrets.Add(JwkConversion.ToPrivateJwk(edPair, authKeys[0].Kid!));
        secrets.Add(derivedJwk);
        narrator.Note("One Ed25519 seed now serves signatures (authentication) and encryption (derived X25519 keyAgreement).");
        return did;
    }

    // ── 2. did:peer:0 — the inception-key variant ───────────────────────────────────────

    private static async Task SectionTwoDidPeerZeroAsync(
        Narrator narrator, IDidManager manager, IKeyGenerator keyGen, ICryptoProvider crypto,
        IDidKeyService keyService, InMemorySecretsResolver secrets, DidCommClient client, string didKey)
    {
        narrator.Section("2", "Mint a did:peer numalgo 0 — the inception-key variant");

        // Numalgo 0 is did:key's pattern under the did:peer method: one inception key encoded
        // directly in the DID string. Useful when a pairwise relationship starts from a single
        // signing key.
        var edPair = keyGen.Generate(KeyType.Ed25519);
        var created = await manager.CreateAsync(new DidPeerCreateOptions
        {
            Numalgo = PeerNumalgo.Zero,
            InceptionKeyType = KeyType.Ed25519,
            ExistingKey = new KeyPairSigner(edPair, crypto),
        });
        var did0 = created.Did.Value ?? throw new InvalidOperationException("did:peer:0 mint returned no DID.");
        narrator.Value("did:peer:0", Trunc(did0, 64));
        narrator.Value("Prefix is did:peer:0", did0.StartsWith("did:peer:0", StringComparison.Ordinal));

        var authKeys = await keyService.GetVerificationMethodsAsync(did0, VerificationRelationship.Authentication);
        foreach (var jwk in authKeys)
            narrator.Value("- authentication", $"crv={jwk.Crv} kid={Trunc(jwk.Kid, 72)}");

        // Put it to work: a signed (non-repudiable) envelope from the did:peer:0 identity,
        // verified on unpack through the same resolver pipeline.
        secrets.Add(JwkConversion.ToPrivateJwk(edPair, authKeys[0].Kid!));
        var signed = await client.PackSignedAsync(new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(did0)
            .WithTo(didKey)
            .WithBody(new JsonObject { ["content"] = "Signed by an inception key." })
            .Build(), signFrom: did0);
        var verified = await client.UnpackAsync(signed);
        narrator.Value("Signed envelope NonRepudiation", verified.NonRepudiation);
        narrator.Value("SignerKid belongs to did:peer:0", verified.SignerKid?.StartsWith(did0, StringComparison.Ordinal));
    }

    // ── 3. Messaging across DID-method boundaries ───────────────────────────────────────

    private static async Task<string> SectionThreeAcrossMethodsAsync(
        Narrator narrator, IDidManager manager, IKeyGenerator keyGen, ICryptoProvider crypto,
        InMemorySecretsResolver secrets, DidCommClient client, string didKey)
    {
        narrator.Section("3", "did:key sender → did:peer:2 recipient (methods interoperate)");

        // The recipient is a conventional did:peer:2 (numalgo 2: inline keys AND services).
        // Nothing about pack/unpack cares that sender and recipient use different DID methods —
        // both resolve through the same composite resolver into the same JWK shapes.
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var key in bob.Privates)
            secrets.Add(key);
        narrator.Value("did:peer:2 recipient", Trunc(bob.Did, 64));

        var packed = await client.PackEncryptedAsync(new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(didKey)
            .WithTo(bob.Did)
            .WithBody(new JsonObject { ["content"] = "Hello across the method boundary." })
            .Build(), new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: didKey));
        var received = await client.UnpackAsync(packed.Message);

        narrator.Value("Authenticated (authcrypt)", received.Authenticated);
        narrator.Value("SenderKid is a did:key kid", received.SenderKid?.StartsWith("did:key:", StringComparison.Ordinal));
        narrator.Value("RecipientKid is a did:peer kid", received.RecipientKid?.StartsWith("did:peer:2", StringComparison.Ordinal));
        narrator.Value("Content", received.Message.Body?["content"]?.GetValue<string>());
        return bob.Did;
    }

    // ── 4. expires_time under an injected clock ─────────────────────────────────────────

    private static async Task SectionFourClockAsync(
        Narrator narrator, ServiceProvider sp, DidCommClient client, string didKey, string didPeer2)
    {
        narrator.Section("4", "expires_time + DidCommOptions.Clock / ExpiresClockSkew (FR-API-05)");

        // Expiry is checked against DidCommOptions.Clock, injectable precisely so samples and
        // tests never sleep: we pin a base instant and move the CLOCK, not the wall time.
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(didKey)
            .WithTo(didPeer2)
            .WithExpiresTime(baseTime.AddMinutes(5).ToUnixTimeSeconds())
            .WithBody(new JsonObject { ["content"] = "Read me within five minutes." })
            .Build();
        var packed = (await client.PackEncryptedAsync(message, new PackEncryptedOptions(
            Recipients: new[] { didPeer2 }, From: didKey))).Message;
        narrator.Step($"Packed with expires_time = base + 5 minutes ({message.ExpiresTime}).");

        // Three receivers that differ ONLY in their configured clock/skew, built on the same
        // secrets and resolver graph.
        var secretsResolver = sp.GetRequiredService<DidComm.Secrets.ISecretsResolver>();
        var keyService = sp.GetRequiredService<IDidKeyService>();
        DidCommClient At(TimeSpan offset, TimeSpan skew) => new(
            secretsResolver, keyService,
            new DidCommOptions { Clock = () => baseTime + offset, ExpiresClockSkew = skew });

        var fresh = await At(TimeSpan.FromMinutes(1), TimeSpan.Zero).UnpackAsync(packed);
        narrator.Value("Clock at base+1min", $"accepted (content = {fresh.Message.Body?["content"]?.GetValue<string>()})");

        try
        {
            await At(TimeSpan.FromHours(1), TimeSpan.Zero).UnpackAsync(packed);
            narrator.Value("Clock at base+1h, no skew", "UNEXPECTED — accepted");
        }
        catch (MalformedMessageException)
        {
            narrator.Value("Clock at base+1h, no skew", "rejected (MalformedMessageException — message expired)");
        }

        var tolerant = await At(TimeSpan.FromHours(1), TimeSpan.FromHours(2)).UnpackAsync(packed);
        narrator.Value("Clock at base+1h, skew 2h", $"accepted (tolerance covers the drift, Authenticated={tolerant.Authenticated})");
    }

    // ── 5. The deliberate did:web refusal ───────────────────────────────────────────────

    private static async Task SectionFiveDidWebAsync(
        Narrator narrator, IDidKeyService keyService, DidCommClient client, string didKey, string didPeer2)
    {
        narrator.Section("5", "did:web is refused at every entry point (FR-DID-06, DD-08)");

        // did:web anchors trust in DNS + web PKI + domain control, with no verifiable history
        // and no pre-rotation: a domain takeover silently substitutes keys. The library
        // refuses the method OUTRIGHT — before any envelope work — and recommends did:webvh
        // (verifiable history) for web-hosted DIDs. This is a permanent design decision, not a
        // missing feature.
        const string didWeb = "did:web:example.com";
        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(didKey)
            .WithTo(didPeer2)
            .Build();

        await Refuses(narrator, "Resolution (GetVerificationMethodsAsync)", () =>
            keyService.GetVerificationMethodsAsync(didWeb, VerificationRelationship.KeyAgreement));

        await Refuses(narrator, "Pack — did:web recipient", () =>
            client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { didWeb })));

        await Refuses(narrator, "Pack — did:web sender", () =>
            client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { didPeer2 }, From: didWeb)));

        await Refuses(narrator, "Pack — did:web signer", () =>
            client.PackSignedAsync(message, signFrom: didWeb));

        await Refuses(narrator, "SendAsync — did:web recipient", () =>
            client.SendAsync(message, new SendOptions(Recipients: new[] { didWeb })));

        // The receive side is guarded too: a wire message ADDRESSED FROM did:web is rejected
        // when its plaintext surfaces, even though the envelope itself parsed fine.
        var didWebPlaintext = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("D"),
            typ = "application/didcomm-plain+json",
            type = "https://didcomm.org/basicmessage/2.0/message",
            from = didWeb,
            to = new[] { didPeer2 },
            body = new { content = "greetings from DNS-anchored trust" },
        });
        await Refuses(narrator, "Unpack — plaintext from did:web", () => client.UnpackAsync(didWebPlaintext));
    }

    // ── 6. Under the adapter's hood ─────────────────────────────────────────────────────

    private static async Task SectionSixAdapterAsync(
        Narrator narrator, ServiceProvider sp, string didKey, string didPeer2)
    {
        narrator.Section("6", "Under the hood: NetDidKeyService, key bindings, and the route record");

        // UseNetDidResolver() registered exactly this adapter over net-did's IDidResolver —
        // construct it yourself when assembling a client without DI (DD-01). Hold it by its
        // contract: IDidKeyService is the seam a custom resolver implementation replaces.
        var resolver = sp.GetRequiredService<IDidResolver>();
        var adapter = new NetDidKeyService(resolver);
        IDidKeyService keyService = adapter;

        // The authorization question the unpack pipeline asks on every envelope: is this kid
        // genuinely authorized by this DID under this relationship? Resolver-backed — not
        // string prefix matching (FR-CONSIST-06).
        var kaKeys = await keyService.GetVerificationMethodsAsync(didPeer2, VerificationRelationship.KeyAgreement);
        var kid = kaKeys[0].Kid ?? throw new InvalidOperationException("Resolved keyAgreement JWK has no kid.");
        narrator.Value("IsKeyAuthorizedAsync(own DID, kid)",
            await keyService.IsKeyAuthorizedAsync(didPeer2, kid, VerificationRelationship.KeyAgreement));
        narrator.Value("IsKeyAuthorizedAsync(a DIFFERENT DID, kid)",
            await keyService.IsKeyAuthorizedAsync(didKey, kid, VerificationRelationship.KeyAgreement));

        // The stronger answer — a key binding: key material, controller, and relationship read
        // from ONE resolution, so no check can straddle two document versions (FR-CONSIST-07).
        // The adapter exposes it through IDidKeyBindingService; a custom IDidKeyService opts
        // into the same contract by implementing this interface.
        IDidKeyBindingService bindings = adapter;
        var binding = await bindings.ResolveKeyBindingAsync(kid, VerificationRelationship.KeyAgreement)
            ?? throw new InvalidOperationException("The adapter should bind its own resolved kid.");
        narrator.Value("Binding.Kid", Trunc(binding.Kid, 64));
        narrator.Value("Binding.Did", Trunc(binding.Did, 64));
        narrator.Value("Binding.Controller (null ⇒ method declares none)", binding.Controller);
        narrator.Value("Binding.Relationship", binding.Relationship);
        narrator.Value("Binding.PublicJwk.Crv", binding.PublicJwk.Crv);
        narrator.Value("Binding.PublicKeyThumbprint", Trunc(binding.PublicKeyThumbprint, 32));

        // A custom binding service returns exactly this record — the constructor's contract is
        // that all five facts came from a single read of a single document version.
        var custom = new ResolvedKeyBinding(binding.Kid, binding.Did, binding.Controller, binding.Relationship, binding.PublicJwk);
        narrator.Value("Hand-built binding mirrors the adapter's", custom.Kid == binding.Kid && custom.Did == binding.Did);

        // RejectUnsupportedMethod is the guard every entry point calls — the section-5 refusals
        // all funnel through it. Its exception carries the security rationale.
        try
        {
            keyService.RejectUnsupportedMethod("did:web:example.com");
            narrator.Value("RejectUnsupportedMethod(did:web)", "UNEXPECTED — accepted");
        }
        catch (UnsupportedDidMethodException ex)
        {
            narrator.Value("RejectUnsupportedMethod(did:web) reason", Trunc(ex.Reason, 88));
        }

        // The service-endpoint side of the same adapter family: NetDidServiceEndpointResolver
        // reads DIDCommMessaging entries (sample 04 drives it end to end), and the send
        // pipeline expands them into a ResolvedRoute — transport URI, routing-key JWKs to wrap
        // for, and fallback URIs. Construct the record yourself when faking routes in tests.
        var endpointResolver = new NetDidServiceEndpointResolver(resolver, keyService, new DidCommOptions());
        var services = await endpointResolver.ResolveAsync(didPeer2);
        narrator.Value("DIDCommMessaging entries on the did:peer:2", services.Count);
        var fakeRoute = new ResolvedRoute(
            TransportUri: "https://mediator.example/didcomm",
            RoutingKeyJwks: kaKeys,
            FallbackUris: new[] { "wss://mediator.example/ws" });
        narrator.Value("Fake route", $"{fakeRoute.TransportUri} (+{fakeRoute.FallbackUris.Count} fallback, {fakeRoute.RoutingKeyJwks.Count} routing key)");
    }

    /// <summary>
    /// Run <paramref name="action"/>, assert it throws <see cref="UnsupportedDidMethodException"/>
    /// whose <c>Method</c> is <c>web</c>, and narrate the outcome (any other result is loud).
    /// </summary>
    private static async Task Refuses(Narrator narrator, string label, Func<Task> action)
    {
        try
        {
            await action();
            narrator.Value(label, "UNEXPECTED — no exception thrown");
        }
        catch (UnsupportedDidMethodException ex) when (ex.Method == "web")
        {
            narrator.Value(label, $"refused (Method='{ex.Method}', Did='{ex.Did}')");
        }
    }

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
