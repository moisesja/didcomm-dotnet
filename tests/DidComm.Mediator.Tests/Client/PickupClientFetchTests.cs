using System.Text;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols;
using Microsoft.Extensions.Logging;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="PickupClient.FetchAsync"/> against a fake exchange (#96 design §4.7, FR-PICK-11): what
/// is read (only the first <c>limit</c> attachments, L-055), what is returned (delivery order,
/// failures as items, repeats left out), and — the point of most tests here — what is acknowledged:
/// exactly the messages that unpacked plus the repeats, never a failure, never something beyond the
/// limit, and only after the last cancellation point (L-057).
/// </summary>
public sealed class PickupClientFetchTests
{
    // ---- answers ------------------------------------------------------------------------------

    [Fact]
    public async Task A_status_answer_is_a_drained_batch_carrying_the_status_and_nothing_is_acknowledged()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Status(r, count: 0));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 7);

        batch.Drained.Should().BeTrue();
        batch.Items.Should().BeEmpty();
        batch.Status.Should().NotBeNull();
        batch.Status!.MessageCount.Should().Be(0);
        var request = exchange.Sent.Should().ContainSingle().Subject;
        request.Is(MessagePickup.DeliveryRequestType).Should().BeTrue();
        request.Limit.Should().Be(7);
        request.RecipientDid.Should().BeNull();
        request.Spec.Should().BeSameAs(DeliveryReader.DeliveryRequestSpec);
        request.Spec.ExpectsDelivery.Should().BeTrue("over HTTP a thid-less delivery must still complete the request");
        request.Request.From.Should().Be(L);
        request.Request.To.Should().Equal(M);
        request.Request.CreatedTime.Should().BeNull("the send path stamps created_time (FR-RR-09), never the client");
        Guid.TryParse(request.Request.Id, out _).Should().BeTrue("request ids are fresh UUIDs, never a host generator's");
        exchange.RouteCalls.Should().ContainSingle().Which.RequireWebSocket.Should().BeFalse();
    }

    [Fact]
    public async Task A_delivery_without_attachments_is_drained_with_no_status()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Drained.Should().BeTrue();
        batch.Status.Should().BeNull();
        batch.Items.Should().BeEmpty();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_message_that_unpacked_is_returned_in_delivery_order_and_acknowledged_in_one_message()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2"), ("q3", "ok:m3")));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Drained.Should().BeFalse();
        batch.Status.Should().BeNull();
        batch.Items.Select(i => i.Id).Should().Equal("q1", "q2", "q3");
        batch.Items.Select(i => i.Unpacked!.Message.Id).Should().Equal("m1", "m2", "m3");
        batch.Items.Should().OnlyContain(i => i.Error == null);
        exchange.Unpacked.Should().Equal("ok:m1", "ok:m2", "ok:m3");
        var ack = exchange.SentOf(MessagePickup.MessagesReceivedType).Should().ContainSingle().Subject;
        ack.Ids.Should().Equal("q1", "q2", "q3");
        ack.Spec.Should().BeSameAs(DeliveryReader.MessagesReceivedSpec);
        exchange.Duplicates.Count.Should().Be(3, "what was returned is remembered at the commit point");
    }

    [Fact]
    public async Task The_acknowledgement_is_exactly_the_messages_that_unpacked_plus_the_repeats()
    {
        // L-057: a give-back names what it took. Every kind of entry in one delivery.
        var exchange = new FakePickupExchange();
        exchange.Duplicates.Add(L, DefaultOutcome("ok:seen-before").DedupeKey);
        exchange.On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry("good1", "ok:m1"),
            Entry("fails", "bad:not-for-us"),
            Entry("repeat-old", "ok:seen-before"),
            PoisonedAttachment("undecodable"),
            Entry(null, "ok:no-id"),
            Entry("good2", "ok:m2"),
            Entry("repeat-in-batch", "ok:m1"),
            Entry("good1", "ok:second-with-same-id"),
            Entry("anon", "anon:bytes"),
        }));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 100);

        exchange.AcknowledgedIds.Should().Equal(new[] { "good1", "repeat-old", "good2", "repeat-in-batch", "anon" },
            "acknowledged: what unpacked and is new, plus repeats; never a failure, an id-less attachment or a second use of an id");
        batch.Items.Select(i => (i.Id, Ok: i.Unpacked != null)).Should().Equal(
            ("good1", true), ("fails", false), ("undecodable", false), ("good2", true), ("anon", true));
        batch.Items.Single(i => i.Id == "undecodable").Error.Should().BeOfType<MalformedMessageException>();
        batch.Items.Single(i => i.Id == "fails").Error.Should().BeOfType<MalformedMessageException>();
        exchange.Unpacked.Should().NotContain("ok:second-with-same-id", "the first attachment with an id wins; a later one is not even unpacked");
        exchange.Unpacked.Should().NotContain("ok:no-id", "a message that cannot be acknowledged is not unpacked");
    }

    [Fact]
    public async Task Failures_are_never_acknowledged_even_when_nothing_unpacked()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "bad:1"), ("q2", "bad:2")));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        batch.Items.Should().HaveCount(2).And.OnlyContain(i => i.Unpacked == null && i.Error != null);
        batch.Drained.Should().BeFalse();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty("FR-PICK-11: a failure stays with the mediator");
        exchange.Duplicates.Count.Should().Be(0);
    }

    // ---- the limit (L-055) --------------------------------------------------------------------

    [Fact]
    public async Task Attachments_beyond_the_limit_are_never_decoded_unpacked_or_acknowledged()
    {
        // The out-of-bound attachment is itself poisoned (its data is not base64): had it been read,
        // it would be a failed item. So no failed item proves it was never decoded.
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry("q1", "ok:m1"),
            Entry("q2", "ok:m2"),
            PoisonedAttachment("beyond"),
        }));
        var batch = await PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 2);

        batch.Items.Select(i => i.Id).Should().Equal("q1", "q2");
        batch.Items.Should().OnlyContain(i => i.Error == null, "the poisoned attachment beyond the limit was never decoded");
        exchange.Unpacked.Should().Equal("ok:m1", "ok:m2");
        exchange.AcknowledgedIds.Should().Equal("q1", "q2");
        logs.Entries.Should().Contain(e => e.Message.Contains("1 message(s) beyond the limit"));
    }

    [Fact]
    public async Task An_unusable_id_beyond_the_limit_is_not_even_counted()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry("q1", "ok:m1"),
            Entry(new string('x', 3000), "ok:huge-id"),
            null,
        }));
        var batch = await PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 1);

        batch.Items.Should().ContainSingle().Which.Id.Should().Be("q1");
        logs.Entries.Should().NotContain(e => e.Message.Contains("without a usable id"), "attachments beyond the limit are not looked at");
        logs.Entries.Should().Contain(e => e.Message.Contains("2 message(s) beyond the limit"));
    }

    // ---- ids ----------------------------------------------------------------------------------

    [Fact]
    public async Task Attachments_without_a_usable_id_are_ignored_counted_and_never_acknowledged()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry(null, "ok:a"),
            Entry(string.Empty, "ok:b"),
            Entry(new string('i', PickupBodies.MaxIdLength + 1), "ok:c"),
            Entry("has space", "ok:d"),
            Entry("q/slash", "ok:e"),
            Entry("q-ok", "ok:f"),
        }));
        var batch = await PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 10);

        batch.Items.Should().ContainSingle().Which.Id.Should().Be("q-ok");
        exchange.Unpacked.Should().Equal("ok:f");
        exchange.AcknowledgedIds.Should().Equal("q-ok");
        logs.Entries.Should().Contain(e => e.Message.Contains("5 message(s) without a usable id"));
    }

    [Fact]
    public async Task A_delivery_in_which_no_message_has_a_usable_id_is_a_MediationProtocolException_naming_our_request()
    {
        // Returned as a batch it would be neither drained nor carry an item, and a loop that fetches
        // until drained would ask again for ever (QA-2 observation 7).
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            Entry(null, "ok:a"),
            Entry(string.Empty, "ok:b"),
            Entry("has space", "ok:c"),
            null,
        }));

        var thrown = (await FluentActions.Awaiting(() => PickupTest.Client(exchange).FetchAsync(M, L, limit: 10))
            .Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.RequestId.Should().Be(exchange.SentOf(MessagePickup.DeliveryRequestType).Single().Request.Id);
        thrown.Message.Should().NotContain("has space").And.NotContain(L).And.NotContain(M);
        exchange.Unpacked.Should().BeEmpty();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
        exchange.Duplicates.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_repeated_attachment_id_is_read_once_and_the_first_wins()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
        {
            PoisonedAttachment("q1"),
            Entry("q1", "ok:m1"),
        }));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);

        var item = batch.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be("q1");
        item.Error.Should().BeOfType<MalformedMessageException>("the first attachment with the id wins, even when it is the bad one");
        exchange.Unpacked.Should().BeEmpty();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
    }

    // ---- the filter (D-20) --------------------------------------------------------------------

    [Fact]
    public async Task The_recipient_filter_is_sent_and_enforced_against_the_DID_that_decrypted()
    {
        const string carol = "did:example:carol";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r,
            ("q1", $"to:{carol}:m1"),
            ("q2", $"to:{L}:m2"),
            ("q3", $"to:{carol}:m3")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: carol);

        exchange.SentOf(MessagePickup.DeliveryRequestType).Single().RecipientDid.Should().Be(carol);
        batch.Items.Select(i => (i.Id, Ok: i.Unpacked != null)).Should().Equal(("q1", true), ("q2", false), ("q3", true));
        var refused = batch.Items[1].Error.Should().BeOfType<MediationProtocolException>().Subject;
        refused.RequestId.Should().Be(exchange.SentOf(MessagePickup.DeliveryRequestType).Single().Request.Id);
        refused.Message.Should().NotContain(L).And.NotContain(carol);
        exchange.AcknowledgedIds.Should().Equal(new[] { "q1", "q3" }, "a message for another DID than the filter is left with the mediator");
        exchange.Duplicates.Contains(L, DefaultOutcome($"to:{L}:m2").DedupeKey).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_filter_a_message_for_any_of_our_DIDs_is_returned()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "to:did:example:carol:m1")));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10);
        batch.Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull();
    }

    // Owner decision 1-A (L-063 stop): under a filter on L only a message one of L's own keys opened
    // is L's. The envelope's other recipient labels are not evidence (L-040; ADV1-2, ADV1-3): one a
    // sender encrypted to carol and L opens with carol's key (the first held key it lists) and is a
    // failure under L's filter, though its envelope names L; it is accepted unfiltered or under
    // carol's filter. A message L's key opened passes whatever else its envelope lists.
    [Fact]
    public async Task Under_a_filter_only_a_message_one_of_the_filter_DIDs_own_keys_opened_passes_whatever_its_envelope_lists()
    {
        const string carol = "did:example:carol";
        const string dave = "did:example:dave";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r,
            ("q1", "multi:m1"),
            ("q2", "mine:m2"),
            ("q3", "multi-last:m3")));
        exchange.Unpacker = (text, _) => Task.FromResult(text switch
        {
            "multi:m1" => Encrypted(text, "m1", decryptedFor: carol, carol + "#key-1", L + "#key-1"),
            "mine:m2" => Encrypted(text, "m2", decryptedFor: L, carol + "#key-1", dave + "#key-1", L + "#key-1"),
            "multi-last:m3" => Encrypted(text, "m3", decryptedFor: carol, carol + "#key-1", dave + "#key-1", L + "#key-2"),
            _ => DefaultOutcome(text),
        });

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: L);

        var requestId = exchange.SentOf(MessagePickup.DeliveryRequestType).Single().Request.Id;
        batch.Items.Select(i => (i.Id, Ok: i.Unpacked != null)).Should().Equal(("q1", false), ("q2", true), ("q3", false));
        batch.Items[1].Unpacked!.RecipientKid.Should().Be(L + "#key-1");
        foreach (var refused in new[] { batch.Items[0], batch.Items[2] })
        {
            var error = refused.Error.Should().BeOfType<MediationProtocolException>("carol's key opened it, though its envelope names L").Subject;
            error.RequestId.Should().Be(requestId);
            error.Message.Should().Contain(requestId).And.Contain("another of your DIDs").And.Contain("AckAsync");
            error.Message.Should().NotContain(L).And.NotContain(carol).And.NotContain(dave);
        }
        exchange.AcknowledgedIds.Should().Equal(new[] { "q2" }, "only the message L's own key opened is L's");
    }

    [Fact]
    public async Task A_message_its_sender_encrypted_to_two_of_our_DIDs_is_accepted_unfiltered_or_under_the_filter_of_the_DID_whose_key_opened_it()
    {
        const string carol = "did:example:carol";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "multi:m1")));
        exchange.Unpacker = (text, _) => Task.FromResult(text == "multi:m1"
            ? Encrypted(text, "m1", decryptedFor: carol, carol + "#key-1", L + "#key-1")
            : DefaultOutcome(text));
        var client = PickupTest.Client(exchange);

        (await client.FetchAsync(M, L, limit: 10, recipientDid: L)).Items.Should().ContainSingle().Which.Error.Should().BeOfType<MediationProtocolException>();
        (await client.FetchAsync(M, L, limit: 10, recipientDid: carol)).Items.Should().ContainSingle().Which.Unpacked!.RecipientKid.Should().Be(carol + "#key-1");
        exchange.AcknowledgedIds.Should().Equal("q1");

        var unfiltered = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "multi:m1")));
        unfiltered.Unpacker = exchange.Unpacker;
        (await PickupTest.Client(unfiltered).FetchAsync(M, L, limit: 10)).Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull();
        unfiltered.AcknowledgedIds.Should().Equal("q1");
    }

    // ADV1-1: repeats are remembered per mailbox (the `from` of the fetch). A copy a third party
    // relayed into another DID's mailbox, once returned there, must not make this mailbox's own copy a
    // repeat; within one mailbox a repeat is still a repeat.
    [Fact]
    public async Task A_message_returned_from_one_mailbox_is_not_a_repeat_in_another()
    {
        const string other = "did:example:other";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")));
        var client = PickupTest.Client(exchange);

        (await client.FetchAsync(M, other, limit: 10)).Items.Should().ContainSingle().Which.Unpacked.Should().NotBeNull();
        (await client.FetchAsync(M, L, limit: 10)).Items.Should().ContainSingle("this mailbox never returned it").Which.Unpacked!.Message.Id.Should().Be("m1");
        (await client.FetchAsync(M, L, limit: 10)).Items.Should().BeEmpty("in the same mailbox it is a repeat");
        exchange.AcknowledgedIds.Should().Equal("q1", "q1", "q1");
    }

    [Fact]
    public async Task Under_a_filter_a_decrypting_key_id_over_the_identifier_bounds_does_not_pass_even_when_it_starts_with_the_filters_DID()
    {
        var overLong = L + "#" + new string('k', 4096);           // over 4096 characters in all
        var longDid = "did:example:" + new string('b', 2048);    // a DID part over 2048 characters
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "long:m1"), ("q2", "longdid:m2")));
        exchange.Unpacker = (text, _) => Task.FromResult(text switch
        {
            "long:m1" => Success(Authcrypt(A, L, "m1") with { RecipientKid = overLong, AllRecipientKids = new[] { overLong } }, text),
            "longdid:m2" => Success(Authcrypt(A, L, "m2") with { RecipientKid = longDid + "#key-1", AllRecipientKids = new[] { longDid + "#key-1" } }, text),
            _ => DefaultOutcome(text),
        });

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: L);

        batch.Items.Should().HaveCount(2).And.OnlyContain(i => i.Error is MediationProtocolException);
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_filter_the_copies_of_a_message_encrypted_to_two_of_our_DIDs_are_returned_once_and_all_acknowledged()
    {
        // The mediator queues one copy per DID; both open with the same key, so they are the same message.
        var exchange = new FakePickupExchange().OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("for-carol", "multi:m1"), ("for-bob", "multi:m1")),
            r => Delivery(r, ("for-bob-again", "multi:m1")));
        exchange.Unpacker = (text, _) => Task.FromResult(text == "multi:m1"
            ? Encrypted(text, "m1", decryptedFor: "did:example:carol", "did:example:carol#key-1", L + "#key-1")
            : DefaultOutcome(text));
        var client = PickupTest.Client(exchange);

        (await client.FetchAsync(M, L, limit: 10)).Items.Should().ContainSingle().Which.Id.Should().Be("for-carol");
        (await client.FetchAsync(M, L, limit: 10)).Items.Should().BeEmpty("a later copy is a repeat");
        exchange.AcknowledgedIds.Should().Equal("for-carol", "for-bob", "for-bob-again");
    }

    [Theory]
    [InlineData("did:example:carol", "did:example:dave")]
    [InlineData(null, "did:example:dave")]
    public async Task A_status_about_another_DID_than_asked_is_refused(string? filter, string echoed)
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Status(r, recipientDid: echoed));
        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: filter);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.RequestId.Should().Be(exchange.Sent.Single().Request.Id);
        thrown.ProblemCode.Should().BeNull();
        thrown.Message.Should().NotContain(echoed);
    }

    [Fact]
    public async Task A_status_repeating_the_filter_is_accepted()
    {
        const string carol = "did:example:carol";
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Status(r, count: 3, recipientDid: carol));
        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: carol);
        batch.Drained.Should().BeTrue();
        batch.Status!.MessageCount.Should().Be(3);
    }

    [Fact]
    public async Task A_delivery_about_another_DID_than_asked_is_refused_before_anything_is_unpacked_or_acknowledged()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType,
            r => RawDelivery(r, new Attachment?[] { Entry("q1", "ok:m1") }, recipientDid: "did:example:dave"));
        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, limit: 10, recipientDid: "did:example:carol");

        await act.Should().ThrowAsync<MediationProtocolException>();
        exchange.Unpacked.Should().BeEmpty();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
    }

    // ---- repeats ------------------------------------------------------------------------------

    [Fact]
    public async Task A_message_already_returned_is_acknowledged_again_and_left_out()
    {
        // A replayed forward: the same message under a new queue id.
        var exchange = new FakePickupExchange().OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1")),
            r => Delivery(r, ("q9", "ok:m1"), ("q10", "ok:m2")));
        var client = PickupTest.Client(exchange);

        (await client.FetchAsync(M, L, 10)).Items.Select(i => i.Id).Should().Equal("q1");
        var second = await client.FetchAsync(M, L, 10);

        second.Items.Select(i => i.Id).Should().Equal("q10");
        exchange.AcknowledgedIds.Should().Equal("q1", "q9", "q10");
    }

    [Fact]
    public async Task With_the_duplicate_filter_off_a_redelivered_message_is_returned_again()
    {
        var exchange = new FakePickupExchange(o => o.DedupeCapacity = 0).OnSequence(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1")),
            r => Delivery(r, ("q9", "ok:m1")));
        var client = PickupTest.Client(exchange);

        (await client.FetchAsync(M, L, 10)).Items.Should().ContainSingle();
        (await client.FetchAsync(M, L, 10)).Items.Should().ContainSingle().Which.Id.Should().Be("q9");
    }

    [Fact]
    public async Task With_the_duplicate_filter_off_a_message_twice_in_one_delivery_is_still_returned_once()
    {
        var exchange = new FakePickupExchange(o => o.DedupeCapacity = 0).On(MessagePickup.DeliveryRequestType,
            r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m1")));

        var batch = await PickupTest.Client(exchange).FetchAsync(M, L, 10);

        batch.Items.Should().ContainSingle().Which.Id.Should().Be("q1");
        exchange.AcknowledgedIds.Should().Equal("q1", "q2");
    }

    // ---- the commit point -----------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_before_the_commit_acknowledges_and_records_nothing_so_everything_comes_back()
    {
        using var cts = new CancellationTokenSource();
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1"), ("q2", "ok:m2")));
        // The cancellation lands after the unpack, before the commit: the last cancellation point.
        exchange.AfterBatchUnpack = cts.Cancel;
        var client = PickupTest.Client(exchange);

        var act = () => client.FetchAsync(M, L, 10, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();
        exchange.Duplicates.Count.Should().Be(0, "nothing was committed, so the redelivery is returned, not filtered");

        exchange.AfterBatchUnpack = null;
        var again = await client.FetchAsync(M, L, 10);
        again.Items.Select(i => i.Id).Should().Equal("q1", "q2");
        exchange.AcknowledgedIds.Should().Equal("q1", "q2");
    }

    [Fact]
    public async Task Cancellation_after_the_commit_still_returns_the_batch_and_the_acknowledgement_is_not_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var ackGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var exchange = new FakePickupExchange()
            .On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")))
            .On(MessagePickup.MessagesReceivedType, async r =>
            {
                await ackGate.Task;
                r.Ct.ThrowIfCancellationRequested();
                answered = true;
                return Status(r);
            });

        var fetch = PickupTest.Client(exchange).FetchAsync(M, L, 10, ct: cts.Token);
        await exchange.WaitForSentAsync(2);
        await cts.CancelAsync();
        ackGate.SetResult(true);

        var batch = await PickupTest.Within(fetch);
        batch.Items.Should().ContainSingle().Which.Id.Should().Be("q1");
        answered.Should().BeTrue("the acknowledgement runs on the client's own deadline, not the caller's token");
        exchange.SentOf(MessagePickup.MessagesReceivedType).Single().Ct.Should().NotBe(cts.Token);
    }

    [Fact]
    public async Task A_failed_acknowledgement_keeps_the_results_and_the_redelivery_is_filtered()
    {
        var logs = new PickupTestLogs();
        var acks = 0;
        var exchange = new FakePickupExchange()
            .On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")))
            .On(MessagePickup.MessagesReceivedType, r => ++acks == 1
                ? Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer"))
                : Task.FromResult(Status(r)));
        var client = PickupTest.Client(exchange, logs);

        var first = await client.FetchAsync(M, L, 10);
        first.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be("m1", "the batch is returned whatever happens to the acknowledgement");
        logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("TimeoutException") && e.Message.Contains("failed"));
        logs.Entries.Should().NotContain(e => e.Message.Contains("no answer"), "exception texts are never logged");

        // The mediator delivers it again (the acknowledgement was lost): acknowledged, not returned.
        var second = await client.FetchAsync(M, L, 10);
        second.Items.Should().BeEmpty();
        second.Drained.Should().BeFalse();
        exchange.AcknowledgedIds.Should().Equal("q1", "q1");
    }

    [Fact]
    public async Task An_acknowledgement_that_never_answers_is_bounded_by_RequestTimeout_on_the_injected_clock()
    {
        var exchange = new FakePickupExchange(o => o.RequestTimeout = TimeSpan.FromSeconds(30))
            .On(MessagePickup.DeliveryRequestType, r => Delivery(r, ("q1", "ok:m1")))
            .On(MessagePickup.MessagesReceivedType, async r =>
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, r.Ct);
                return Status(r);
            });

        var fetch = PickupTest.Client(exchange).FetchAsync(M, L, 10);
        await exchange.WaitForSentAsync(2);
        fetch.IsCompleted.Should().BeFalse();
        exchange.Clock.Advance(TimeSpan.FromSeconds(29));
        fetch.IsCompleted.Should().BeFalse();
        exchange.Clock.Advance(TimeSpan.FromSeconds(1));

        (await PickupTest.Within(fetch)).Items.Should().ContainSingle();
    }

    // ---- malformed answers --------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_status_is_a_MediationProtocolException_naming_our_request()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Snapshot(new Message
        {
            Id = "a1",
            Type = MessagePickup.StatusType,
            Thid = r.Request.Id,
            Body = new System.Text.Json.Nodes.JsonObject { ["message_count"] = "many" },
        }));
        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, 10);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.RequestId.Should().Be(exchange.Sent.Single().Request.Id);
        thrown.InnerException.Should().BeOfType<MalformedMessageException>();
        thrown.Message.Should().Contain(thrown.RequestId).And.Contain("delivery-request");
    }

    [Fact]
    public async Task An_answer_of_another_type_is_refused()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.DeliveryRequestType, r => Snapshot(new Message
        {
            Id = "a1",
            Type = "https://didcomm.org/basicmessage/2.0/message",
            Thid = r.Request.Id,
        }));
        var act = () => PickupTest.Client(exchange).FetchAsync(M, L, 10);
        await act.Should().ThrowAsync<MediationProtocolException>();
    }

    // ---- the drain loop (L-056 / L-058) ---------------------------------------------------------

    [Fact]
    public async Task A_loop_that_fetches_until_drained_gets_past_more_failures_than_its_limit_within_the_lease()
    {
        var mailbox = new FakeMailbox(new ManualTimeProvider(), ("f1", "bad:1"), ("f2", "bad:2"), ("f3", "bad:3"), ("g1", "ok:good"));
        var exchange = mailbox.Attach(new FakePickupExchange(time: mailbox.Time));
        var client = PickupTest.Client(exchange);

        var seen = new List<PickupItem>();
        PickupBatch batch;
        var rounds = 0;
        do
        {
            batch = await client.FetchAsync(M, L, limit: 2);
            seen.AddRange(batch.Items);
        }
        while (!batch.Drained && ++rounds < 10);

        batch.Drained.Should().BeTrue();
        seen.Where(i => i.Unpacked != null).Select(i => i.Id).Should().Equal("g1");
        seen.Where(i => i.Error != null).Select(i => i.Id).Should().Equal("f1", "f2", "f3");
        mailbox.Remaining.Should().Equal(new[] { "f1", "f2", "f3" }, "failures are never acknowledged (FR-PICK-11)");
    }

    [Fact]
    public async Task A_caller_polling_slower_than_the_lease_with_more_failures_than_its_limit_sees_only_failures_until_it_discards_them()
    {
        // The documented residual (L-058): the client never acknowledges a failure on its own, so a
        // poller that comes back only after every lease ran out meets the same failures first.
        var mailbox = new FakeMailbox(new ManualTimeProvider(), ("f1", "bad:1"), ("f2", "bad:2"), ("f3", "bad:3"), ("g1", "ok:good"));
        var exchange = mailbox.Attach(new FakePickupExchange(time: mailbox.Time));
        var client = PickupTest.Client(exchange);

        for (var poll = 0; poll < 5; poll++)
        {
            var batch = await client.FetchAsync(M, L, limit: 2);
            batch.Items.Select(i => i.Id).Should().Equal(new[] { "f1", "f2" }, "poll {0} came after the lease ended", poll);
            batch.Items.Should().OnlyContain(i => i.Error != null);
            mailbox.Time.Advance(FakeMailbox.Lease + TimeSpan.FromSeconds(1));
        }
        exchange.SentOf(MessagePickup.MessagesReceivedType).Should().BeEmpty();

        // Discarding them is the caller's decision, through AckAsync.
        (await client.AckAsync(M, L, new[] { "f1", "f2", "f3" })).MessageCount.Should().Be(1);
        var next = await client.FetchAsync(M, L, limit: 2);
        next.Items.Should().ContainSingle().Which.Unpacked!.Message.Id.Should().Be("good");
        mailbox.Remaining.Should().BeEmpty();
    }

    // ---- arguments ----------------------------------------------------------------------------

    public static TheoryData<string, Func<PickupClient, Task>> BadArguments => new()
    {
        { "limit 0", c => c.FetchAsync(M, L, 0) },
        { "limit 101", c => c.FetchAsync(M, L, 101) },
        { "mediator DID URL", c => c.FetchAsync(M + "#key-1", L, 10) },
        { "mediator not a DID", c => c.FetchAsync("https://mediator.example", L, 10) },
        { "from DID URL", c => c.FetchAsync(M, L + "/path", 10) },
        { "from empty", c => c.FetchAsync(M, string.Empty, 10) },
        { "from over-long", c => c.FetchAsync(M, "did:example:" + new string('a', 2048), 10) },
        { "filter DID URL", c => c.FetchAsync(M, L, 10, recipientDid: L + "?x=1") },
        { "filter empty", c => c.FetchAsync(M, L, 10, recipientDid: string.Empty) },
        { "relative override", c => c.FetchAsync(M, L, 10, serviceEndpointOverride: new Uri("/didcomm", UriKind.Relative)) },
        { "override with user info", c => c.FetchAsync(M, L, 10, serviceEndpointOverride: new Uri("https://user:secret@mediator.example/")) },
    };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public async Task Bad_arguments_are_refused_before_anything_is_resolved_or_sent(string name, Func<PickupClient, Task> call)
    {
        var exchange = new FakePickupExchange();
        var act = () => call(PickupTest.Client(exchange));

        var thrown = (await act.Should().ThrowAsync<ArgumentException>(name)).Which;
        thrown.Message.Should().NotContain("secret").And.NotContain(L + "/path").And.NotContain(L + "?x=1");
        exchange.RouteCalls.Should().BeEmpty(name);
        exchange.Sent.Should().BeEmpty(name);
    }

    [Fact]
    public async Task A_null_DID_is_an_ArgumentNullException()
    {
        var act = () => PickupTest.Client(new FakePickupExchange()).FetchAsync(null!, L, 10);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task The_override_is_handed_to_route_resolution()
    {
        var exchange = new FakePickupExchange();
        var endpoint = new Uri("wss://mediator.example/ws");
        await PickupTest.Client(exchange).FetchAsync(M, L, 10, serviceEndpointOverride: endpoint);
        exchange.RouteCalls.Should().ContainSingle().Which.Should().Be((M, endpoint, false));
        exchange.Sent.Single().Route.Should().BeSameAs(exchange.Route, "every request of the operation uses the route resolved once");
    }

    // ---- logs ---------------------------------------------------------------------------------

    [Fact]
    public async Task Log_lines_carry_no_DID_attachment_id_or_message_id()
    {
        var logs = new PickupTestLogs();
        var exchange = new FakePickupExchange()
            .On(MessagePickup.DeliveryRequestType, r => RawDelivery(r, new Attachment?[]
            {
                Entry("attachment-one", "ok:message-one"),
                Entry("attachment-two", "bad:x"),
                Entry("attachment-one", "ok:message-two"),
                Entry(null, "ok:message-three"),
                PoisonedAttachment("attachment-three"),
                Entry("attachment-four", "to:did:example:carol:message-four"),
                Entry("attachment-beyond", "ok:message-five"),
            }))
            .On(MessagePickup.MessagesReceivedType, _ => Task.FromException<InboundMessageSnapshot>(new TransportException("connection to did:example:mediator lost")));

        await PickupTest.Client(exchange, logs).FetchAsync(M, L, limit: 6, recipientDid: L);

        logs.Entries.Should().NotBeEmpty();
        foreach (var value in new[] { M, L, A, "did:example:carol", "attachment-", "message-", "lost" })
            logs.Entries.Should().NotContain(e => e.Message.Contains(value), "no log line carries {0}", value);
    }

    /// <summary>Alice's authcrypted message <paramref name="id"/>, opened with <paramref name="decryptedFor"/>'s key, from an envelope naming <paramref name="envelopeKids"/>.</summary>
    private static DeliveredOutcome Encrypted(string packed, string id, string decryptedFor, params string[] envelopeKids)
        => Success(Authcrypt(A, decryptedFor, id) with { AllRecipientKids = envelopeKids }, packed);
}

/// <summary>
/// A mediator queue with leases, for the drain-loop tests: a <c>delivery-request</c> takes, oldest
/// first, up to <c>limit</c> messages not under a lease and leases them for <see cref="Lease"/>; a
/// <c>messages-received</c> removes them; nothing left to deliver is answered with a <c>status</c>.
/// </summary>
internal sealed class FakeMailbox
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly List<(string Id, string Payload, DateTimeOffset LeasedUntil)> _queue = new();

    public FakeMailbox(ManualTimeProvider time, params (string Id, string Payload)[] messages)
    {
        Time = time;
        foreach (var (id, payload) in messages)
            _queue.Add((id, payload, DateTimeOffset.MinValue));
    }

    public ManualTimeProvider Time { get; }

    public IReadOnlyList<string> Remaining
    {
        get
        {
            lock (_gate)
                return _queue.Select(q => q.Id).ToArray();
        }
    }

    public FakePickupExchange Attach(FakePickupExchange exchange)
        => exchange
            .On(MessagePickup.DeliveryRequestType, Deliver)
            .On(MessagePickup.MessagesReceivedType, Acknowledge)
            .On(MessagePickup.StatusRequestType, r => FakePickupExchange.Status(r, Count()));

    private InboundMessageSnapshot Deliver(SentRequest request)
    {
        var now = Time.GetUtcNow();
        var taken = new List<(string, string)>();
        lock (_gate)
        {
            for (var i = 0; i < _queue.Count && taken.Count < request.Limit; i++)
            {
                if (_queue[i].LeasedUntil > now)
                    continue;
                _queue[i] = (_queue[i].Id, _queue[i].Payload, now + Lease);
                taken.Add((_queue[i].Id, _queue[i].Payload));
            }
        }
        return taken.Count == 0 ? FakePickupExchange.Status(request, Count()) : FakePickupExchange.Delivery(request, taken.ToArray());
    }

    private InboundMessageSnapshot Acknowledge(SentRequest request)
    {
        lock (_gate)
            _queue.RemoveAll(q => request.Ids.Contains(q.Id));
        return FakePickupExchange.Status(request, Count());
    }

    private int Count()
    {
        lock (_gate)
            return _queue.Count;
    }
}
