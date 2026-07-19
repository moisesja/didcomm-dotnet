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
/// or <see cref="InboundObservation.FromUnpackResult"/> would fail these, unlike tests that hand-build
/// the trust metadata.
/// </summary>
public sealed class DiscoverFeaturesInitiatorCryptoTests
{
    private sealed record World(DidCommClient Client, string Alice, string Bob, string Mallory);

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

        return new World(sp.GetRequiredService<DidCommClient>(), alice.Did, bob.Did, mallory.Did);
    }

    /// <summary>Drive a DiscoverFeaturesClient that captures its outbound query, and dispatch real
    /// unpacked disclosures into it through a dispatcher wired with it as the observer.</summary>
    private static (DiscoverFeaturesClient Client, Func<Message> LastQuery, ProtocolDispatcher Dispatcher) NewInitiator()
    {
        Message? captured = null;
        var client = new DiscoverFeaturesClient((msg, _, _) => { captured = msg; return Task.CompletedTask; });
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new DiscoverFeaturesHandler(Array.Empty<IFeatureProvider>()));
        var dispatcher = new ProtocolDispatcher(
            registry, new InMemoryThreadStateStore(), logger: null, traceOptions: null,
            observers: new IProtocolObserver[] { client });
        return (client, () => captured ?? throw new InvalidOperationException("no query captured"), dispatcher);
    }

    private async Task DispatchAsync(ProtocolDispatcher dispatcher, DidCommClient client, string packed)
    {
        var unpacked = await client.UnpackAsync(packed);
        await dispatcher.DispatchAsync(unpacked, client, new DidCommOptions());
        await dispatcher.FlushObserversAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_real_authcrypt_disclose_from_the_queried_peer_completes_the_query()
    {
        var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(10));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "https://didcomm.org/trust-ping/2.0" });
        var packed = (await w.Client.PackEncryptedAsync(disclose,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;

        await DispatchAsync(dispatcher, w.Client, packed);
        (await task).Should().ContainSingle(d => d.Id == "https://didcomm.org/trust-ping/2.0");
    }

    [Fact]
    public async Task A_real_signed_disclose_from_the_queried_peer_completes_the_query()
    {
        var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(10));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "signed-ok" });
        var packed = await w.Client.PackSignedAsync(disclose, signFrom: w.Bob); // JWS: authenticated via verified signer

        await DispatchAsync(dispatcher, w.Client, packed);
        (await task).Should().ContainSingle(d => d.Id == "signed-ok");
    }

    [Fact]
    public async Task A_real_self_consistent_Mallory_authcrypt_disclose_cannot_answer_the_query_for_Bob()
    {
        var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        var task = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(10));

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
    public async Task A_post_sign_tampered_disclose_is_rejected_at_unpack_and_never_observed()
    {
        var w = await BuildAsync();
        var (client, lastQuery, dispatcher) = NewInitiator();
        _ = client.QueryFeaturesAsync(w.Alice, w.Bob, new[] { new FeatureQuery { FeatureType = "protocol", Match = "*" } }, TimeSpan.FromSeconds(10));

        var disclose = DiscoverFeaturesApi.CreateDisclose(from: w.Bob, to: w.Alice, thid: lastQuery().Id,
            new FeatureDisclosure { FeatureType = "protocol", Id = "will-be-tampered" });
        var packed = (await w.Client.PackEncryptedAsync(disclose,
            new PackEncryptedOptions(Recipients: new[] { w.Alice }, From: w.Bob))).Message;

        // Flip a byte in the ciphertext: AEAD fails, so UnpackAsync throws and the message never
        // reaches dispatch or the observer — a tampered disclose can't complete a query.
        var tampered = CorruptCiphertext(packed);
        var act = async () => await w.Client.UnpackAsync(tampered);
        await act.Should().ThrowAsync<Exception>("post-sign/encrypt tampering must be rejected at unpack");
    }

    private static string CorruptCiphertext(string jweJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(jweJson)!.AsObject();
        var ct = node["ciphertext"]!.GetValue<string>();
        node["ciphertext"] = ct[..^1] + (ct[^1] == 'A' ? 'B' : 'A');
        return node.ToJsonString();
    }
}
