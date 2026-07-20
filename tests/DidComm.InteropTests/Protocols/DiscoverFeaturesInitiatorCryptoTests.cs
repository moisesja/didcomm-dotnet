using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Threading;
using DidComm.Transports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetCrypto;
using NetDid.Core;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// FR-PROTO-05a with REAL cryptography (PR #51 review finding 4): the initiator correlation and its
/// anti-spoof gate are exercised against genuine authcrypt/signed envelopes unpacked through
/// <see cref="DidCommClient.UnpackAsync"/> — so a regression in envelope unpacking, sender binding,
/// or immutable inbound-snapshot capture would fail these, unlike tests that hand-build trust metadata.
/// </summary>
public sealed class DiscoverFeaturesInitiatorCryptoTests
{
    private sealed record World(ServiceProvider Sp, DidCommClient Client, string Alice, string Bob, string Mallory)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Sp.DisposeAsync();
    }

    private static async Task<World> BuildAsync()
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
        });
        var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var mallory = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var id in new[] { alice, bob, mallory })
            foreach (var jwk in id.Privates) secrets.Add(jwk);

        return new World(sp, sp.GetRequiredService<DidCommClient>(), alice.Did, bob.Did, mallory.Did);
    }

    /// <summary>Drive a DiscoverFeaturesClient that captures its outbound query, and dispatch real
    /// unpacked disclosures into it through a dispatcher wired with it as the inline correlator.</summary>
    private static (DiscoverFeaturesClient Client, Func<Message> LastQuery, ProtocolDispatcher Dispatcher) NewInitiator()
    {
        Message? captured = null;
        var client = new DiscoverFeaturesClient((msg, _, _) => { captured = msg; return Task.CompletedTask; });
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: null, correlators: new IInboundCorrelator[] { client });
        return (client, () => captured ?? throw new InvalidOperationException("no query captured"), dispatcher);
    }

    // Correlation is synchronous and inline, so once DispatchAsync returns the pending query is
    // already completed (or rejected) — no flush needed.
    private async Task DispatchAsync(ProtocolDispatcher dispatcher, DidCommClient client, string packed)
    {
        var unpacked = await client.UnpackAsync(packed);
        await dispatcher.DispatchAsync(unpacked, client, new DidCommOptions());
    }

    [Fact]
    public async Task A_real_authcrypt_disclose_from_the_queried_peer_completes_the_query()
    {
        await using var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(60));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "https://didcomm.org/trust-ping/2.0" });
        var packed = (await w.Client.PackEncryptedAsync(disclose,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;

        await DispatchAsync(dispatcher, w.Client, packed);
        (await task).Should().ContainSingle(d => d.Id == "https://didcomm.org/trust-ping/2.0");
    }

    [Fact]
    public async Task Plaintext_unpack_preserves_the_exact_verified_json_in_the_weak_snapshot_sidecar()
    {
        await using var w = await BuildAsync();
        const string plaintext = "{\n  \"id\": \"snapshot-1\", \"type\": \"https://didcomm.org/test/1.0/message\",\n  \"attachments\": [{ \"id\": \"a1\", \"data\": { \"base64\": \"aGVsbG8=\" } }],\n  \"x-extra\": { \"emoji\": \"😀\" }\n}";

        var unpacked = await w.Client.UnpackAsync(plaintext);

        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();
        snapshot.PlaintextJson.Should().Be(plaintext, "the unpack boundary must reuse the verified raw plaintext rather than reserialize it");
        snapshot.Utf8ByteCount.Should().Be(System.Text.Encoding.UTF8.GetByteCount(plaintext));
        var cloned = snapshot.DeserializeMessage();
        cloned.AdditionalHeaders.Should().ContainKey("x-extra");
        cloned.Attachments.Should().ContainSingle().Which.Id.Should().Be("a1");
    }

    [Fact]
    public async Task Real_unpack_snapshot_is_immune_to_live_message_mutation_before_dispatch()
    {
        await using var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob,
            new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(60));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "verified-original" });
        var packed = (await w.Client.PackEncryptedAsync(disclose,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;
        var unpacked = await w.Client.UnpackAsync(packed);

        InboundMessageSnapshot.TryGetFor(unpacked.Message, out var snapshot).Should().BeTrue();
        snapshot.From.Should().Be(w.Bob);
        snapshot.To.Should().ContainSingle().Which.Should().Be(w.Alice);
        snapshot.RecipientKid.Should().StartWith(w.Alice);

        // The public Message is intentionally mutable. Correlation must still use the exact verified
        // plaintext/trust sidecar captured before this caller-controlled mutation.
        unpacked.Message.From = w.Mallory;
        unpacked.Message.To = new[] { w.Mallory };
        unpacked.Message.Thid = "rewritten-thread";
        unpacked.Message.Body = new System.Text.Json.Nodes.JsonObject();

        await dispatcher.DispatchAsync(unpacked, w.Client, new DidCommOptions());
        (await task).Should().ContainSingle(d => d.Id == "verified-original");
    }

    [Fact]
    public async Task A_real_signed_disclose_from_the_queried_peer_completes_the_query()
    {
        await using var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(60));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "signed-ok" });
        var packed = await w.Client.PackSignedAsync(disclose, signFrom: w.Bob); // JWS: authenticated via verified signer

        await DispatchAsync(dispatcher, w.Client, packed);
        (await task).Should().ContainSingle(d => d.Id == "signed-ok");
    }

    [Fact]
    public async Task A_real_self_consistent_Mallory_authcrypt_disclose_cannot_answer_the_query_for_Bob()
    {
        await using var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(60));

        // Mallory forges a fully self-consistent, genuinely-authenticated disclose (from = Mallory,
        // signed/encrypted by Mallory's real keys) with the guessed thid. It unpacks cleanly with
        // Authenticated = true and from = Mallory — the anti-spoof rejects it because Mallory is not
        // the queried responder (Bob).
        var forged = DiscoverFeaturesApi.CreateDisclose(from: w.Mallory, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "forged" });
        var packed = (await w.Client.PackEncryptedAsync(forged,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Mallory))).Message;

        await DispatchAsync(dispatcher, w.Client, packed);
        task.IsCompleted.Should().BeFalse("a genuinely-authenticated disclose from a third party must not answer Bob's query");

        // The legitimate Bob disclose still completes it afterward — the forgery didn't cancel it.
        var legit = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "legit" });
        var legitPacked = (await w.Client.PackEncryptedAsync(legit,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;
        await DispatchAsync(dispatcher, w.Client, legitPacked);
        (await task).Should().ContainSingle(d => d.Id == "legit");
    }

    [Fact]
    public async Task An_authcrypt_tampered_disclose_is_rejected_at_unpack_and_never_observed()
    {
        await using var w = await BuildAsync();
        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: "some-query-id",
            new FeatureDisclosure { FeatureType = "protocol", Id = "will-be-tampered" });
        var packed = (await w.Client.PackEncryptedAsync(disclose,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;

        // Flip an interior byte of the ciphertext (stays valid base64url): the AEAD tag fails, so
        // UnpackAsync rejects it and the message never reaches dispatch — a tampered disclose can't
        // complete a query.
        var tampered = CorruptFieldInterior(packed, "ciphertext");
        var act = async () => await w.Client.UnpackAsync(tampered);
        await act.Should().ThrowAsync<DidComm.Exceptions.CryptoException>("authcrypt tampering must be rejected at unpack");
    }

    [Fact]
    public async Task A_post_sign_tampered_signed_disclose_fails_signature_verification_at_unpack()
    {
        await using var w = await BuildAsync();
        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: "some-query-id",
            new FeatureDisclosure { FeatureType = "protocol", Id = "will-be-tampered" });
        var signed = await w.Client.PackSignedAsync(disclose, signFrom: w.Bob); // JWS

        // Corrupt the SIGNATURE itself (an interior byte, still valid base64url), so the failure is a
        // genuine signature-verification rejection — not a base64/JSON decode error that could pass
        // before verification is even reached.
        var tampered = CorruptSignature(signed);
        var act = async () => await w.Client.UnpackAsync(tampered);
        await act.Should().ThrowAsync<DidComm.Exceptions.CryptoException>("post-sign JWS tampering must fail signature verification");
    }

    [Fact]
    public async Task A_self_consistent_Mallory_SIGNED_disclose_cannot_answer_the_query_for_Bob()
    {
        await using var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        using var cts = new CancellationTokenSource();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, Timeout.InfiniteTimeSpan, ct: cts.Token);

        // Mallory produces a fully valid, genuinely-signed disclose from herself (from = Mallory,
        // verified JWS signer = Mallory) with the guessed thid. It unpacks with Authenticated = true
        // and a real SignerKid — but the anti-spoof rejects it because Mallory is not the queried Bob.
        var forged = DiscoverFeaturesApi.CreateDisclose(from: w.Mallory, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "forged-signed" });
        var signed = await w.Client.PackSignedAsync(forged, signFrom: w.Mallory);

        await DispatchAsync(dispatcher, w.Client, signed);
        task.IsCompleted.Should().BeFalse("a genuinely-signed disclose from a third party must not answer Bob's query");

        cts.Cancel(); // abandon the still-pending query so nothing leaks
        await task.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    // Flip an INTERIOR base64url char (not the last one, which could be dropped as non-canonical
    // padding before the value is used) so the change survives decode and reaches the crypto check.
    private static string CorruptFieldInterior(string jsonEnvelope, string field)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(jsonEnvelope)!.AsObject();
        node[field] = Flip(node[field]!.GetValue<string>());
        return node.ToJsonString();
    }

    // Corrupt the JWS signature bytes (interior), so unpack fails at SIGNATURE VERIFICATION rather
    // than at base64/JSON decode. Handles both flattened ("signature") and general ("signatures")
    // JWS JSON shapes.
    private static string CorruptSignature(string jws)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(jws)!.AsObject();
        if (node["signature"] is System.Text.Json.Nodes.JsonNode flat)
        {
            node["signature"] = Flip(flat.GetValue<string>());
        }
        else
        {
            var sig = node["signatures"]!.AsArray()[0]!.AsObject();
            sig["signature"] = Flip(sig["signature"]!.GetValue<string>());
        }
        return node.ToJsonString();
    }

    private static string Flip(string base64Url)
    {
        var i = base64Url.Length / 2; // an interior char
        var c = base64Url[i];
        var replacement = c == 'A' ? 'B' : 'A';
        return base64Url[..i] + replacement + base64Url[(i + 1)..];
    }
}
