using System.Text.Json;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-1 over the mediator role of Message Pickup 3.0 (#94): identity,
/// authorisation, oracles, hostile input, logs. <strong>Every test in this class is a FINDING: it
/// states what an artifact of the change promises and fails against the product as it is.</strong>
/// Each comment names the claim, who can trigger the behaviour and what they gain. The attacks
/// that were tried and are defeated live in the other <c>MessagePickupAdversarialIdentity…</c>
/// classes.
/// </summary>
public sealed class MessagePickupAdversarialIdentityFindingsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- FINDING 1: a quota eaten by messages nobody can see ---------------------------------

    // ---- FINDING 2: "never delivered" ends when the revoked peer asks again -------------------

    // ---- FINDING 3: the one pickup record that prints its secrets ------------------------------

    [Fact]
    public void A_printed_lease_carries_neither_its_owner_token_nor_its_id()
    {
        // FINDING (low; host code only). Every other public record of the store contract overrides
        // ToString so that "a logged instance carries no wire value" (QueuedMessage,
        // MessageLeaseRequest — which goes out of its way to leave its Lease out — PickupStatus,
        // DeliveredMessage, PickupDeliveryResult). MessageLease keeps the compiler's: it prints
        // Owner and Id. PickupSessionState.OwnerToken is documented "never on the wire, never
        // logged", and the lease id is the capability ReleaseAsync checks ("a lease is given back
        // only by its taker"). A store that logs the lease it was handed — the first thing a host
        // writing a durable IMessageStore does — writes both.
        var lease = new MessageLease("adv1-owner-token", DateTimeOffset.UnixEpoch.AddHours(1)) { Id = "adv1-lease-id" };

        foreach (var printed in new[] { lease.ToString(), $"{lease}", string.Format("{0}", lease) })
        {
            printed.Should().NotContain("adv1-owner-token", "the owner token is documented as never logged");
            printed.Should().NotContain("adv1-lease-id", "the lease id is what lets a caller end the lease");
        }
    }

    // ---- FINDING 4: a limit read "without allocation" allocates twice its literal ---------------

    // ---- FINDING 5: the delivery-time purge removes more than the check looked at ---------------

    [Fact]
    public async Task A_message_admitted_after_the_registration_check_read_its_answer_is_not_purged_with_the_stale_ones()
    {
        // FINDING (L-039: the check and the use read two different things). In an unfiltered batch
        // DropUnregisteredAsync looks up each recipient DID of the LEASED records and then calls
        // PurgeRecipientAsync(grant, did) — which removes every record of that DID under the
        // grant, including records that were not in the batch and did not exist when the lookup
        // answered. A message DeliverAsync admitted under a valid registration in between (and
        // reported Queued to the pipeline: "retained") is destroyed without ever being offered.
        //
        // Who: nobody can force it from outside; it takes the recipient registering the DID again
        // (a keylist re-sync: remove, then add) while one of its own delivery-requests is between
        // the lookup and the purge, and a forward arriving in that window. In memory the window is
        // microseconds; with a durable IMediationStore / IMessageStore it is two round trips.
        // Gain: none for an attacker; for the recipient, a message lost beyond the documented
        // at-least-once. The seam below makes the interleaving deterministic: no threads, no sleeps.
        var mediation = new MessagePickupAdversarialIdentityMediationStore();
        await using var fx = PickupFixture.Create(services: collection => collection.AddSingleton<IMediationStore>(mediation));
        var mediator = (await fx.NewPeerAsync()).Did;
        var bob = (await fx.NewPeerAsync()).Did;
        const string contactDid = "did:example:adv1-contact";
        await fx.EnrolAsync(bob, mediator, contactDid);
        await fx.QueueAsync(contactDid, PickupFixture.Payload(16, 1));
        await mediation.Inner.UpdateRecipientsAsync(bob, new[] { new RecipientUpdate(contactDid, RecipientUpdateAction.Remove) }, None);

        // Right after the delivery-time lookup has read "not registered to Bob": Bob's re-sync adds
        // the DID again and a forward for it arrives.
        PickupDeliveryResult? admitted = null;
        var fired = false;
        mediation.AfterFind = async did =>
        {
            if (fired || did != contactDid)
                return;
            fired = true;
            await mediation.Inner.UpdateRecipientsAsync(bob, new[] { new RecipientUpdate(contactDid, RecipientUpdateAction.Add) }, None);
            admitted = await fx.Queue.DeliverAsync(contactDid, PickupFixture.Payload(24, 2));
        };

        await fx.FetchAsync(bob, mediator, fx.Session(bob, mediator), 10);

        fired.Should().BeTrue("the unfiltered delivery checks the registration of what it leased");
        admitted!.Value.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the DID was registered to Bob when this message arrived");
        (await mediation.Inner.FindGrantByRecipientDidAsync(contactDid, None))!.PeerDid.Should().Be(bob);
        fx.Memory.MessageCount.Should().Be(1,
            "the stale message is rightly dropped, but the one admitted under a valid registration was reported as retained and must still be there");
    }
}
