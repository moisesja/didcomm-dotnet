using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance for Coordinate Mediation 2.0 on the client (FR-MED-09, FR-MED-10): against a host
/// whose Discover Features discloses only 2.0 (<see cref="FakeCoordinateMediation20Mediator"/>, the
/// registry-aware endpoint with a test handler answering in the 2.0 page's shapes), the client
/// negotiates 2.0 and enrols, updates and lists in 2.0 messages, whether the answers carry a
/// <c>thid</c> or not (over HTTP the response is the answer). A host disclosing neither version is
/// <see cref="MediationNotSupportedException"/>; a mediator disclosing 3.0 and 2.0 is spoken to in
/// 3.0.
/// </summary>
public sealed class MediatorClientCoordinateMediation20Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Against_a_2_0_only_host_the_client_negotiates_2_0_and_enrols_updates_and_lists_in_2_0(bool omitThid)
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, omitThid: omitThid);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var (grant, published) = await MediatorClientEnrolmentTests.EnrolAsync(harness, fake.Did, bob);

        grant.RoutingDids.Should().Equal(new[] { fake.Did }, "a 2.0 grant's single routing_did is a one-element list");
        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(
            CoordinateMediation20.MediateRequestType,
            CoordinateMediation20.KeylistUpdateType,
            CoordinateMediation20.KeylistQueryType,
            CoordinateMediation20.KeylistQueryType);
        fake.Handler.KeysOf(bob.Did).Should().Equal(published.Did);
        harness.Channel.Versions.TryGet(fake.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);

        // The service entry minted from a 2.0 grant points at the 2.0 mediator.
        var entry = DidCommServiceBuilder.ForMediation(grant).Build();
        entry.ServiceEndpoint!.Map!["uri"].GetString().Should().Be(fake.Did);
        mediator.LeakingLogLines(published.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_disclosing_neither_version_is_MediationNotSupportedException_and_nothing_else_is_sent()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator, speaks20: false);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var thrown = (await FluentActions.Awaiting(() => harness.Mediation.RequestMediationAsync(fake.Did, bob.Did))
            .Should().ThrowAsync<MediationNotSupportedException>()).Which;

        thrown.DisclosedProtocols.Should().BeEmpty("the query asks for coordinate-mediation/* only");
        thrown.RequestId.Should().NotBeNullOrEmpty();
        thrown.Message.Should().NotContain(fake.Did).And.NotContain(bob.Did);
        harness.Channel.Versions.TryGet(fake.Did, out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_mediator_disclosing_3_0_and_2_0_is_spoken_to_in_3_0()
    {
        var identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://mediator.test/", Accept: new[] { "didcomm/v2" }));
        var twoOh = new FakeCoordinateMediation20Handler(identity.Did, omitThid: false);
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Identity = identity;
            o.ConfigureServices = s =>
            {
                s.AddSingleton(twoOh);
                s.TryAddEnumerable(ServiceDescriptor.Singleton<IProtocolHandler, FakeCoordinateMediation20Handler>(sp => sp.GetRequiredService<FakeCoordinateMediation20Handler>()));
            };
        });
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var bob = await harness.NewPeerAsync();

        var (grant, published) = await MediatorClientEnrolmentTests.EnrolAsync(harness, mediator.Did, bob);

        grant.RoutingDids.Should().Equal(mediator.Did);
        twoOh.Handled.Should().BeEmpty("3.0 is preferred when both are disclosed");
        (await mediator.Mediation.FindGrantByRecipientDidAsync(published.Did, default))!.PeerDid.Should().Be(bob.Did);
        harness.Channel.Versions.TryGet(mediator.Did, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V3);
    }

    [Fact]
    public async Task A_pinned_2_0_client_enrols_with_the_2_0_host_without_asking_first()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var fake = await FakeCoordinateMediation20Mediator.StartAsync(mediator);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.Client = c => c.CoordinateMediationVersion = CoordinateMediationVersion.V2);
        var bob = await harness.NewPeerAsync();

        (await harness.Mediation.RequestMediationAsync(fake.Did, bob.Did))!.RoutingDids.Should().Equal(fake.Did);

        fake.Handler!.Handled.Select(m => m.Type).Should().Equal(CoordinateMediation20.MediateRequestType);
        harness.Channel.Versions.TryGet(fake.Did, out _).Should().BeFalse("a pinned version is not negotiated");
    }
}
