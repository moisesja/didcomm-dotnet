using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Mediator.Tests.Coordination.HandlerMessages;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>
/// One tenant across both Coordinate Mediation versions (FR-MED-10, #99 acceptance): a 2.0
/// <c>keylist-update</c> and a 3.0 <c>recipient-update</c> from the same peer change one grant's
/// registrations, each version lists and removes what the other added, a second peer can touch
/// none of it through either version, and the forward directory sees DIDs added through 2.0. Run
/// twice: with both handlers built by hand over one store, and through the container's
/// <c>AddCoordinateMediation()</c>.
/// </summary>
public sealed class CoordinateMediationCrossVersionTests
{
    private const RecipientUpdateOutcome Success = RecipientUpdateOutcome.Success;
    private const RecipientUpdateOutcome NoChange = RecipientUpdateOutcome.NoChange;
    private const RecipientUpdateOutcome ClientError = RecipientUpdateOutcome.ClientError;

    /// <summary>How the two handlers are put together.</summary>
    public enum Wiring
    {
        /// <summary>Both handlers constructed by the test over one store, policy and identity, in a dispatcher of its own.</summary>
        HandBuilt,

        /// <summary>The container's <c>AddCoordinateMediation()</c> registrations and dispatcher.</summary>
        Container,
    }

    public static TheoryData<Wiring> Wirings => new() { Wiring.HandBuilt, Wiring.Container };

    /// <summary>A mediator serving both versions, the store behind it and its forward directory.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required HandlerFixture Fx { get; init; }
        public required InMemoryMediationStore Store { get; init; }
        public required HandlerFakeIdentity Identity { get; init; }
        public required IForwardRecipientRegistry Forward { get; init; }
        public ProtocolDispatcher? Own { get; init; }

        public async ValueTask DisposeAsync()
        {
            if (Own is not null)
                await Own.DisposeAsync();
            await Fx.DisposeAsync();
        }
    }

    private static Rig Create(Wiring wiring)
    {
        if (wiring == Wiring.Container)
        {
            var fx = HandlerFixture.Create(fakeIdentity: true);
            return new Rig
            {
                Fx = fx,
                Store = fx.Memory,
                Identity = fx.Identity!,
                Forward = fx.Host.Services.GetRequiredService<IForwardRecipientRegistry>(),
            };
        }

        // The container still packs and unpacks (it holds every party's keys); the handlers that
        // answer are these two, over one store.
        var host = HandlerFixture.Create();
        var store = new InMemoryMediationStore();
        var policy = new DefaultMediationPolicy();
        var identity = new HandlerFakeIdentity();
        var options = Microsoft.Extensions.Options.Options.Create(new CoordinateMediationOptions());
        var registry = new ProtocolHandlerRegistry();
        registry.Register(new CoordinateMediationHandler(store, policy, identity, options));
        registry.Register(new CoordinateMediation20Handler(store, policy, identity, options));
        var dispatcher = new ProtocolDispatcher(registry, new InMemoryThreadStateStore());
        host.Dispatcher = dispatcher;
        return new Rig
        {
            Fx = host,
            Store = store,
            Identity = identity,
            Forward = new MediationForwardRecipientRegistry(store),
            Own = dispatcher,
        };
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task A_peer_enrolled_in_2_0_that_adds_through_both_versions_is_one_tenant(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var via20 = (await fx.NewPeerAsync()).Did;
        var via30 = (await fx.NewPeerAsync()).Did;

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);
        (await fx.Update20Async(bob.Did, mediator.Did, Add(via20))).Should().Equal(Added(via20, Success));
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(via30))).Should().Equal(new[] { Added(via30, Success) }, "the 2.0 grant enrols Bob for 3.0 too");

        // Each version sees the other's registration as already there.
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(via20))).Should().Equal(Added(via20, NoChange));
        (await fx.Update20Async(bob.Did, mediator.Did, Add(via30))).Should().Equal(Added(via30, NoChange));
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(new[] { mediator.Did }, "a 3.0 request re-issues the same grant");

        rig.Store.GrantCount.Should().Be(1);
        rig.Store.RecipientCount.Should().Be(2);
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().BeEquivalentTo(new[] { via20, via30 }, "a 3.0 recipient-query lists 2.0 adds");
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().BeEquivalentTo(new[] { via20, via30 }, "a 2.0 keylist-query lists 3.0 adds");
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task A_peer_enrolled_in_3_0_that_adds_through_2_0_is_one_tenant(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var via20 = (await fx.NewPeerAsync()).Did;
        var via30 = (await fx.NewPeerAsync()).Did;

        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal(mediator.Did);
        (await fx.Update20Async(bob.Did, mediator.Did, Add(via20))).Should().Equal(new[] { Added(via20, Success) }, "the 3.0 grant enrols Bob for 2.0 too");
        (await fx.UpdateAsync(bob.Did, mediator.Did, Add(via30))).Should().Equal(Added(via30, Success));
        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be(mediator.Did);

        rig.Store.GrantCount.Should().Be(1);
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().BeEquivalentTo(new[] { via20, via30 });
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().BeEquivalentTo(new[] { via20, via30 });
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task Each_version_removes_what_the_other_added(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var via20 = (await fx.NewPeerAsync()).Did;
        var via30 = (await fx.NewPeerAsync()).Did;
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add(via20));
        await fx.UpdateAsync(bob.Did, mediator.Did, Add(via30));

        (await fx.Update20Async(bob.Did, mediator.Did, Remove(via30))).Should().Equal(new[] { Removed(via30, Success) }, "a 2.0 remove of a 3.0 add");
        (await fx.UpdateAsync(bob.Did, mediator.Did, Remove(via20))).Should().Equal(new[] { Removed(via20, Success) }, "a 3.0 remove of a 2.0 add");

        rig.Store.RecipientCount.Should().Be(0);
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().BeEmpty();
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().BeEmpty();
        (await rig.Forward.IsRegisteredAsync(via20, default)).Should().BeFalse();
        (await rig.Forward.IsRegisteredAsync(via30, default)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task A_second_peer_can_neither_add_nor_remove_those_dids_through_either_version(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var via20 = (await fx.NewPeerAsync()).Did;
        var via30 = (await fx.NewPeerAsync()).Did;
        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add(via20));
        await fx.UpdateAsync(bob.Did, mediator.Did, Add(via30));
        await fx.EnrolAsync(mallory.Did, mediator.Did);

        foreach (var did in new[] { via20, via30 })
        {
            (await fx.Update20Async(mallory.Did, mediator.Did, Add(did))).Should().Equal(Added(did, ClientError));
            (await fx.Update20Async(mallory.Did, mediator.Did, Remove(did))).Should().Equal(Removed(did, ClientError));
            (await fx.UpdateAsync(mallory.Did, mediator.Did, Add(did))).Should().Equal(Added(did, ClientError));
            (await fx.UpdateAsync(mallory.Did, mediator.Did, Remove(did))).Should().Equal(Removed(did, ClientError));
        }

        (await fx.QueryAsync(mallory.Did, mediator.Did)).Dids.Should().BeEmpty();
        (await fx.Query20Async(mallory.Did, mediator.Did)).Keys.Should().BeEmpty();
        (await fx.Query20Async(bob.Did, mediator.Did)).Keys.Should().BeEquivalentTo(new[] { via20, via30 });
        (await rig.Store.FindGrantByRecipientDidAsync(via20, default))!.PeerDid.Should().Be(bob.Did);
        (await rig.Store.FindGrantByRecipientDidAsync(via30, default))!.PeerDid.Should().Be(bob.Did);
        rig.Store.GrantCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task A_did_added_through_2_0_is_found_by_the_store_and_the_forward_directory(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var via20 = (await fx.NewPeerAsync()).Did;
        (await rig.Forward.IsRegisteredAsync(via20, default)).Should().BeFalse();

        await fx.Enrol20Async(bob.Did, mediator.Did);
        await fx.Update20Async(bob.Did, mediator.Did, Add(via20));

        (await rig.Store.FindGrantByRecipientDidAsync(via20, default))!.PeerDid.Should().Be(bob.Did);
        (await rig.Forward.IsRegisteredAsync(via20, default)).Should().BeTrue("a forward to a DID registered through 2.0 is relayed");
    }

    [Theory]
    [MemberData(nameof(Wirings))]
    public async Task A_peer_enrolled_in_3_0_gets_the_first_routing_did_of_its_stored_grant_from_a_2_0_request(Wiring wiring)
    {
        await using var rig = Create(wiring);
        var fx = rig.Fx;
        rig.Identity.Get = () => new[] { "did:example:routing-1", "did:example:routing-2" };
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        (await fx.EnrolAsync(bob.Did, mediator.Did)).Should().Equal("did:example:routing-1", "did:example:routing-2");

        // The configuration is reordered afterwards: the stored grant decides, not today's list.
        rig.Identity.Get = () => new[] { "did:example:routing-2", "did:example:routing-1" };

        (await fx.Enrol20Async(bob.Did, mediator.Did)).Should().Be("did:example:routing-1");
        rig.Store.GrantCount.Should().Be(1);
    }
}
