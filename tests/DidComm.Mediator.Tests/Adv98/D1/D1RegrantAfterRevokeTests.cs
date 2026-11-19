using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Mediator.Tests.Adv98.D1;

/// <summary>
/// #98 adversarial pass, dimension D1: a grant revoked through <see cref="IMediationStore.RevokeAsync"/>
/// alone (no purge), a DID it held squatted by Mallory meanwhile, then the same peer granted again
/// over the wire. The leftovers queued for the freed DID stay under the revoked peer's key
/// (docs/codebase-architecture.md "Revoked grants"); they must never be shown or delivered to
/// Mallory, and the returning peer's unfiltered pickup removes the leftover for the DID it no longer
/// holds rather than delivering it.
/// </summary>
public sealed class D1RegrantAfterRevokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Leftovers_of_a_revoked_grant_never_reach_the_squatter_and_are_dropped_not_delivered_on_regrant()
    {
        var policy = new D1CountingPolicy();
        await using var fx = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationPolicy>(policy));
        var mediator = (await fx.NewPeerAsync()).Did;
        var bob = (await fx.NewPeerAsync()).Did;
        var mallory = (await fx.NewPeerAsync()).Did;
        var x = (await fx.NewPeerAsync()).Did;
        var bobsOther = (await fx.NewPeerAsync()).Did;
        var mediation = fx.Host.Services.GetRequiredService<IMediationStore>();

        // Bob enrols over the wire (the policy is asked once) and registers X and another DID.
        await GrantAsync(fx, bob, mediator);
        (await UpdateAsync(fx, bob, mediator, Add(x), Add(bobsOther))).Select(r => r.Result)
            .Should().Equal(RecipientUpdateOutcome.Success, RecipientUpdateOutcome.Success);
        var leftover = await fx.QueueAsync(x, PickupFixture.Payload(40, 1));
        var leftoverOther = await fx.QueueAsync(bobsOther, PickupFixture.Payload(41, 2));
        policy.Requests.Should().Be(1);

        // Revoked by the host without a purge: the records stay under Bob's key.
        (await mediation.RevokeAsync(bob, default)).Should().BeTrue();
        (await StoredAsync(fx, bob)).Should().Be(2);
        (await mediation.FindGrantByRecipientDidAsync(x, default)).Should().BeNull("revoking frees the grant's DIDs");

        // Mallory enrols and squats X (first come, first served; X is free).
        await GrantAsync(fx, mallory, mediator);
        (await UpdateAsync(fx, mallory, mediator, Add(x))).Single().Result.Should().Be(RecipientUpdateOutcome.Success);
        var mallorysOwn = await fx.QueueAsync(x, PickupFixture.Payload(42, 3));

        // Mallory looks for X every way she can: she sees only what arrived while X was hers.
        var mallorysStatus = await fx.StatusOfAsync(MessagePickup.CreateStatusRequest(mallory, mediator), mediator, fx.Session(mallory, mediator));
        var mallorysStatusForX = await fx.StatusOfAsync(MessagePickup.CreateStatusRequest(mallory, mediator, x), mediator, fx.Session(mallory, mediator));
        var mallorysFetch = await fx.FetchAsync(mallory, mediator, fx.Session(mallory, mediator), 10);
        fx.Clock.Advance(TimeSpan.FromSeconds(61));
        var mallorysFetchForX = await fx.FetchAsync(mallory, mediator, fx.Session(mallory, mediator), 10, x);
        fx.Clock.Advance(TimeSpan.FromSeconds(61));
        var mallorysAck = await fx.StatusOfAsync(MessagePickup.CreateMessagesReceived(mallory, mediator, new[] { leftover, leftoverOther }), mediator, fx.Session(mallory, mediator));
        output.WriteLine($"Mallory: status {mallorysStatus.MessageCount}, status(X) {mallorysStatusForX.MessageCount}, fetch [{Ids(mallorysFetch)}], fetch(X) [{Ids(mallorysFetchForX)}], after acking Bob's ids {mallorysAck.MessageCount}");

        mallorysStatus.MessageCount.Should().Be(1);
        mallorysStatusForX.MessageCount.Should().Be(1);
        Ids(mallorysFetch).Should().Be(mallorysOwn);
        Ids(mallorysFetchForX).Should().Be(mallorysOwn);
        (await StoredAsync(fx, bob)).Should().Be(2, "Mallory's acknowledgement of Bob's ids removed nothing of Bob's");

        // Bob is granted again: no grant exists, so the policy is asked again (a new grant).
        await GrantAsync(fx, bob, mediator);
        policy.Requests.Should().Be(3, "Bob, Mallory, then Bob again: an allow-list policy is consulted on every new grant");
        (await mediation.ListRecipientsAsync(bob, 0, 10, default)).Total.Should().Be(0, "the new grant starts with no registrations");

        // Bob's unfiltered pickup: neither leftover is his to receive any more (X is Mallory's, the
        // other DID nobody's); both are removed under Bob's own key and nothing is delivered.
        fx.FakeStore!.Acknowledged.Clear();
        var bobsStatus = await fx.StatusOfAsync(MessagePickup.CreateStatusRequest(bob, mediator), mediator, fx.Session(bob, mediator));
        var bobsFetch = await fx.FetchAsync(bob, mediator, fx.Session(bob, mediator), 10);
        var bobsFetchForX = await fx.FetchAsync(bob, mediator, fx.Session(bob, mediator), 10, x);
        output.WriteLine($"Bob after re-grant: status {bobsStatus.MessageCount} (counts the stored leftovers), fetch [{Ids(bobsFetch)}], fetch(X) [{Ids(bobsFetchForX)}]; acknowledged: {string.Join("; ", fx.FakeStore.Acknowledged.Select(a => $"{(a.GrantPeerDid == bob ? "Bob" : "other")}:{a.Ids.Count}"))}");

        bobsFetch.Should().BeNull("a leftover for a DID the peer no longer holds is removed, not delivered");
        bobsFetchForX.Should().BeNull("X is Mallory's now");
        fx.FakeStore.Acknowledged.Should().OnlyContain(a => a.GrantPeerDid == bob);
        fx.FakeStore.Acknowledged.SelectMany(a => a.Ids).Should().BeEquivalentTo(new[] { leftover, leftoverOther });
        (await StoredAsync(fx, bob)).Should().Be(0);
        (await StoredAsync(fx, mallory)).Should().Be(1, "Bob's clean-up never touches Mallory's queue");
        (await mediation.FindGrantByRecipientDidAsync(x, default))!.PeerDid.Should().Be(mallory);
    }

    /// <summary>
    /// The case the "Revoked grants" note describes: the same peer granted again gets its leftovers
    /// only if it registers the same DIDs again before its next unfiltered pickup; until then a
    /// status counts them.
    /// </summary>
    [Fact]
    public async Task Leftovers_come_back_only_when_the_regranted_peer_registers_the_same_did_again()
    {
        await using var fx = PickupFixture.Create(fakeStore: true);
        var mediator = (await fx.NewPeerAsync()).Did;
        var bob = (await fx.NewPeerAsync()).Did;
        var x = (await fx.NewPeerAsync()).Did;
        var mediation = fx.Host.Services.GetRequiredService<IMediationStore>();
        await GrantAsync(fx, bob, mediator);
        await UpdateAsync(fx, bob, mediator, Add(x));
        var leftover = await fx.QueueAsync(x, PickupFixture.Payload(40, 1));
        (await mediation.RevokeAsync(bob, default)).Should().BeTrue();

        await GrantAsync(fx, bob, mediator);
        var statusBefore = await fx.StatusOfAsync(MessagePickup.CreateStatusRequest(bob, mediator), mediator, fx.Session(bob, mediator));
        (await UpdateAsync(fx, bob, mediator, Add(x))).Single().Result.Should().Be(RecipientUpdateOutcome.Success);
        var fetched = await fx.FetchAsync(bob, mediator, fx.Session(bob, mediator), 10);
        output.WriteLine($"re-granted Bob: status before re-registering {statusBefore.MessageCount}; after re-registering X, fetch [{Ids(fetched)}]");

        statusBefore.MessageCount.Should().Be(1);
        Ids(fetched).Should().Be(leftover);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static RecipientUpdate Add(string did) => new(did, RecipientUpdateAction.Add);

    private static string Ids((IReadOnlyList<DeliveredMessage> Messages, Message Delivery)? fetched)
        => fetched is null ? "" : string.Join(",", fetched.Value.Messages.Select(m => m.Id));

    private static async Task<int> StoredAsync(PickupFixture fx, string grantPeerDid)
        => (await fx.Memory.GetStatusAsync(grantPeerDid, null, fx.Clock.Now, CancellationToken.None)).MessageCount;

    private static async Task GrantAsync(PickupFixture fx, string peer, string mediator)
    {
        var outcome = await fx.SendAsync(CoordinateMediation.CreateMediateRequest(peer, mediator), mediator);
        if (outcome.Reply is null || !CoordinateMediation.TryParseGrant(outcome.Reply, out _))
            throw new InvalidOperationException($"No grant ({outcome.Result}).");
    }

    private static async Task<IReadOnlyList<RecipientUpdateResult>> UpdateAsync(PickupFixture fx, string peer, string mediator, params RecipientUpdate[] updates)
    {
        var outcome = await fx.SendAsync(CoordinateMediation.CreateRecipientUpdate(peer, mediator, updates), mediator);
        if (outcome.Reply is null || !CoordinateMediation.TryParseRecipientUpdateResponse(outcome.Reply, out var updated))
            throw new InvalidOperationException($"No recipient-update-response ({outcome.Result}).");
        return updated;
    }

    private sealed class D1CountingPolicy : IMediationPolicy
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        public ValueTask<MediationDecision> OnMediateRequestAsync(string peerDid, ProtocolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return new ValueTask<MediationDecision>(MediationDecision.Grant);
        }

        public ValueTask<bool> MayRegisterAsync(string peerDid, string recipientDid, ProtocolContext context, CancellationToken ct)
            => new(true);
    }
}
