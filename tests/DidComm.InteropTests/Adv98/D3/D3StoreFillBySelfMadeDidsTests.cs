using AwesomeAssertions;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Adv98.D3;

/// <summary>
/// #98 D3 E-16b / E-21 (deterministic, TestServer, defaults): under the default open enrolment one
/// stranger mints 50 DIDs, enrols each and forwards 100 tiny messages to each of them, filling the
/// store-wide <c>MaxStoreMessages</c> (5 000). Another tenant's forward is then dropped
/// (<c>Dropped-Quota</c>, still 202) and the filler stays until <c>MaxMessageAge</c> (30 days) or a
/// revoke (a documented limit of open enrolment). Under an allow-list
/// <see cref="IMediationPolicy"/> the stranger gets no grant and the tenant's forward is queued.
/// </summary>
public sealed class D3StoreFillBySelfMadeDidsTests
{
    [Fact]
    public async Task D3_E16b_under_open_enrolment_fifty_self_made_grants_fill_the_store_and_another_tenants_forward_is_dropped()
    {
        using var floor = Qa.RaiseThreadPoolFloor();
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();

        for (var g = 0; g < 50; g++)
        {
            var self = await mediator.NewPeerAsync();
            await mediator.EnrolAsync(self);
            var (packed, _) = await mallory.ForwardAsync(mediator.Did, self.Did, Qa.JsonPayloadOfSize(64));
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => Qa.PostForwardAsync(mediator, packed))));
        }
        await mediator.FlushAsync();
        var filled = mediator.Store.MessageCount;
        var quotaDropsBefore = mediator.Outcome(ForwardOutcomes.DroppedQuota);

        await Qa.SendNoteAsync(mediator, alice, bob);

        filled.Should().Be(5_000, "the store-wide MaxStoreMessages default");
        quotaDropsBefore.Should().Be(0);
        mediator.Outcome(ForwardOutcomes.DroppedQuota).Should().Be(1, "Alice's forward to Bob is dropped while the store is full");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task D3_E16b_under_an_allow_list_policy_the_stranger_gets_no_grant_and_the_tenants_forward_is_queued()
    {
        using var floor = Qa.RaiseThreadPoolFloor();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<IMediationPolicy>(new D3AllowListPolicy(allowed)));
        var bob = await mediator.NewPeerAsync();
        allowed.Add(bob.Did);
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var mallory = await mediator.NewSenderAsync();
        var self = await mediator.NewPeerAsync();

        var answer = await mediator.AskAsync(self, CoordinateMediation.CreateMediateRequest(self.Did, mediator.Did));
        var granted = CoordinateMediation.TryParseGrant(answer.Message, out _);
        var (packed, _) = await mallory.ForwardAsync(mediator.Did, self.Did, Qa.JsonPayloadOfSize(64));
        await Qa.PostForwardAsync(mediator, packed);
        await Qa.SendNoteAsync(mediator, alice, bob);
        await mediator.FlushAsync();

        granted.Should().BeFalse("the allow-list denies the stranger");
        mediator.Outcome(ForwardOutcomes.Queued).Should().Be(1, "only Alice's forward to Bob is queued");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
    }

    private sealed class D3AllowListPolicy(HashSet<string> allowed) : IMediationPolicy
    {
        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
            => new(allowed.Contains(peerDid) ? MediationDecision.Grant : MediationDecision.Deny);

        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
            => new(allowed.Contains(peerDid));
    }
}
