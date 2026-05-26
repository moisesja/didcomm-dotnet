using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Empty;
using DidComm.Protocols.TrustPing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetDid.Core;
using NetDid.Core.Crypto;
using Xunit;

// L-014.
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.Protocols;

public sealed class RegistryDiAndDispatchTests
{
    [Fact]
    public void AddBuiltInProtocols_registers_TrustPing_and_Empty_in_the_registry()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
        });
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        registry.TryResolve(TrustPingApi.PingType, out var trust).Should().BeTrue();
        trust.Should().BeOfType<TrustPingHandler>();

        registry.TryResolve(EmptyProtocol.MessageType, out var empty).Should().BeTrue();
        empty.Should().BeOfType<EmptyHandler>();
    }

    [Fact]
    public async Task End_to_end_pack_unpack_dispatch_replies_to_trust_ping()
    {
        var services = new ServiceCollection();
        var secrets = new InMemorySecretsResolver();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
        });
        using var sp = services.BuildServiceProvider();

        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();

        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) secrets.Add(jwk);
        foreach (var jwk in bob.Privates) secrets.Add(jwk);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DidCommOptions>>().Value;

        // Alice packs a Trust Ping authcrypt envelope to Bob.
        var ping = TrustPingApi.CreatePing(from: alice.Did, to: bob.Did);
        var packed = await client.PackEncryptedAsync(ping, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did));

        // Bob's side unpacks and dispatches.
        var unpacked = await client.UnpackAsync(packed.Message);
        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);

        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply.Should().NotBeNull();
        outcome.Reply!.Type.Should().Be(TrustPingApi.ResponseType);
        outcome.Reply.Thid.Should().Be(ping.Id);
        outcome.Reply.From.Should().Be(bob.Did);
        outcome.Reply.To.Should().Equal(alice.Did);
        outcome.Handler.Should().BeOfType<TrustPingHandler>();
    }
}
