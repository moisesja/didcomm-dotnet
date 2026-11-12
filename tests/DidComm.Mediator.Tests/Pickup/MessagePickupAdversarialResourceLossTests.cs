using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 2: lose a message, or deliver one twice beyond the documented at-least-once (a
/// lease that ran out, a connection closed mid-delivery). Every interleaving is driven through a
/// rendezvous; no assertion depends on which racer won.
/// </summary>
public sealed class MessagePickupAdversarialResourceLossTests
{
    public MessagePickupAdversarialResourceLossTests() => MessagePickupAdversarialResource.RaiseThreadPoolFloor();

    // ---- the delivery-time registration purge (FR-PICK-03, owner decision) ---------------------

    /// <summary>
    /// The delivery-time check reads the registration of a DID, and the purge that follows removes
    /// EVERY record of that DID under the grant — not the records the request leased. Between the
    /// two the recipient registers the DID again and a new message for it is accepted
    /// (<c>Queued</c>, an id handed out). The purge then deletes that message too: accepted, never
    /// delivered, gone. The check and the use read different things (L-039).
    /// </summary>
    [Fact]
    public async Task A_message_accepted_after_its_did_was_registered_again_is_not_deleted_by_a_delivery_request_that_saw_the_did_gone_a_moment_earlier()
    {
        var mediation = new MessagePickupAdversarialResourceMediation();
        await using var fixture = PickupFixture.Create(services: c => c.AddSingleton<IMediationStore>(mediation));
        var mediator = (await fixture.NewPeerAsync()).Did;
        var peer = (await fixture.NewPeerAsync()).Did;
        var second = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(peer, mediator, peer, second);

        // One message for the second DID, then the recipient removes that DID from its grant.
        var stale = await fixture.QueueAsync(second, PickupFixture.Payload(100, 1));
        await mediation.Inner.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(second, RecipientUpdateAction.Remove) }, CancellationToken.None);

        // An unfiltered delivery-request: its delivery-time check of the second DID gets its answer
        // ("not registered to this peer") and is parked before the role acts on it.
        var check = mediation.ParkNextFindAfterItsAnswer(second);
        var session = fixture.Session(peer, mediator);
        var request = fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session);
        await check.Arrived;

        // Meanwhile: the DID is registered again, and a message for it is accepted.
        await mediation.Inner.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(second, RecipientUpdateAction.Add) }, CancellationToken.None);
        var fresh = await fixture.Queue.DeliverAsync(second, PickupFixture.Payload(200, 2));
        fresh.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the DID is registered to the peer when this message arrives");

        check.Resume();
        await request;

        // The stale record goes (it was queued for a registration that ended) — that record and no
        // other. The fresh one was accepted under a valid registration: the same request comes to it
        // in its next round, checks it on its own, and delivers it. It is still stored (a delivered
        // message is removed only by its acknowledgement, its expiry or a host purge).
        var delivered = session.Sent.Should().ContainSingle().Subject;
        MessagePickup.TryParseDelivery(delivered, out var messages, out _).Should().BeTrue();
        messages!.Select(m => m.Id).Should().Equal(fresh.MessageId);
        fixture.Memory.MessageCount.Should().Be(1, "only the stale record was removed");
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().BeEmpty("the fresh message is under the lease of the request that delivered it, and the stale one is gone");
        _ = stale;
    }

    /// <summary>
    /// The mirror image, which must hold: a record leased by the request for a DID that is gone at
    /// the check is not delivered, whatever happens to the registration afterwards.
    /// </summary>
    [Fact]
    public async Task A_record_whose_did_was_gone_at_the_check_is_not_delivered_even_when_the_did_is_registered_again_before_the_send()
    {
        var mediation = new MessagePickupAdversarialResourceMediation();
        await using var fixture = PickupFixture.Create(services: c => c.AddSingleton<IMediationStore>(mediation));
        var mediator = (await fixture.NewPeerAsync()).Did;
        var peer = (await fixture.NewPeerAsync()).Did;
        var second = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(peer, mediator, peer, second);
        var own = await fixture.QueueAsync(peer, PickupFixture.Payload(50, 1));
        var stale = await fixture.QueueAsync(second, PickupFixture.Payload(100, 2));
        await mediation.Inner.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(second, RecipientUpdateAction.Remove) }, CancellationToken.None);

        var check = mediation.ParkNextFindAfterItsAnswer(second);
        var session = fixture.Session(peer, mediator);
        var request = fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session);
        await check.Arrived;
        await mediation.Inner.UpdateRecipientsAsync(peer, new[] { new RecipientUpdate(second, RecipientUpdateAction.Add) }, CancellationToken.None);
        check.Resume();
        await request;

        session.Sent.Should().ContainSingle();
        MessagePickup.TryParseDelivery(session.Sent.Single(), out var delivered, out _).Should().BeTrue();
        delivered!.Select(m => m.Id).Should().Equal(own);
        delivered!.Select(m => m.Id).Should().NotContain(stale);
    }

    // ---- what a store hands out twice (host seam; L-054) ---------------------------------------

    /// <summary>
    /// "What a store hands out is not trusted further than its contract … One wrong record and
    /// nothing of the batch is delivered." A store that returns the SAME record twice in one lease
    /// is outside the contract ("a record is in exactly one place"), yet the batch goes out with two
    /// attachments under one id.
    /// </summary>
    [Fact]
    public async Task A_lease_that_names_the_same_record_twice_is_outside_the_contract_and_nothing_of_it_is_put_on_the_wire()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var first = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1));
        await fixture.QueueAsync(peer, PickupFixture.Payload(40, 2));
        var store = fixture.FakeStore!;
        store.OnLease = async (request, ct) =>
        {
            var taken = await store.Inner.LeaseAsync(request with { MaxMessages = 1 }, ct);
            return new[] { taken[0], taken[0] };
        };

        var session = fixture.Session(peer, mediator);
        var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, 2), mediator, session);

        session.Sent.Should().BeEmpty("a delivery with two attachments of one id acknowledges ambiguously and was never a valid lease");
        outcome.Reply.Should().BeNull("a store fault is answered with nothing (JC-15)");

        // And the lease the store did hand out is given back: the record is deliverable at once.
        store.OnLease = null;
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator, 1)).Should().Equal(first);
    }

    /// <summary>
    /// The damaging half of the same fault: when such a batch is over the byte budget the trim gives
    /// "the last" record back — which is also the first — and then delivers it. The message is on
    /// the wire and, in the store, deliverable again at once: a second delivery inside what the
    /// recipient was told is a live lease, with no lease having run out and no connection closed.
    /// </summary>
    [Fact]
    public async Task A_trimmed_batch_never_delivers_a_message_whose_lease_it_has_just_given_back()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1));
        var store = fixture.FakeStore!;
        store.OnLease = async (request, ct) =>
        {
            var taken = await store.Inner.LeaseAsync(request with { MaxMessages = 1 }, ct);
            return taken.Count == 0 ? taken : new[] { taken[0], taken[0] };
        };

        // The session refuses the two-attachment delivery as too large and takes the trimmed one.
        var session = fixture.Session(peer, mediator);
        var sends = 0;
        session.OnSend = (_, _) => Interlocked.Increment(ref sends) == 1 ? ReturnRouteSendResult.TooLarge : null;
        await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, 2), mediator, session);

        var deliveredFirst = session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var d, out _) ? d! : Array.Empty<DeliveredMessage>()).Select(m => m.Id).ToArray();

        // A second request, at the same instant, over another connection: whatever the first one
        // put on the wire is under its lease and must not be handed out again.
        store.OnLease = null;
        var deliveredSecond = await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator);

        deliveredFirst.Intersect(deliveredSecond).Should().BeEmpty(
            "a message delivered by one request is leased until it is acknowledged, its lease runs out or its connection closes (FR-PICK-04); delivered first: {0}, the queued id: {1}",
            deliveredFirst.Length, id.Length);
    }
}
