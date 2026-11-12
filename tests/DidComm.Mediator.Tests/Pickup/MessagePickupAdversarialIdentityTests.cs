using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-1 over the mediator role of Message Pickup 3.0 (#94), part 1: can an enrolled
/// peer (Mallory) or a sender read, delete, probe or purge another recipient's queue? Every test
/// here is an attack that was tried and is <strong>defeated</strong>; they are kept as regression
/// tests. The attacks that succeeded are in <see cref="MessagePickupAdversarialIdentityFindingsTests"/>.
/// </summary>
public sealed class MessagePickupAdversarialIdentityTests
{
    private const string VictimsDid = "did:example:Bob-1";
    private static readonly CancellationToken None = CancellationToken.None;

    // Built with casts, not escapes: the characters must be in the string, whatever wrote this file.
    private static readonly Dictionary<string, string> Spellings = new(StringComparer.Ordinal)
    {
        ["a fragment"] = VictimsDid + "#key-1",
        ["an empty fragment"] = VictimsDid + "#",
        ["a query"] = VictimsDid + "?versionId=1",
        ["an empty query"] = VictimsDid + "?",
        ["a path"] = VictimsDid + "/",
        ["a parameter"] = VictimsDid + ";service=x",
        ["a leading space"] = " " + VictimsDid,
        ["a trailing space"] = VictimsDid + " ",
        ["a trailing newline"] = VictimsDid + (char)0x0A,
        ["a trailing carriage return"] = VictimsDid + (char)0x0D,
        ["a trailing tab"] = VictimsDid + (char)0x09,
        ["a trailing NUL"] = VictimsDid + (char)0x00,
        ["a trailing colon"] = VictimsDid + ":",
        ["a doubled colon"] = "did:example::Bob-1",
        ["an upper-case scheme"] = "DID:example:Bob-1",
        ["an upper-case method"] = "did:EXAMPLE:Bob-1",
        ["a lower-case id"] = "did:example:bob-1",
        ["a percent-encoded letter"] = "did:example:%42ob-1",
        ["a percent-encoded hyphen"] = "did:example:Bob%2D1",
        ["a full-width letter"] = "did:example:" + (char)0xFF22 + "ob-1",
        ["a Cyrillic look-alike"] = "did:example:" + (char)0x0412 + "ob-1",
        ["a zero-width joiner"] = "did:example:Bob" + (char)0x200D + "-1",
        ["a combining mark"] = "did:example:Bob" + (char)0x0301 + "-1",
        ["a byte-order mark"] = (char)0xFEFF + VictimsDid,
        ["a right-to-left override"] = VictimsDid + (char)0x202E,
        ["an astral character"] = VictimsDid + char.ConvertFromUtf32(0x1F600),
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

    private static RecipientUpdate[] Add(string did) => new[] { new RecipientUpdate(did, RecipientUpdateAction.Add) };

    private static RecipientUpdate[] Remove(string did) => new[] { new RecipientUpdate(did, RecipientUpdateAction.Remove) };

    private static void ShouldBeTheEmptyQueueOf(DispatchOutcome outcome, string value, bool live = false)
    {
        outcome.Reply.Should().NotBeNull();
        outcome.Reply!.Type.Should().Be(MessagePickup.StatusType);
        (outcome.Reply.Attachments?.Count ?? 0).Should().Be(0);
        outcome.Reply.Body!.Select(p => p.Key).Should().BeEquivalentTo("recipient_did", "message_count", "total_bytes", "live_delivery");
        var status = outcome.Status();
        status.RecipientDid.Should().Be(value, "the filter is echoed exactly as it was sent");
        status.MessageCount.Should().Be(0);
        status.TotalBytes.Should().Be(0);
        status.LongestWaitedSeconds.Should().BeNull();
        status.NewestReceivedTime.Should().BeNull();
        status.OldestReceivedTime.Should().BeNull();
        status.LiveDelivery.Should().Be(live);
    }

    // ---- 1. no spelling of the victim's DID meets the victim's DID ----------------------------

    [Theory]
    [MemberData(nameof(SpellingNames))]
    public async Task No_spelling_of_the_victims_did_shows_mallory_its_queue_or_lets_a_sender_reach_it(string what)
    {
        // Defeated. Nothing between the wire and the two stores normalises: the parser hands the
        // string on, BareDid.IsBareDid compares the DID subject with the string itself, and both
        // stores key ordinally. So a look-alike is either not a bare DID (never looked up) or a
        // different DID (one lookup, another answer) — never the victim's.
        var spelling = Spellings[what];
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        await h.QueueAsync(VictimsDid, 40, 1);
        await h.QueueAsync(VictimsDid, 50, 2);
        (await h.Fixture.FetchAsync(victim, h.Mediator, h.Http(victim), 1)).Should().NotBeNull("one of the victim's messages is out for delivery");
        var session = h.Live(mallory);
        h.ResetTraces();

        var status = await h.SendAsync(h.StatusRequest(mallory, spelling), session);
        var fetch = await h.SendAsync(h.DeliveryRequest(mallory, 10, spelling), session);

        ShouldBeTheEmptyQueueOf(status, spelling);
        ShouldBeTheEmptyQueueOf(fetch, spelling);
        session.Attempts.Should().BeEmpty("nothing is pushed for a scope the peer does not own");
        h.StoreTrace.Should().BeEmpty("the message store is never consulted for a scope the peer does not own ({0})", what);
        h.MediationTrace.Should().OnlyContain(call => call == nameof(IMediationStore.FindGrantByRecipientDidAsync));
        h.MediationTrace.Length.Should().BeLessThanOrEqualTo(2, "at most one lookup per request");

        // The sender's side: a forward addressed to the look-alike never lands in the victim's queue…
        (await h.Fixture.Queue.DeliverAsync(spelling, PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered, what);
        (await h.StoredAsync(victim)).Should().Be(2);

        // …and when Mallory registers the look-alike (it is a different DID, so she may), she gets
        // what is sent to the look-alike and nothing that is sent to the victim.
        if (BareDid.IsBareDid(spelling))
        {
            (await h.Mediation.Inner.UpdateRecipientsAsync(mallory, Add(spelling), None)).Should().Equal(RecipientStoreOutcome.Added);
            var planted = await h.Fixture.Queue.DeliverAsync(spelling, PickupFixture.Payload(8, 3));
            planted.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            planted.GrantPeerDid.Should().Be(mallory);
            (await h.Fixture.Queue.DeliverAsync(VictimsDid, PickupFixture.Payload(8, 4))).GrantPeerDid.Should().Be(victim);
            (await h.StoredAsync(victim)).Should().Be(3);
            (await h.StoredAsync(mallory)).Should().Be(1);
            var hers = await h.Fixture.FetchAsync(mallory, h.Mediator, h.Http(mallory), 10);
            hers!.Value.Messages.Select(m => m.Id).Should().Equal(planted.MessageId);
        }
    }

    [Fact]
    public async Task A_did_of_2048_characters_and_its_nearest_twins_are_different_dids_to_every_layer()
    {
        // Defeated. Length is counted in UTF-16 code units and every later comparison is ordinal over
        // the same units: a 2047-character prefix, a twin that differs in its last character and the
        // victim's DID are three keys; one character more is malformed before anything looks it up.
        var victimsDid = "did:example:" + new string('b', 2048 - "did:example:".Length);
        var twin = victimsDid[..^1] + "c";
        var prefix = victimsDid[..^1];
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, victimsDid);
        await h.EnrolAsync(mallory, mallory);
        await h.QueueAsync(victimsDid, 40);
        var session = h.Http(mallory);

        foreach (var value in new[] { victimsDid, twin, prefix })
        {
            h.ResetTraces();
            ShouldBeTheEmptyQueueOf(await h.SendAsync(h.StatusRequest(mallory, value), session), value);
            ShouldBeTheEmptyQueueOf(await h.SendAsync(h.DeliveryRequest(mallory, 10, value), session), value);
            h.StoreTrace.Should().BeEmpty();
            h.MediationTrace.Should().Equal(nameof(IMediationStore.FindGrantByRecipientDidAsync), nameof(IMediationStore.FindGrantByRecipientDidAsync));
        }
        (await h.Fixture.Queue.DeliverAsync(twin, PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await h.Fixture.Queue.DeliverAsync(prefix, PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await h.Fixture.Queue.DeliverAsync(victimsDid + "b", PickupFixture.Payload(8))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered, "an over-long value is never looked up");

        h.ResetTraces();
        var tooLong = await Record.ExceptionAsync(() => h.SendAsync(
            h.Raw("status-request", mallory, new JsonObject { ["recipient_did"] = victimsDid + "b" }), session));
        tooLong.Should().BeOfType<MalformedMessageException>().Which.Message.Should().NotContain("bbbb");
        h.MediationTrace.Should().BeEmpty("2049 characters are refused by the length check alone");
        h.StoreTrace.Should().BeEmpty();
        (await h.StoredAsync(victim)).Should().Be(1);
    }

    // ---- 2. every store call Mallory can cause is keyed by Mallory ----------------------------

    [Fact]
    public async Task Nothing_mallory_sends_reaches_a_store_call_under_the_victims_key_or_ends_one_of_its_leases()
    {
        // Defeated. The victim holds three messages: one out for delivery over HTTP (a lease
        // without an owner), one pushed live (a lease owned by the victim's session) and one
        // waiting. Mallory names the victim's DID and ids in every request type, on an HTTP and on
        // a live session, and then drops her connection. Every call the message store sees carries
        // her key (or her own session's owner token), and the victim's three records are exactly
        // as they were — the two leases included.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, VictimsDid);
        await h.EnrolAsync(mallory, mallory);
        var overHttp = await h.QueueAsync(VictimsDid, 40, 1);
        (await h.Fixture.FetchAsync(victim, h.Mediator, h.Http(victim), 1))!.Value.Messages.Select(m => m.Id).Should().Equal(overHttp);
        var victimsSocket = h.Fixture.Session(victim, h.Mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(h.Fixture, victim, h.Mediator, victimsSocket)).LiveDelivery.Should().BeTrue();
        var pushed = await h.Fixture.Queue.DeliverAsync(VictimsDid, PickupFixture.Payload(50, 2));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        var waiting = await h.Fixture.Queue.DeliverAsync(VictimsDid, PickupFixture.Payload(60, 3), new PickupDeliveryOptions(AllowLive: false));
        waiting.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        var victimsIds = new[] { overHttp, pushed.MessageId!, waiting.MessageId! };
        (await h.DeliverableAsync(victim)).Should().Equal(waiting.MessageId);
        h.ResetTraces();

        var malloryHttp = h.Http(mallory);
        var mallorySocket = h.Fixture.Session(mallory, h.Mediator, live: true);
        foreach (var session in new PickupFakeSession[] { malloryHttp, mallorySocket })
        {
            (await h.SendAsync(h.StatusRequest(mallory), session)).Status().MessageCount.Should().Be(0);
            ShouldBeTheEmptyQueueOf(await h.SendAsync(h.StatusRequest(mallory, VictimsDid), session), VictimsDid, live: false);
            (await h.SendAsync(h.DeliveryRequest(mallory, 10), session)).Status().MessageCount.Should().Be(0);
            ShouldBeTheEmptyQueueOf(await h.SendAsync(h.DeliveryRequest(mallory, 10, VictimsDid), session), VictimsDid, live: false);
            (await h.SendAsync(h.MessagesReceived(mallory, victimsIds), session)).Status().MessageCount.Should().Be(0);
            await h.SendAsync(h.LiveChange(mallory, true), session);
            await h.SendAsync(h.LiveChange(mallory, false), session);
        }
        // An arrival for the victim while Mallory's socket is in live mode, then her socket closes.
        await h.SendAsync(h.LiveChange(mallory, true), mallorySocket);
        var late = await h.Fixture.Queue.DeliverAsync(VictimsDid, PickupFixture.Payload(70, 4));
        late.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        victimsSocket.Sent.Should().HaveCount(2, "both live arrivals went to the victim's own socket");
        mallorySocket.Close();
        await MessagePickupLive.UntilAsync(() => !h.Store.ReleasedOwners.IsEmpty, "the close of Mallory's socket releases the leases of her own session");

        malloryHttp.Sent.Should().BeEmpty("no delivery was ever pushed to Mallory");
        mallorySocket.Sent.Should().BeEmpty();
        h.Store.LeaseRequests.Should().OnlyContain(r => r.GrantPeerDid == mallory);
        h.Store.Acknowledged.Should().OnlyContain(a => a.GrantPeerDid == mallory);
        h.Store.Released.Should().OnlyContain(r => r.GrantPeerDid == mallory);
        h.Store.StatusRequests.Should().OnlyContain(s => s.GrantPeerDid == mallory);
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.Store.Enqueued.Should().OnlyContain(e => e.Message.GrantPeerDid == victim);
        var victimsOwnerToken = h.Store.Enqueued.Single().Lease!.Owner;
        h.Store.ReleasedOwners.Should().NotContain(victimsOwnerToken, "only the token of the session that closed is released");
        (await h.StoredAsync(victim)).Should().Be(4);
        (await h.DeliverableAsync(victim)).Should().Equal(new[] { waiting.MessageId }, "the HTTP lease and both live leases of the victim still hold");
    }

    // ---- 3. the queue follows the registration, not the name of the peer ------------------------

    [Fact]
    public async Task A_peers_own_did_registered_under_another_grant_is_that_grants_queue_and_an_empty_one_to_the_peer()
    {
        // Defeated, in the sense that pickup adds nothing to what Coordinate Mediation already
        // decided (#93: "the first peer to register a DID gets it"). The peer whose DID was taken
        // sees an empty queue under every scope it can name — it cannot read, count or acknowledge
        // what is held for "its" DID under the other grant, and its live session gets no push.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var named = h.Alice;
        var holder = h.Bob;
        await h.Fixture.EnrolAsync(named, h.Mediator);
        await h.EnrolAsync(holder, named);
        var namedSocket = h.Fixture.Session(named, h.Mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(h.Fixture, named, h.Mediator, namedSocket)).LiveDelivery.Should().BeTrue();

        var arrival = await h.Fixture.Queue.DeliverAsync(named, PickupFixture.Payload(33));

        arrival.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the grant that holds the DID has no live session");
        arrival.GrantPeerDid.Should().Be(holder);
        namedSocket.Sent.Should().BeEmpty("a push follows the grant, not the DID the message names");
        h.ResetTraces();
        (await h.SendAsync(h.StatusRequest(named), namedSocket)).Status().MessageCount.Should().Be(0);
        ShouldBeTheEmptyQueueOf(await h.SendAsync(h.StatusRequest(named, named), namedSocket), named, live: true);
        ShouldBeTheEmptyQueueOf(await h.SendAsync(h.DeliveryRequest(named, 10, named), namedSocket), named, live: true);
        (await h.SendAsync(h.MessagesReceived(named, new[] { arrival.MessageId! }), namedSocket)).Status().MessageCount.Should().Be(0);
        h.Store.LeaseRequests.Should().BeEmpty();
        h.Store.Acknowledged.Should().OnlyContain(a => a.GrantPeerDid == named);
        h.Store.StatusRequests.Should().OnlyContain(s => s.GrantPeerDid == named);
        (await h.Fixture.FetchAsync(holder, h.Mediator, h.Http(holder), 10))!.Value.Messages.Select(m => m.Id).Should().Equal(arrival.MessageId);
    }

    [Fact]
    public async Task A_did_handed_from_one_grant_to_another_and_back_takes_no_message_and_no_purge_across_with_it()
    {
        // Defeated. X is the victim's, then Mallory's, then the victim's again; both keep a live
        // socket in live mode throughout. At every step an arrival goes to the grant that holds X
        // at that moment, and the delivery-time purge of one side only ever removes that side's own
        // leftovers: Mallory can neither receive what was queued for X before she had it, nor make
        // the mediator purge what the victim holds.
        const string x = "did:example:adv1-handed-over";
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = h.Alice;
        var mallory = h.Bob;
        await h.EnrolAsync(victim, x, victim);
        await h.EnrolAsync(mallory, mallory);
        var victimsSocket = h.Fixture.Session(victim, h.Mediator, live: true);
        var mallorysSocket = h.Fixture.Session(mallory, h.Mediator, live: true);
        await MessagePickupLive.SetLiveAsync(h.Fixture, victim, h.Mediator, victimsSocket);
        await MessagePickupLive.SetLiveAsync(h.Fixture, mallory, h.Mediator, mallorysSocket);

        // Step 1: X is the victim's.
        var first = await h.Fixture.Queue.DeliverAsync(x, PickupFixture.Payload(10, 1));
        first.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        first.GrantPeerDid.Should().Be(victim);
        var own = await h.Fixture.Queue.DeliverAsync(victim, PickupFixture.Payload(11, 2), new PickupDeliveryOptions(AllowLive: false));
        victimsSocket.Sent.Should().HaveCount(1);
        mallorysSocket.Sent.Should().BeEmpty();

        // While the victim holds X, Mallory cannot take it: the store's answer is the #93 one, and nothing of the victim's moves.
        (await h.Mediation.Inner.UpdateRecipientsAsync(mallory, Add(x), None)).Should().Equal(RecipientStoreOutcome.OwnedByAnotherGrant);
        (await h.Fixture.FetchAsync(mallory, h.Mediator, h.Http(mallory), 10)).Should().BeNull();
        (await h.StoredAsync(victim)).Should().Be(2);

        // Step 2: the victim removes X and Mallory registers it.
        (await h.Mediation.Inner.UpdateRecipientsAsync(victim, Remove(x), None)).Should().Equal(RecipientStoreOutcome.Removed);
        (await h.Mediation.Inner.UpdateRecipientsAsync(mallory, Add(x), None)).Should().Equal(RecipientStoreOutcome.Added);
        var second = await h.Fixture.Queue.DeliverAsync(x, PickupFixture.Payload(12, 3));
        second.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        second.GrantPeerDid.Should().Be(mallory);
        victimsSocket.Sent.Should().HaveCount(1, "the victim's socket is still live, but X is no longer its DID");
        mallorysSocket.Sent.Should().HaveCount(1);
        MessagePickup.TryParseDelivery(mallorysSocket.Sent.Single(), out var mallorys, out _).Should().BeTrue();
        mallorys!.Select(m => m.Id).Should().Equal(second.MessageId);

        // Mallory asks for X in every way: she gets her own message at most, never the victim's first one.
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var filter in new[] { null, x })
        {
            var fetched = await h.Fixture.FetchAsync(mallory, h.Mediator, h.Http(mallory), 10, filter);
            fetched!.Value.Messages.Select(m => m.Id).Should().Equal(second.MessageId);
            h.Clock.Advance(TimeSpan.FromSeconds(61));
        }
        (await h.SendAsync(h.MessagesReceived(mallory, new[] { first.MessageId!, own.MessageId! }), h.Http(mallory))).Status().MessageCount.Should().Be(1);
        (await h.StoredAsync(victim)).Should().Be(2, "Mallory's acknowledgement of the victim's ids removed nothing");

        // The victim's next unfiltered delivery drops its own leftover for X, delivers the rest, and leaves Mallory's queue alone.
        h.ResetTraces();
        var victims = await h.Fixture.FetchAsync(victim, h.Mediator, h.Http(victim), 10);
        victims!.Value.Messages.Select(m => m.Id).Should().Equal(own.MessageId);
        // The leftover is removed by id, under the victim's own key: exactly the record the check looked at.
        h.Store.Acknowledged.Should().ContainSingle().Which.Should().BeEquivalentTo((GrantPeerDid: victim, Ids: new[] { first.MessageId! }));
        h.Store.PurgedRecipients.Should().BeEmpty();
        (await h.StoredAsync(victim)).Should().Be(1);
        (await h.StoredAsync(mallory)).Should().Be(1);

        // Step 3: Mallory removes X and the victim registers it again.
        (await h.Mediation.Inner.UpdateRecipientsAsync(mallory, Remove(x), None)).Should().Equal(RecipientStoreOutcome.Removed);
        (await h.Mediation.Inner.UpdateRecipientsAsync(victim, Add(x), None)).Should().Equal(RecipientStoreOutcome.Added);
        var third = await h.Fixture.Queue.DeliverAsync(x, PickupFixture.Payload(13, 4));
        third.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        third.GrantPeerDid.Should().Be(victim);
        mallorysSocket.Sent.Should().HaveCount(1);
        victimsSocket.Sent.Should().HaveCount(2);

        // Mallory's leftover for X is now undeliverable to her, and her purge touches her key only.
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        h.ResetTraces();
        (await h.Fixture.FetchAsync(mallory, h.Mediator, h.Http(mallory), 10)).Should().BeNull();
        ShouldBeTheEmptyQueueOf(await h.SendAsync(h.StatusRequest(mallory, x), h.Http(mallory)), x);
        h.Store.Acknowledged.Should().ContainSingle().Which.GrantPeerDid.Should().Be(mallory, "her leftover is removed under her own key and nobody else's");
        h.Store.PurgedRecipients.Should().BeEmpty();
        (await h.StoredAsync(mallory)).Should().Be(0);
        (await h.StoredAsync(victim)).Should().Be(2, "the victim's acknowledged-by-nobody messages are untouched by Mallory's purge");
    }

    // ---- 4. the live framing belongs to the session that chose it -----------------------------

    [Fact]
    public async Task Each_live_session_is_pushed_in_the_framing_it_turned_live_delivery_on_with()
    {
        // Defeated. The framing delegate (what a 3.0 or a 4.0 handler hands over when it turns live
        // mode on) is stored on the session's own state: one peer's choice never frames another
        // peer's push, and with two sessions of one peer the push carries the framing of the
        // session it goes to.
        await using var fx = PickupFixture.Create();
        var mediator = (await fx.NewPeerAsync()).Did;
        var bob = (await fx.NewPeerAsync()).Did;
        var mallory = (await fx.NewPeerAsync()).Did;
        await fx.EnrolAsync(bob, mediator, bob);
        await fx.EnrolAsync(mallory, mediator, mallory);
        var coordinator = fx.Queue.Coordinator;

        static Func<string, string, DeliveredMessage, Message> Framing(string mark) => (from, to, delivered) =>
        {
            var message = MessagePickup.CreateDelivery(from, to, new[] { delivered });
            message.Body = new JsonObject { ["framing"] = mark };
            return message;
        };

        static string? FramingOf(Message message) => message.Body?["framing"]?.GetValue<string>();

        var bobsOlder = fx.Session(bob, mediator, live: true);
        var bobsNewer = fx.Session(bob, mediator, live: true);
        var mallorys = fx.Session(mallory, mediator, live: true);
        (await coordinator.SetLiveDeliveryAsync(bob, true, bobsOlder, Framing("bob-older"), "m-1", None)).Reply.Should().Be(PickupReply.Status);
        (await coordinator.SetLiveDeliveryAsync(mallory, true, mallorys, Framing("mallory"), "m-2", None)).Reply.Should().Be(PickupReply.Status);

        (await fx.Queue.DeliverAsync(bob, PickupFixture.Payload(8, 1))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        (await fx.Queue.DeliverAsync(mallory, PickupFixture.Payload(8, 2))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        bobsOlder.Sent.Select(FramingOf).Should().Equal("bob-older");
        mallorys.Sent.Select(FramingOf).Should().Equal("mallory");

        (await coordinator.SetLiveDeliveryAsync(bob, true, bobsNewer, Framing("bob-newer"), "m-3", None)).Reply.Should().Be(PickupReply.Status);
        (await fx.Queue.DeliverAsync(bob, PickupFixture.Payload(8, 3))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        bobsNewer.Sent.Select(FramingOf).Should().Equal("bob-newer");

        (await coordinator.SetLiveDeliveryAsync(bob, false, bobsNewer, Framing("unused"), "m-4", None)).Reply.Should().Be(PickupReply.Status);
        (await fx.Queue.DeliverAsync(bob, PickupFixture.Payload(8, 4))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        bobsOlder.Sent.Select(FramingOf).Should().Equal("bob-older", "bob-older");
        bobsNewer.Sent.Should().HaveCount(1);
        mallorys.Sent.Should().HaveCount(1, "none of Bob's four arrivals reached Mallory's socket");
        mallorys.Sent.Single().To.Should().Equal(mallory);
        bobsOlder.Sent.Concat(bobsNewer.Sent).Should().OnlyContain(m => m.To!.Single() == bob);
    }
}
