using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.DiscoverFeatures;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetDid.Core;
using NetDid.Core.Crypto;
using Xunit;

// L-014.
using DiscoverFeaturesApi = DidComm.Protocols.DiscoverFeatures.DiscoverFeatures;
using TrustPingApi = DidComm.Protocols.TrustPing.TrustPing;

namespace DidComm.InteropTests.Protocols;

public sealed class DiscoverFeaturesDiTests
{
    [Fact]
    public void AddBuiltInProtocols_registers_handler_plus_default_providers()
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
        registry.TryResolve(DiscoverFeaturesApi.QueriesType, out var handler).Should().BeTrue();
        handler.Should().BeOfType<DiscoverFeaturesHandler>();

        var providers = sp.GetServices<IFeatureProvider>().ToList();
        providers.Should().Contain(p => p is DidComm.Protocols.DiscoverFeatures.ProtocolFeatureProvider);
        providers.Should().Contain(p => p is MaxReceiveBytesConstraintProvider);
    }

    [Fact]
    public async Task End_to_end_query_disclose_round_trip_via_dispatcher_lists_all_built_in_protocols()
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
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var query = DiscoverFeaturesApi.CreateQuery(alice.Did, bob.Did,
            new FeatureQuery { FeatureType = "protocol", Match = "https://didcomm.org/*" },
            new FeatureQuery { FeatureType = "constraint", Match = DiscoverFeaturesApi.ConstraintMaxReceiveBytes });

        var packed = await client.PackEncryptedAsync(query, new PackEncryptedOptions(
            Recipients: new[] { bob.Did }, From: alice.Did));
        var unpacked = await client.UnpackAsync(packed.Message);

        var outcome = await dispatcher.DispatchAsync(unpacked, client, options);
        outcome.Result.Should().Be(DispatchResult.ReplyProduced);
        outcome.Reply!.Type.Should().Be(DiscoverFeaturesApi.DiscloseType);
        outcome.Reply.Thid.Should().Be(query.Id);

        var disclosures = DiscoverFeaturesApi.ReadDisclosures(outcome.Reply);
        disclosures.Select(d => d.Id).Should().Contain(new[]
        {
            TrustPingApi.ProtocolUri,
            DidComm.Protocols.Empty.EmptyProtocol.ProtocolUri,
            DiscoverFeaturesApi.ProtocolUri,
            DiscoverFeaturesApi.ConstraintMaxReceiveBytes,
        });

        // max_receive_bytes value reflects the default DidCommOptions.MaxReceiveBytes.
        var maxBytes = disclosures.First(d => d.Id == DiscoverFeaturesApi.ConstraintMaxReceiveBytes);
        maxBytes.Value.Should().Be(options.MaxReceiveBytes);
    }

    [Fact]
    public void AddFeatureProvider_appends_consumer_provider_alongside_defaults()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
            b.AddFeatureProvider<GoalCodeStubProvider>();
        });
        using var sp = services.BuildServiceProvider();
        var providers = sp.GetServices<IFeatureProvider>().ToList();
        providers.Should().Contain(p => p is GoalCodeStubProvider);
    }

    private sealed class GoalCodeStubProvider : IFeatureProvider
    {
        public string FeatureType => "goal-code";
        public IEnumerable<FeatureDisclosure> Disclose(string match, ProtocolContext context)
            => Array.Empty<FeatureDisclosure>();
    }
}
