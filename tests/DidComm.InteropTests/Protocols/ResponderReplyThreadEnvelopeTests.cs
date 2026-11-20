using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Protocols.TrustPing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.Protocols;

/// <summary>
/// #138 on the real envelope path: a request packed authcrypt, unpacked by <see cref="DidCommClient.UnpackAsync"/>
/// (so its <c>thid</c>/<c>pthid</c> passed receive validation) and dispatched by the DI-built
/// <see cref="ProtocolDispatcher"/>. DIDComm v2.1 threading.md L15: a request that declares no
/// <c>thid</c> has its <c>id</c> as its <c>thid</c>; L17: every later message in the thread MUST carry
/// that same <c>thid</c>. So the Trust Ping <c>ping-response</c> and the Discover Features
/// <c>disclose</c> carry the declared <c>thid</c>, never the request's <c>id</c> in its place, and
/// still carry it after being packed back to the requester and unpacked there.
/// </summary>
public sealed class ResponderReplyThreadEnvelopeTests
{
    private const string Parent = "parent-thread-138";

    public enum Kind { TrustPing, DiscoverFeatures }

    public enum Case { NoThid, ThidDiffersFromId, ThidWithPthid, PthidWithoutThid }

    public static TheoryData<Kind, Case> Matrix()
    {
        var data = new TheoryData<Kind, Case>();
        foreach (var kind in Enum.GetValues<Kind>())
            foreach (var @case in Enum.GetValues<Case>())
                data.Add(kind, @case);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task An_unpacked_authcrypt_request_is_answered_by_the_dispatcher_on_the_thid_it_declares_else_on_its_id(Kind kind, Case @case)
    {
        var services = new ServiceCollection();
        var secrets = new InMemorySecretsResolver();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
        });
        await using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) secrets.Add(jwk);
        foreach (var jwk in bob.Privates) secrets.Add(jwk);
        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var declared = "thr-" + Guid.NewGuid().ToString("N");
        var request = kind == Kind.TrustPing
            ? TrustPingApi.CreatePing(from: alice.Did, to: bob.Did)
            : DiscoverFeaturesApi.CreateQuery(alice.Did, bob.Did, new FeatureQuery { FeatureType = "protocol", Match = "*" });
        request.Thid = @case is Case.ThidDiffersFromId or Case.ThidWithPthid ? declared : null;
        request.Pthid = @case is Case.ThidWithPthid or Case.PthidWithoutThid ? Parent : null;
        var expected = request.Thid ?? request.Id; // threading.md L15

        var packed = await client.PackEncryptedAsync(request, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did));
        var unpacked = await client.UnpackAsync(packed.Message);
        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        var reply = outcome.Reply!;
        reply.Type.Should().Be(kind == Kind.TrustPing ? TrustPingApi.ResponseType : DiscoverFeaturesApi.DiscloseType);
        reply.Thid.Should().Be(expected);
        if (request.Thid is not null)
            reply.Thid.Should().NotBe(request.Id, "a reply on the request's id is not in the thread the request declared (threading.md L17)");
        reply.Pthid.Should().BeNull("the request is the first message of its thread, so the reply need not repeat the parent");
        reply.From.Should().Be(bob.Did);
        reply.To.Should().Equal(alice.Did);

        // The reply packed back to the requester still names the thread after the requester's unpack validated it.
        var back = await client.PackEncryptedAsync(reply, new PackEncryptedOptions(Recipients: new[] { alice.Did }, From: bob.Did));
        var received = await client.UnpackAsync(back.Message);
        received.Authenticated.Should().BeTrue();
        received.Message.Thid.Should().Be(expected);
        received.Message.Pthid.Should().BeNull();
    }
}
