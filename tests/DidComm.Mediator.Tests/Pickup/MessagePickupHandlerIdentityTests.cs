using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Whose queue a request reaches (FR-PICK-01..04; JC-4, -16; the owner decision on removed DIDs
/// and revoked grants). Every case here is self-consistent: the envelopes are valid, the ids and
/// DIDs exist — only the identity fields disagree with each other or with the registry. A peer
/// sees and changes what is held under its own authenticated DID and nothing else, and what it is
/// told about anything else is the same whatever the truth is.
/// </summary>
public sealed class MessagePickupHandlerIdentityTests
{
    // ---- (a) the plaintext `from` is not the peer ------------------------------------------

    [Fact]
    public async Task The_peer_is_the_authcrypt_sender_key_not_the_plaintext_from()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var bobId = await h.QueueAsync(h.Bob, 5, 3);
        var t0 = h.Start.ToUnixTimeSeconds();
        var bobTimes = new MessagePickupHandlerTimes(0, t0, t0);
        var aliceSocket = h.Live(h.Alice);
        var bobSession = h.Http(h.Bob);

        // A message that names no `from` at all is attributed to the key that authcrypted it.
        var anonymous = new MessageBuilder().WithType(MessagePickup.StatusRequestType).WithTo(h.Mediator).WithBody(new JsonObject()).Build();
        (await h.SendAsync(anonymous, sender: h.Bob)).ShouldBeStatus(anonymous, h.Mediator, h.Bob, 1, 5, live: false, times: bobTimes);

        // Unpack results whose plaintext says "Alice" while the sender key is Bob's. The real
        // unpack refuses that pairing; these are handed to the dispatcher directly, so the rule
        // under test is the handler's own.
        async Task<DispatchOutcome> ForgedAsync(Message bobsMessage, DidComm.Transports.ReturnRoute.IReturnRouteSession? session)
        {
            var received = await h.Fixture.ReceiveAsync(bobsMessage, h.Mediator);
            received.Message.From = h.Alice;
            return await h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, session, CancellationToken.None);
        }

        // status-request: Bob's queue, addressed to Bob.
        var status = h.StatusRequest(h.Bob);
        (await ForgedAsync(status, bobSession)).ShouldBeStatus(status, h.Mediator, h.Bob, 1, 5, live: false, times: bobTimes);

        // messages-received naming Alice's ids: applied under Bob's key, where they match nothing.
        h.ResetTraces();
        var ack = h.MessagesReceived(h.Bob, aliceIds);
        (await ForgedAsync(ack, bobSession)).ShouldBeStatus(ack, h.Mediator, h.Bob, 1, 5, live: false, times: bobTimes);
        h.Store.Acknowledged.Should().ContainSingle().Which.GrantPeerDid.Should().Be(h.Bob);
        (await h.StoredAsync(h.Alice)).Should().Be(2);

        // live-delivery-change carried with ALICE's live session: not Bob's connection, so the
        // problem report goes to Bob and Alice's session keeps its flag.
        var live = h.LiveChange(h.Bob, true);
        (await ForgedAsync(live, aliceSocket)).ShouldBeLiveModeNotSupported(live, h.Mediator, h.Bob);
        (await h.LiveFlagAsync(aliceSocket)).Should().BeFalse();

        // delivery-request carried with Alice's session: nothing is pushed to her, nothing of hers is leased.
        var viaAlice = h.DeliveryRequest(h.Bob, 10);
        (await ForgedAsync(viaAlice, aliceSocket)).ShouldBeStatus(viaAlice, h.Mediator, h.Bob, 1, 5, live: false, times: bobTimes);
        aliceSocket.Attempts.Should().BeEmpty();

        // delivery-request on Bob's own session: Bob's message, to Bob.
        var fetch = h.DeliveryRequest(h.Bob, 10);
        (await ForgedAsync(fetch, bobSession)).ShouldBeNoReply();
        bobSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, h.Mediator, h.Bob, null, new[] { bobId });

        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds, "Alice's queue was neither read, leased nor changed");
    }

    [Fact]
    public async Task A_real_envelope_whose_from_disagrees_with_its_sender_key_never_touches_the_named_peers_queue()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var aliceSocket = h.Live(h.Alice);

        // Every byte validly authcrypted by Bob; the plaintext claims to be from Alice.
        var forged = new[]
        {
            h.MessagesReceived(h.Alice, aliceIds),
            h.DeliveryRequest(h.Alice, 10),
            h.StatusRequest(h.Alice),
            h.LiveChange(h.Alice, true),
        };
        foreach (var message in forged)
        {
            var thrown = await Record.ExceptionAsync(async () =>
            {
                var outcome = await h.SendAsync(message, aliceSocket, sender: h.Bob);
                // Not refused at unpack: then the peer is Bob, and only Bob is answered.
                if (outcome.Reply is not null)
                    outcome.Reply.To.Should().Equal(h.Bob);
            });
            (thrown is null or DidCommException).Should().BeTrue(message.Type);
        }

        aliceSocket.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds);
        (await h.LiveFlagAsync(aliceSocket)).Should().BeFalse();
        h.Store.Acknowledged.Should().NotContain(call => call.GrantPeerDid == h.Alice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_authcrypt_by_one_peer_around_a_signature_by_another_is_nobody(bool plaintextNamesTheSigner)
    {
        // authcrypt(sign): the signature is genuinely Alice's (a JWS she made for this mediator and
        // Bob captured); the sender key is Bob's. Neither of them is the peer.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1) };
        var bobIds = new[] { await h.QueueAsync(h.Bob, 5, 2) };
        h.ResetTraces();

        var builder = new MessageBuilder().WithType(MessagePickup.MessagesReceivedType).WithTo(h.Mediator)
            .WithBody(new JsonObject { ["message_id_list"] = new JsonArray(aliceIds[0], bobIds[0]) });
        if (plaintextNamesTheSigner)
            builder.WithFrom(h.Alice);
        var message = builder.Build();

        var thrown = await Record.ExceptionAsync(async () =>
        {
            var client = h.Fixture.Host.Client;
            var packed = (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { h.Mediator }, From: h.Bob, SignFrom: h.Alice))).Message;
            var received = await client.UnpackAsync(packed);
            var outcome = await h.Fixture.Host.Dispatcher.DispatchAsync(received, client, h.Fixture.Host.Options, CancellationToken.None);
            outcome.Reply.Should().BeNull("a sender key and a signer that disagree are nobody");
        });

        (thrown is null or DidCommException or ArgumentException).Should().BeTrue();
        h.StoreTrace.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds);
        (await h.DeliverableAsync(h.Bob)).Should().Equal(bobIds);
    }

    [Fact]
    public async Task Rewriting_the_sender_key_of_a_genuine_envelope_after_packing_never_reaches_the_pickup_role()
    {
        // The other half of the identity check: not a self-consistent forgery but tampering after
        // the fact. Bob packs real requests, then the envelope's skid (and apu, derived from it) is
        // pointed at the victim's key.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var victim = await h.Fixture.NewPeerAsync();
        await h.EnrolAsync(victim.Did, victim.Did);
        await h.EnrolAsync(h.Bob, h.Bob);
        var victimIds = new[] { await h.QueueAsync(victim.Did, 10, 1), await h.QueueAsync(victim.Did, 20, 2) };
        var victimSocket = h.Live(victim.Did);
        var victimKid = victim.Privates.First(j => j.Crv == "X25519").Kid!;
        var client = h.Fixture.Host.Client;
        h.ResetTraces();

        static string Encode(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static string Decode(string text)
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')));
        }

        foreach (var message in new[] { h.MessagesReceived(h.Bob, victimIds), h.DeliveryRequest(h.Bob, 10), h.StatusRequest(h.Bob), h.LiveChange(h.Bob, true) })
        {
            var genuine = (await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { h.Mediator }, From: h.Bob))).Message;
            var jwe = JsonNode.Parse(genuine)!.AsObject();
            var header = JsonNode.Parse(Decode(jwe["protected"]!.GetValue<string>()))!.AsObject();
            header["skid"] = victimKid;
            header["apu"] = Encode(victimKid);
            jwe["protected"] = Encode(header.ToJsonString());

            var thrown = await Record.ExceptionAsync(async () =>
            {
                var received = await client.UnpackAsync(jwe.ToJsonString());
                await h.Fixture.Host.Dispatcher.DispatchAsync(received, client, h.Fixture.Host.Options, Task.CompletedTask, victimSocket, CancellationToken.None);
            });

            thrown.Should().BeAssignableTo<DidCommException>(message.Type);
        }

        h.StoreTrace.Should().BeEmpty();
        h.MediationTrace.Should().BeEmpty();
        victimSocket.Attempts.Should().BeEmpty();
        (await h.DeliverableAsync(victim.Did)).Should().Equal(victimIds);
        (await h.LiveFlagAsync(victimSocket)).Should().BeFalse();
    }

    // ---- (b) recipient_did is no registration oracle ---------------------------------------

    public static TheoryData<string, bool> ForeignRecipientDids => new()
    {
        // what the peer names → whether the value is a bare DID (and so is looked up at all)
        { "a DID registered under another grant", true },
        { "another DID registered under another grant", true },
        { "a DID nobody registered", true },
        { "the mediator's own DID", true },
        { "another grant's DID with a fragment", false },
        { "another grant's DID with a query", false },
        { "another grant's DID with a path", false },
        { "the peer's own registered DID with a fragment", false },
        { "the peer's own registered DID with a query", false },
        { "the peer's own registered DID with a path", false },
        { "the peer's own registered DID with a trailing space", false },
        { "the peer's own registered DID in upper case", false },
        { "not a DID at all", false },
        { "a value with quotes, a line break and a character outside the BMP", false },
    };

    [Theory]
    [MemberData(nameof(ForeignRecipientDids))]
    public async Task A_recipient_did_the_peer_does_not_hold_is_answered_like_an_empty_queue_and_the_message_store_is_not_touched(string what, bool lookedUp)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var aliceSecond = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, aliceSecond);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(aliceSecond, 20, 2) };
        var bobId = await h.QueueAsync(h.Bob, 5, 3);
        var session = h.Http(h.Bob);
        var value = what switch
        {
            "a DID registered under another grant" => h.Alice,
            "another DID registered under another grant" => aliceSecond,
            "a DID nobody registered" => "did:example:nobody",
            "the mediator's own DID" => h.Mediator,
            "another grant's DID with a fragment" => h.Alice + "#key-1",
            "another grant's DID with a query" => h.Alice + "?x=1",
            "another grant's DID with a path" => h.Alice + "/path",
            "the peer's own registered DID with a fragment" => h.Bob + "#key-1",
            "the peer's own registered DID with a query" => h.Bob + "?x=1",
            "the peer's own registered DID with a path" => h.Bob + "/path",
            "the peer's own registered DID with a trailing space" => h.Bob + " ",
            "the peer's own registered DID in upper case" => h.Bob.ToUpperInvariant(),
            "not a DID at all" => "bob",
            "a value with quotes, a line break and a character outside the BMP" => h.Bob + "\"'\r\n<&>\\ \U0001F512 é",
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        var expectedReads = lookedUp ? new[] { ("FindGrantByRecipientDidAsync", value) } : Array.Empty<(string, string)>();

        // status-request: status echoing the value, with message_count 0 and no time members.
        h.ResetTraces();
        var status = h.StatusRequest(h.Bob, value);
        (await h.SendAsync(status, session)).ShouldBeStatus(status, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: value);
        h.StoreTrace.Should().BeEmpty("the message store is read only for a scope the peer owns");
        h.Mediation.Reads.Should().Equal(expectedReads);

        // delivery-request: the same status; nothing leased, nothing pushed.
        h.ResetTraces();
        var fetch = h.DeliveryRequest(h.Bob, 10, value);
        (await h.SendAsync(fetch, session)).ShouldBeStatus(fetch, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: value);
        h.StoreTrace.Should().BeEmpty();
        h.Mediation.Reads.Should().Equal(expectedReads);
        session.Attempts.Should().BeEmpty();

        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds);
        (await h.DeliverableAsync(h.Bob)).Should().Equal(bobId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registered_to_another_grant_and_registered_to_nobody_give_the_same_answer_and_the_same_store_calls(bool askerHasGrant)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        if (askerHasGrant)
            await h.EnrolAsync(h.Bob, h.Bob);
        await h.QueueAsync(h.Alice, 10);
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        var session = h.Http(h.Bob);

        // Everything observable for one probe value: both replies, both stores' calls, what was pushed.
        async Task<string> ProbeAsync(string value)
        {
            h.ResetTraces();
            var status = await h.SendAsync(h.StatusRequest(h.Bob, value), session);
            var fetch = await h.SendAsync(h.DeliveryRequest(h.Bob, 10, value), session);
            return string.Join(" | ",
                status.Reply!.Type, MessagePickupHandlerAssert.Members(status.Reply).Replace(value, "<value>", StringComparison.Ordinal),
                fetch.Reply!.Type, MessagePickupHandlerAssert.Members(fetch.Reply).Replace(value, "<value>", StringComparison.Ordinal),
                "store: " + string.Join(",", h.StoreTrace),
                "mediation: " + string.Join(",", h.MediationTrace),
                "pushed: " + session.Attempts.Count);
        }

        var registeredToAlice = await ProbeAsync(h.Alice);
        var registeredToNobody = await ProbeAsync("did:example:nobody");

        registeredToAlice.Should().Be(registeredToNobody);
        registeredToAlice.Should().Contain("message_count=0").And.Contain("recipient_did=\"<value>\"")
            .And.Contain("store:  |").And.Contain("mediation: FindGrantByRecipientDidAsync,FindGrantByRecipientDidAsync").And.Contain("pushed: 0");
    }

    // ---- (c) ids mean something only under the caller's own key ----------------------------

    [Fact]
    public async Task Acknowledging_another_grants_ids_removes_nothing_and_echoes_nothing()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var bobId = await h.QueueAsync(h.Bob, 5, 4);
        // One of Alice's is out for delivery (leased): that state must not make it reachable either.
        var aliceSession = h.Http(h.Alice);
        (await h.Fixture.FetchAsync(h.Alice, h.Mediator, aliceSession, 1))!.Value.Messages.Should().ContainSingle(m => m.Id == aliceIds[0]);
        var bobSession = h.Http(h.Bob);
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();

        var request = h.MessagesReceived(h.Bob, aliceIds);
        var outcome = await h.SendAsync(request, bobSession);

        // Bob's status is the ordinary one: his own message, nothing about the ids he named.
        outcome.ShouldBeStatus(request, h.Mediator, h.Bob, 1, 5, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        var call = h.Store.Acknowledged.Should().ContainSingle().Subject;
        call.GrantPeerDid.Should().Be(h.Bob, "the acknowledgement is confined to the caller's own key");
        call.Ids.Should().Equal(aliceIds);
        (await h.StoredAsync(h.Alice)).Should().Be(3);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds[1], aliceIds[2]);
        (await h.DeliverableAsync(h.Bob)).Should().Equal(bobId);

        var wire = JsonSerializer.Serialize(outcome.Reply, DidCommJson.Default);
        foreach (var id in aliceIds)
        {
            wire.Should().NotContain(id);
            h.Fixture.PickupLogs.Should().OnlyContain(e => !e.Everything.Contains(id));
        }

        // Mixed with one of his own: only his own goes.
        var mixed = h.MessagesReceived(h.Bob, new[] { aliceIds[1], bobId, aliceIds[2] });
        (await h.SendAsync(mixed, bobSession)).ShouldBeStatus(mixed, h.Mediator, h.Bob, 0, 0, live: false);
        (await h.StoredAsync(h.Alice)).Should().Be(3);
        (await h.StoredAsync(h.Bob)).Should().Be(0);
    }

    // ---- (d) the same recipient DID string, even the same message id, under two grants ------

    [Fact]
    public async Task Two_grants_holding_the_same_recipient_did_and_the_same_message_id_never_see_each_others_records()
    {
        // #93 lets one grant hold a recipient DID. A host store may not be as strict, so the
        // queue's own key — (grant, id) — has to keep the two apart on its own.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        const string shared = "did:example:shared";
        var now = h.Clock.Now;
        var alicePayload = PickupFixture.Payload(11, 1);
        var bobPayload = PickupFixture.Payload(22, 2);
        var aliceOnly = PickupFixture.Payload(33, 3);
        var store = h.Store.Inner;
        (await store.EnqueueAsync(new QueuedMessage("same-id", h.Alice, shared, alicePayload, now), null, now, default)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(new QueuedMessage("same-id", h.Bob, shared, bobPayload, now), null, now, default)).Should().Be(EnqueueOutcome.Enqueued);
        (await store.EnqueueAsync(new QueuedMessage("alice-only", h.Alice, shared, aliceOnly, now), null, now, default)).Should().Be(EnqueueOutcome.Enqueued);

        // The registry claims the DID for whoever asks (what "both registered it" would look like).
        string asking = h.Alice;
        h.Mediation.OnFindGrantByRecipientDid = async (did, ct) =>
            did == shared ? await h.Mediation.Inner.GetGrantAsync(asking, ct) : await h.Mediation.Inner.FindGrantByRecipientDidAsync(did, ct);
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);

        // Alice, filtered by the shared DID: her two records.
        var aliceSession = h.Http(h.Alice);
        var aliceStatus = h.StatusRequest(h.Alice, shared);
        (await h.SendAsync(aliceStatus, aliceSession)).ShouldBeStatus(aliceStatus, h.Mediator, h.Alice, 2, 44, live: false, recipientDid: shared, times: times);
        var aliceFetch = h.DeliveryRequest(h.Alice, 10, shared);
        (await h.SendAsync(aliceFetch, aliceSession)).ShouldBeNoReply();
        aliceSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(aliceFetch, h.Mediator, h.Alice, shared, new[] { "same-id", "alice-only" }, new[] { alicePayload, aliceOnly });

        // Bob, filtered by the same string: his one record, with his payload under the same id.
        asking = h.Bob;
        var bobSession = h.Http(h.Bob);
        var bobStatus = h.StatusRequest(h.Bob, shared);
        (await h.SendAsync(bobStatus, bobSession)).ShouldBeStatus(bobStatus, h.Mediator, h.Bob, 1, 22, live: false, recipientDid: shared, times: times);
        var bobFetch = h.DeliveryRequest(h.Bob, 10);
        (await h.SendAsync(bobFetch, bobSession)).ShouldBeNoReply();
        bobSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(bobFetch, h.Mediator, h.Bob, null, new[] { "same-id" }, new[] { bobPayload });

        // Bob acknowledges both ids he has ever seen or guessed: only his own record goes.
        var bobAck = h.MessagesReceived(h.Bob, new[] { "same-id", "alice-only" });
        (await h.SendAsync(bobAck, bobSession)).ShouldBeStatus(bobAck, h.Mediator, h.Bob, 0, 0, live: false);
        (await h.StoredAsync(h.Alice)).Should().Be(2);

        // Alice acknowledges hers: gone, one by one.
        asking = h.Alice;
        var aliceAck = h.MessagesReceived(h.Alice, new[] { "same-id" });
        (await h.SendAsync(aliceAck, aliceSession)).ShouldBeStatus(aliceAck, h.Mediator, h.Alice, 1, 33, live: false, times: times);
        store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_record_filed_under_a_grant_for_a_did_registered_to_another_grant_is_dropped_not_delivered_and_the_owners_copy_stays()
    {
        // The registry as #93 builds it: the DID belongs to Alice. Bob's grant key nevertheless
        // holds a record for it (a host store filled another way, or a registration that moved).
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        const string shared = "did:example:shared";
        await h.EnrolAsync(h.Alice, h.Alice, shared);
        await h.EnrolAsync(h.Bob, h.Bob);
        var now = h.Clock.Now;
        var store = h.Store.Inner;
        await store.EnqueueAsync(new QueuedMessage("alice-1", h.Alice, shared, PickupFixture.Payload(11, 1), now), null, now, default);
        await store.EnqueueAsync(new QueuedMessage("bob-stray", h.Bob, shared, PickupFixture.Payload(22, 2), now), null, now, default);
        var bobOwn = await h.QueueAsync(h.Bob, 5, 3);
        var bobSession = h.Http(h.Bob);

        // Filtered: Bob does not hold the DID, so the message store is not asked.
        h.ResetTraces();
        var filtered = h.DeliveryRequest(h.Bob, 10, shared);
        (await h.SendAsync(filtered, bobSession)).ShouldBeStatus(filtered, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: shared);
        h.StoreTrace.Should().BeEmpty();

        // Unfiltered: registration is checked at delivery time. The stray record is not delivered
        // and is removed — that record, by id, under Bob's key; Bob's own message is delivered.
        h.ResetTraces();
        var unfiltered = h.DeliveryRequest(h.Bob, 10);
        (await h.SendAsync(unfiltered, bobSession)).ShouldBeNoReply();
        bobSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(unfiltered, h.Mediator, h.Bob, null, new[] { bobOwn });
        var removal = h.Store.Acknowledged.Should().ContainSingle().Subject;
        removal.GrantPeerDid.Should().Be(h.Bob);
        removal.Ids.Should().Equal("bob-stray");
        h.Store.PurgedRecipients.Should().BeEmpty("the role removes the records it leased and never purges a DID");
        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync");
        (await h.DeliverableAsync(h.Alice)).Should().Equal(new[] { "alice-1" }, "the removal is confined to the grant it names");
    }

    // ---- (e) the peer's own DID is not a recipient DID unless it registered it --------------

    [Fact]
    public async Task The_peers_own_did_is_an_empty_queue_as_a_recipient_did_until_it_is_registered()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var registered = await h.NewDidAsync();
        await h.EnrolAsync(h.Bob, registered);
        await h.QueueAsync(registered, 10, 1);
        await h.QueueAsync(registered, 20, 2);
        var session = h.Http(h.Bob);
        var t0 = h.Start.ToUnixTimeSeconds();

        // Bob holds a grant under his DID but never registered that DID as a recipient.
        h.ResetTraces();
        var own = h.StatusRequest(h.Bob, h.Bob);
        (await h.SendAsync(own, session)).ShouldBeStatus(own, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: h.Bob);
        h.StoreTrace.Should().BeEmpty();
        h.Mediation.Reads.Should().Equal(("FindGrantByRecipientDidAsync", h.Bob));
        var ownFetch = h.DeliveryRequest(h.Bob, 10, h.Bob);
        (await h.SendAsync(ownFetch, session)).ShouldBeStatus(ownFetch, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: h.Bob);
        session.Attempts.Should().BeEmpty();

        // Unfiltered, and filtered by the DID he did register: the two messages.
        var times = new MessagePickupHandlerTimes(0, t0, t0);
        var all = h.StatusRequest(h.Bob);
        (await h.SendAsync(all, session)).ShouldBeStatus(all, h.Mediator, h.Bob, 2, 30, live: false, times: times);
        var byRegistered = h.StatusRequest(h.Bob, registered);
        (await h.SendAsync(byRegistered, session)).ShouldBeStatus(byRegistered, h.Mediator, h.Bob, 2, 30, live: false, recipientDid: registered, times: times);

        // Once registered, his own DID is a filter like any other (and still empty).
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Bob, new[] { new RecipientUpdate(h.Bob, RecipientUpdateAction.Add) }, default);
        h.ResetTraces();
        var nowRegistered = h.StatusRequest(h.Bob, h.Bob);
        (await h.SendAsync(nowRegistered, session)).ShouldBeStatus(nowRegistered, h.Mediator, h.Bob, 0, 0, live: false, recipientDid: h.Bob);
        h.StoreTrace.Should().Equal("GetStatusAsync");
    }

    // ---- (f) a DID removed from the grant, with messages still queued ------------------------

    [Fact]
    public async Task What_is_queued_for_a_did_removed_from_the_grant_is_never_delivered_and_is_removed_by_the_next_unfiltered_delivery()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var removed = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, removed);
        var kept1 = await h.QueueAsync(h.Alice, 10, 1);
        var stale = new[] { await h.QueueAsync(removed, 20, 2), await h.QueueAsync(removed, 30, 3) };
        var kept2 = await h.QueueAsync(h.Alice, 40, 4);
        (await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(removed, RecipientUpdateAction.Remove) }, default))
            .Should().Equal(RecipientStoreOutcome.Removed);
        var session = h.Http(h.Alice);

        // Filtered by the removed DID: the empty answer, and the message store is not touched —
        // so nothing is removed by it either.
        h.ResetTraces();
        var status = h.StatusRequest(h.Alice, removed);
        (await h.SendAsync(status, session)).ShouldBeStatus(status, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: removed);
        var filtered = h.DeliveryRequest(h.Alice, 10, removed);
        (await h.SendAsync(filtered, session)).ShouldBeStatus(filtered, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: removed);
        h.StoreTrace.Should().BeEmpty();
        session.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(4);

        // Unfiltered: the records of the DID still registered are delivered, in order; those of the
        // removed DID are not, and are removed — exactly the leased records the check looked at,
        // by id, under exactly this grant. The role does not purge the DID.
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;
        var fetch = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(fetch, session)).ShouldBeNoReply();

        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, h.Mediator, h.Alice, null, new[] { kept1, kept2 });
        var removal = h.Store.Acknowledged.Should().ContainSingle().Subject;
        removal.GrantPeerDid.Should().Be(h.Alice);
        removal.Ids.Should().Equal(stale);
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync");
        h.Mediation.Reads.Should().Equal(("GetGrantAsync", h.Alice), ("FindGrantByRecipientDidAsync", h.Alice), ("FindGrantByRecipientDidAsync", removed));
        h.Store.Inner.MessageCount.Should().Be(2);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("the two delivered messages are under their lease, waiting for messages-received");
        var line = h.Fixture.PickupLogs.Skip(logged).Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.State["Count"].Should().Be(2);
        line.Everything.Should().NotContain("did:");

        // And the status afterwards counts what is left.
        var after = h.StatusRequest(h.Alice);
        (await h.SendAsync(after, session)).Status().MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Every_unregistered_did_in_a_batch_is_checked_and_its_leased_records_removed_whatever_the_others_answered()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var gone1 = await h.NewDidAsync();
        var gone2 = await h.NewDidAsync();
        var kept = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, gone1, kept, gone2);
        var stale1 = await h.QueueAsync(gone1, 10, 1);
        var keptId1 = await h.QueueAsync(kept, 10, 2);
        var stale2 = await h.QueueAsync(gone2, 10, 3);
        var stale3 = await h.QueueAsync(gone1, 10, 4);
        var keptId2 = await h.QueueAsync(kept, 10, 5);
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(gone1, RecipientUpdateAction.Remove), new RecipientUpdate(gone2, RecipientUpdateAction.Remove) }, default);
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var fetch = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(fetch, session)).ShouldBeNoReply();

        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, h.Mediator, h.Alice, null, new[] { keptId1, keptId2 });
        h.Mediation.Reads.Should().Equal(
            ("GetGrantAsync", h.Alice),
            ("FindGrantByRecipientDidAsync", gone1),
            ("FindGrantByRecipientDidAsync", kept),
            ("FindGrantByRecipientDidAsync", gone2));
        // One removal, of the three leased records of the two DIDs that are gone, in lease order.
        var removal = h.Store.Acknowledged.Should().ContainSingle().Subject;
        removal.GrantPeerDid.Should().Be(h.Alice);
        removal.Ids.Should().Equal(stale1, stale2, stale3);
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync");
        h.Store.Inner.MessageCount.Should().Be(2);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task A_batch_made_only_of_a_removed_dids_messages_delivers_none_of_them_and_the_same_request_delivers_what_follows(int staleCount)
    {
        // A batch of two. The head of the queue is all stale — two, three or five records of a
        // DID that is gone — and one message of a registered DID follows. A batch that comes to
        // nothing does not end the request: it leases again, removing the leftovers round by round
        // as it comes to them (each time exactly the leased ones, by id), and delivers what follows.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 2);
        var removed = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, removed);
        var stale = new List<string>();
        for (var i = 0; i < staleCount; i++)
            stale.Add(await h.QueueAsync(removed, 10 + i, (byte)i));
        var kept = await h.QueueAsync(h.Alice, 30, 9);
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(removed, RecipientUpdateAction.Remove) }, default);
        var session = h.Http(h.Alice);
        h.ResetTraces();

        var fetch = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(fetch, session)).ShouldBeNoReply();

        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(fetch, h.Mediator, h.Alice, null, new[] { kept });
        session.Attempts.Should().ContainSingle("nothing stale is ever put in a delivery");
        h.Store.Acknowledged.Should().OnlyContain(a => a.GrantPeerDid == h.Alice);
        h.Store.Acknowledged.Select(a => string.Join(",", a.Ids)).Should().Equal(stale.Chunk(2).Select(pair => string.Join(",", pair)));
        h.Store.PurgedRecipients.Should().BeEmpty();
        var rounds = (staleCount + 1 + 1) / 2;
        h.StoreTrace.Where(call => call == "LeaseAsync").Should().HaveCount(rounds);
        h.StoreTrace.Where(call => call == "AcknowledgeAsync").Should().HaveCount((staleCount + 1) / 2);
        h.StoreTrace.Should().NotContain("GetStatusAsync", "a delivery was pushed");
        h.Store.LeaseRequests.Select(r => r.Lease.Id).Distinct().Should().ContainSingle("one request, one lease, however many rounds");
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_did_that_moved_to_another_grant_is_served_from_the_new_grants_queue_only()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var moved = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, moved);
        var aliceOwn = await h.QueueAsync(h.Alice, 10, 1);
        var old = new[] { await h.QueueAsync(moved, 20, 2), await h.QueueAsync(moved, 30, 3) };

        // Alice drops the DID; Bob registers it; a new message for it arrives.
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(moved, RecipientUpdateAction.Remove) }, default);
        await h.EnrolAsync(h.Bob, h.Bob, moved);
        var fresh = await h.QueueAsync(moved, 40, 4);
        h.Store.Enqueued.Last().Message.GrantPeerDid.Should().Be(h.Bob, "a message is filed under the grant that holds its recipient DID when it arrives");
        var aliceSession = h.Http(h.Alice);
        var bobSession = h.Http(h.Bob);
        var t0 = h.Start.ToUnixTimeSeconds();

        // Alice: the DID is no longer hers, filtered or not. She gets her own message only, and
        // her leftovers for the DID are removed — those records, by id, under her key.
        h.ResetTraces();
        var filtered = h.StatusRequest(h.Alice, moved);
        (await h.SendAsync(filtered, aliceSession)).ShouldBeStatus(filtered, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: moved);
        h.StoreTrace.Should().BeEmpty();
        var aliceFetch = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(aliceFetch, aliceSession)).ShouldBeNoReply();
        aliceSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(aliceFetch, h.Mediator, h.Alice, null, new[] { aliceOwn });
        var removal = h.Store.Acknowledged.Should().ContainSingle().Subject;
        removal.GrantPeerDid.Should().Be(h.Alice);
        removal.Ids.Should().Equal(old);
        h.Store.PurgedRecipients.Should().BeEmpty("the DID is Bob's now: a purge by DID is not the role's to make");
        (await h.StoredAsync(h.Alice)).Should().Be(1);
        (await h.StoredAsync(h.Bob)).Should().Be(1, "Bob's message for the same DID is under Bob's key and was not touched");

        // Alice cannot acknowledge Bob's message away either.
        var steal = h.MessagesReceived(h.Alice, new[] { fresh });
        (await h.SendAsync(steal, aliceSession)).Status().MessageCount.Should().Be(1);

        // Bob: his own queue for the DID is separate and untouched by all of the above. The
        // messages queued while the DID was Alice's never reach him.
        var bobStatus = h.StatusRequest(h.Bob, moved);
        (await h.SendAsync(bobStatus, bobSession)).ShouldBeStatus(bobStatus, h.Mediator, h.Bob, 1, 40, live: false, recipientDid: moved, times: new MessagePickupHandlerTimes(0, t0, t0));
        var bobFetch = h.DeliveryRequest(h.Bob, 10, moved);
        (await h.SendAsync(bobFetch, bobSession)).ShouldBeNoReply();
        bobSession.Sent.Should().ContainSingle().Which.ShouldBeDelivery(bobFetch, h.Mediator, h.Bob, moved, new[] { fresh });
        foreach (var id in old)
            bobSession.Sent.Should().OnlyContain(m => m.Attachments!.All(a => a.Id != id));
    }

    // ---- (g) a revoked grant ----------------------------------------------------------------

    [Fact]
    public async Task A_revoked_grant_is_an_empty_queue_and_no_request_delivers_or_purges_its_leftovers()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        var second = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, second);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(second, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        (await h.Mediation.Inner.RevokeAsync(h.Alice, default)).Should().BeTrue();
        var http = h.Http(h.Alice);
        var socket = h.Live(h.Alice);
        h.ResetTraces();

        var status = h.StatusRequest(h.Alice);
        (await h.SendAsync(status, http)).ShouldBeStatus(status, h.Mediator, h.Alice, 0, 0, live: false);
        var byOwn = h.StatusRequest(h.Alice, h.Alice);
        (await h.SendAsync(byOwn, http)).ShouldBeStatus(byOwn, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: h.Alice);
        var bySecond = h.StatusRequest(h.Alice, second);
        (await h.SendAsync(bySecond, http)).ShouldBeStatus(bySecond, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: second);
        var fetch = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(fetch, http)).ShouldBeStatus(fetch, h.Mediator, h.Alice, 0, 0, live: false);
        var fetchLive = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(fetchLive, socket)).ShouldBeStatus(fetchLive, h.Mediator, h.Alice, 0, 0, live: false);
        var fetchFiltered = h.DeliveryRequest(h.Alice, 10, second);
        (await h.SendAsync(fetchFiltered, http)).ShouldBeStatus(fetchFiltered, h.Mediator, h.Alice, 0, 0, live: false, recipientDid: second);
        var off = h.LiveChange(h.Alice, false);
        (await h.SendAsync(off, socket)).ShouldBeStatus(off, h.Mediator, h.Alice, 0, 0, live: false);

        // Not one message-store call: nothing read, leased, delivered or purged. The leftovers stay
        // until the host calls PurgeGrantAsync.
        h.StoreTrace.Should().BeEmpty();
        http.Attempts.Should().BeEmpty();
        socket.Attempts.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(3);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Fact]
    public async Task Without_a_grant_an_acknowledgement_still_reaches_only_the_callers_own_key()
    {
        // JC-16: messages-received needs no grant lookup, because it cannot leave the caller's key.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var aliceIds = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var bobIds = new[] { await h.QueueAsync(h.Bob, 5, 3) };
        await h.Mediation.Inner.RevokeAsync(h.Alice, default);
        h.ResetTraces();

        // Revoked Alice names one of her leftovers and Bob's id.
        var request = h.MessagesReceived(h.Alice, new[] { aliceIds[0], bobIds[0] });
        (await h.SendAsync(request)).ShouldBeStatus(request, h.Mediator, h.Alice, 0, 0, live: false);

        h.Store.Acknowledged.Should().ContainSingle().Which.GrantPeerDid.Should().Be(h.Alice);
        h.StoreTrace.Should().Equal("AcknowledgeAsync");
        (await h.DeliverableAsync(h.Bob)).Should().Equal(bobIds);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(aliceIds[1]);
    }
}
