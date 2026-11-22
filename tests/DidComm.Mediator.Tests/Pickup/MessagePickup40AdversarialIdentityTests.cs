using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-3 over the mediator role of Message Pickup 4.0 (#100): identity. Can an
/// enrolled peer (Mallory) read, lease, acknowledge or switch live delivery on for another
/// recipient's mailbox through any 4.0 message, or tell from a 4.0 answer whether a DID is
/// registered under another grant? Every test here is an attack that was tried and defeated; they
/// are kept as regression tests.
/// </summary>
public sealed class MessagePickup40AdversarialIdentityTests
{
    private const string VictimsDid = "did:example:Bob-1";

    // Built with casts, not escapes: the characters must be in the string, whatever wrote this file.
    private static readonly Dictionary<string, string> Spellings = new(StringComparer.Ordinal)
    {
        ["a fragment"] = VictimsDid + "#key-1",
        ["a query"] = VictimsDid + "?versionId=1",
        ["a path"] = VictimsDid + "/",
        ["a parameter"] = VictimsDid + ";service=x",
        ["a trailing space"] = VictimsDid + " ",
        ["a trailing NUL"] = VictimsDid + (char)0x00,
        ["an upper-case scheme"] = "DID:example:Bob-1",
        ["an upper-case method"] = "did:EXAMPLE:Bob-1",
        ["a lower-case id"] = "did:example:bob-1",
        ["an upper-case id"] = "did:example:BOB-1",
        ["a percent-encoded letter"] = "did:example:%42ob-1",
        ["a percent-encoded hyphen"] = "did:example:Bob%2D1",
        ["a full-width letter"] = "did:example:" + (char)0xFF22 + "ob-1",
        ["a Cyrillic look-alike"] = "did:example:" + (char)0x0412 + "ob-1",
        ["a zero-width joiner"] = "did:example:Bob" + (char)0x200D + "-1",
        ["a combining mark"] = "did:example:Bob" + (char)0x0301 + "-1",
        ["a byte-order mark"] = (char)0xFEFF + VictimsDid,
        ["a Kelvin sign for a k"] = "did:example:Bo" + (char)0x212A + "-1",
        ["a long s for an s"] = "did:exampl" + (char)0x017F + ":Bob-1",
    };

    public static TheoryData<string> SpellingNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Spellings.Keys)
                data.Add(name);
            return data;
        }
    }

    // ---- 1. a 4.0 filter in any spelling of the victim's DID sees an empty mailbox ------------

    [Theory]
    [MemberData(nameof(SpellingNames))]
    public async Task No_spelling_of_the_victims_did_in_a_4_0_request_shows_mallory_its_mailbox(string what)
    {
        // Defeated: 4.0 reaches the same ownership check as 3.0, so a look-alike is either not a
        // bare DID (never looked up) or another DID (one lookup, not hers): an empty status, an
        // empty delivery repeating the value as sent, and the message store is never consulted.
        var spelling = Spellings[what];
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        await h.QueueAsync(VictimsDid, 40, 1);
        await h.QueueAsync(VictimsDid, 50, 2);
        var session = h.Live(mallory);
        h.ResetTraces();

        var statusRequest = h.StatusRequest40(mallory, spelling);
        var status = await h.SendAsync(statusRequest, session);
        var deliveryRequest = h.DeliveryRequest40(mallory, 10, spelling);
        var delivery = await h.SendAsync(deliveryRequest, session);

        status.ShouldBeStatus40(statusRequest, h.Mediator, mallory, count: 0, bytes: 0, live: false, recipientDid: spelling);
        delivery.ShouldBeEmptyDelivery40(deliveryRequest, h.Mediator, mallory, spelling);
        session.Attempts.Should().BeEmpty("nothing is pushed for a scope the peer does not own");
        h.StoreTrace.Should().BeEmpty("the message store is never consulted for a scope the peer does not own ({0})", what);
        h.MediationTrace.Should().OnlyContain(call => call == nameof(IMediationStore.FindGrantByRecipientDidAsync));
        h.MediationTrace.Length.Should().BeLessThanOrEqualTo(2, "at most one lookup per request");
        (await h.DeliverableAsync(victim)).Should().HaveCount(2, "nothing of the victim's was leased");
    }

    // ---- 2. "registered elsewhere" and "registered nowhere" look and cost the same ----------

    [Fact]
    public async Task A_4_0_answer_about_a_did_registered_elsewhere_is_the_answer_about_one_registered_nowhere()
    {
        // Defeated. The probe set: the victim's registered DID, the victim's own peer DID (a grant,
        // not a registration), the mediator's DID, a DID nobody registered. Each 4.0 status-request
        // and delivery-request gets the empty answer with the value repeated, after exactly one
        // registry lookup and no message-store call: nothing distinguishes the four, in shape or
        // in the work done.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        await h.QueueAsync(VictimsDid, 40, 1);
        var session = h.Live(mallory);
        var probes = new[] { VictimsDid, victim, h.Mediator, "did:example:Nobody-1" };
        var traces = new List<string>();

        foreach (var probe in probes)
        {
            h.ResetTraces();
            var statusRequest = h.StatusRequest40(mallory, probe);
            (await h.SendAsync(statusRequest, session)).ShouldBeStatus40(statusRequest, h.Mediator, mallory, 0, 0, live: false, recipientDid: probe);
            var deliveryRequest = h.DeliveryRequest40(mallory, 10, probe);
            (await h.SendAsync(deliveryRequest, session)).ShouldBeEmptyDelivery40(deliveryRequest, h.Mediator, mallory, probe);
            h.StoreTrace.Should().BeEmpty();
            session.Attempts.Should().BeEmpty();
            traces.Add(string.Join(",", h.MediationTrace));
        }

        traces.Distinct().Should().ContainSingle("the same work is done for each probe")
            .Which.Should().Be(nameof(IMediationStore.FindGrantByRecipientDidAsync) + "," + nameof(IMediationStore.FindGrantByRecipientDidAsync));
    }

    // ---- 3. a one-way acknowledgement naming another grant's ids ------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_4_0_messages_received_naming_the_victims_ids_removes_nothing_and_does_what_it_does_for_unknown_ids(bool live, bool returnRouteAll)
    {
        // Defeated. 4.0's acknowledgement is not answered, so the only observable is what it does:
        // the one store call is keyed by Mallory, with her list as sent, whether the ids are the
        // victim's (one under an HTTP lease, one waiting) or made up; the victim's records and
        // lease are untouched; and Mallory's next status is the same either way.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        var leased = await h.QueueAsync(VictimsDid, 40, 1);
        (await h.Fixture.FetchAsync(victim, h.Mediator, h.Http(victim), 1))!.Value.Messages.Select(m => m.Id).Should().Equal(leased);
        var waiting = await h.QueueAsync(VictimsDid, 50, 2);
        var session = live ? h.Live(mallory) : h.Http(mallory);
        var victims = new[] { leased, waiting };
        var madeUp = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        var observed = new List<string>();

        foreach (var ids in new[] { victims, madeUp, victims.Select(id => id.ToUpperInvariant()).ToArray() })
        {
            h.ResetTraces();
            var ack = h.MessagesReceived40(mallory, ids);
            if (returnRouteAll)
                ack.WithReturnRouteAll();
            (await h.SendAsync(ack, session)).ShouldBeNoReply40();
            var acknowledged = h.Store.Acknowledged.Should().ContainSingle().Subject;
            acknowledged.GrantPeerDid.Should().Be(mallory, "the store call is keyed by the sender's grant");
            acknowledged.Ids.Should().Equal(ids);
            var trace = string.Join(",", h.StoreTrace) + "|" + string.Join(",", h.MediationTrace);
            var statusRequest = h.StatusRequest40(mallory);
            (await h.SendAsync(statusRequest, session)).ShouldBeStatus40(statusRequest, h.Mediator, mallory, 0, 0, live: false);
            observed.Add(trace);
        }

        observed.Distinct().Should().ContainSingle("the victim's ids and made-up ids take the same path");
        session.Attempts.Should().BeEmpty();
        (await h.StoredAsync(victim)).Should().Be(2, "nothing of the victim's was removed");
        (await h.DeliverableAsync(victim)).Should().Equal(new[] { waiting }, "the victim's HTTP lease still holds");
    }

    // ---- 4. another pair's session carries nothing of Mallory's -------------------------------

    [Fact]
    public async Task A_4_0_request_of_mallory_handed_the_victims_live_session_neither_reads_nor_switches_it()
    {
        // Defeated. The dispatcher hands Mallory's requests the victim's live session (a host that
        // mixes up connections). The handler takes the session only when it is bound to the
        // sender's own pair: Mallory's live-delivery-change(false) does not turn the victim's
        // pushes off, (true) is answered with the 4.0 problem report, her delivery-request is
        // not answered and leases nothing, and nothing is sent on the victim's socket.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        var victimsSocket = h.Fixture.Session(victim, h.Mediator, live: true);
        (await h.SendAsync(h.LiveChange40(victim, true), victimsSocket)).Status40().LiveDelivery.Should().BeTrue();
        var waiting = await h.QueueAsync(VictimsDid, 40, 1, new PickupDeliveryOptions(AllowLive: false));
        h.ResetTraces();

        var off = h.LiveChange40(mallory, false);
        (await h.SendAsync(off, victimsSocket)).ShouldBeStatus40(off, h.Mediator, mallory, 0, 0, live: false);
        var on = h.LiveChange40(mallory, true);
        (await h.SendAsync(on, victimsSocket)).ShouldBeLiveModeNotSupported40(on, h.Mediator, mallory);
        (await h.SendAsync(h.DeliveryRequest40(mallory, 10), victimsSocket)).ShouldBeNoReply40();
        (await h.SendAsync(h.DeliveryRequest40(mallory, 10, VictimsDid), victimsSocket)).ShouldBeNoReply40();
        (await h.SendAsync(h.MessagesReceived40(mallory, new[] { waiting }), victimsSocket)).ShouldBeNoReply40();

        victimsSocket.Attempts.Should().BeEmpty("nothing of Mallory's travels on the victim's socket");
        h.Store.LeaseRequests.Should().BeEmpty();
        h.Store.Acknowledged.Should().OnlyContain(a => a.GrantPeerDid == mallory);
        (await h.LiveFlagAsync(victimsSocket)).Should().BeTrue("the victim's live delivery is still on");
        var arrival = await h.Fixture.Queue.DeliverAsync(VictimsDid, PickupFixture.Payload(60, 2));
        arrival.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "the victim's session still takes pushes");
        victimsSocket.Sent.Should().ContainSingle().Which.Type.Should().Be(MessagePickup40Assert.DeliveryType);
        (await h.DeliverableAsync(victim)).Should().Equal(waiting);

        // Nothing of either tenant shows up in any log line of any category.
        var secrets = new[] { victim, mallory, h.Mediator, VictimsDid, waiting, arrival.MessageId!, off.Id, on.Id };
        foreach (var line in h.Fixture.Logs.Entries)
        {
            foreach (var secret in secrets)
                line.Everything.Should().NotContain(secret, "log lines carry no DID, queue id or thread id ({0})", line.Category);
        }
    }

    // ---- 5. a stranger repeating 4.0 messages is logged at a bounded rate --------------------

    [Fact]
    public async Task A_stranger_flooding_unauthenticated_4_0_requests_raises_one_warning_per_thousand()
    {
        // Defeated. Anyone can send plaintext; the drop line is shared with 3.0 and rate-limited:
        // the first and every 1000th at Warning, the rest at Debug, none carrying the sender.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, VictimsDid);
        var queued = await h.QueueAsync(VictimsDid, 40, 1);
        var names = new Func<Message>[]
        {
            () => h.StatusRequest40(h.Alice, VictimsDid),
            () => h.DeliveryRequest40(h.Alice, 10, VictimsDid),
            () => h.MessagesReceived40(h.Alice, new[] { queued }),
            () => h.LiveChange40(h.Alice, true),
        };
        h.ResetTraces();

        for (var i = 0; i < 1001; i++)
            (await h.SendAsync(names[i % names.Length](), h.Live(h.Alice), HandlerEnvelope.Plaintext)).ShouldBeNoReply40();

        var drops = h.Fixture.PickupLogs.Where(e => e.Formatted.Contains("no authcrypt sender", StringComparison.Ordinal)).ToArray();
        drops.Should().HaveCount(1001);
        drops.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Should().Be(2, "the first and the 1000th");
        foreach (var line in drops)
            line.Everything.Should().NotContain("did:");
        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(queued);
    }
}

/// <summary>Queue helper with options, for the tests above.</summary>
internal static class MessagePickup40AdversarialIdentityHarness
{
    public static Task<string> QueueAsync(this MessagePickupHandlerHarness h, string recipientDid, int size, byte seed, PickupDeliveryOptions options)
        => h.Fixture.QueueAsync(recipientDid, PickupFixture.Payload(size, seed), options);
}
