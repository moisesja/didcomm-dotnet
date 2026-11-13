using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #94 QA-5 — Message Pickup 3.0 (mediator role) end to end over the registry-aware HTTP endpoint
/// of a TestServer host with <c>AddCoordinateMediation()</c> and <c>AddMessagePickup()</c>: the
/// "Dispatch" acceptance of the issue and the FR-PICK acceptance cells that need a transport.
/// </summary>
/// <remarks>
/// Every request is authcrypted by a real did:peer identity, carries <c>return_route: all</c> and a
/// <c>created_time</c> from the server's injected clock (what the endpoint's admission requires),
/// and its answer is read from the <c>200</c> body with the recipient's own client. The payloads
/// the queue relays are real encrypted DIDComm messages (authcrypted by a third identity), so each
/// attachment is unpacked by the recipient and compared byte for byte with what was queued. The
/// recipient's client accepts what the product accepts by default (1 MiB): nothing is raised.
/// </remarks>
public sealed class MessagePickupHttpTests
{
    private const int Kibibyte = PickupEndpointHost.Kibibyte;
    private const int Mebibyte = PickupEndpointHost.Mebibyte;

    private readonly ITestOutputHelper _output;

    public MessagePickupHttpTests(ITestOutputHelper output) => _output = output;

    // ---- the walkthrough as a recipient application sends it --------------------------------

    [Fact]
    public async Task The_walkthrough_sent_with_DidCommClient_SendAsync_and_return_route_all_returns_every_answer_to_the_caller()
    {
        // No hand-made stamps or headers here: the recipient is a DidCommClient with the HTTP
        // transport, sending with SendOptions.ReturnRoute = All. Its own admission of what comes
        // back (a fresh created_time on a mediator-service message, a response within its
        // MaxReceiveBytes) is what a real recipient applies to the mediator's answers.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        await using var client = ReturnRouteClientHarness.Create(mediator.Host, holders: recipient);
        var options = client.ToServer(recipient) with { ReturnRoute = ReturnRouteMode.All };
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient, packedBytes: 500 * Kibibyte) };

        async Task<Message> SendAsync(Message request)
        {
            var result = await client.Client.SendAsync(request, options);
            result.Transport.HttpStatusCode.Should().Be(200);
            var returned = result.Returned;
            returned.Should().NotBeNull("the client accepted the mediator's answer");
            returned!.Authenticated.Should().BeTrue();
            returned.SenderKid.Should().StartWith(mediator.Mediator + "#");
            returned.Message.To.Should().Equal(recipient.Did);
            return returned.Message;
        }

        var statusRequest = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        new PickupAnswer(await SendAsync(statusRequest), Array.Empty<byte>()).Status(statusRequest.Id).MessageCount.Should().Be(2);

        // Both fit one delivery (a little under 1 MiB packed), and the client takes it.
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivered = new PickupAnswer(await SendAsync(fetch), Array.Empty<byte>()).Delivery(fetch.Id);
        delivered.Should().HaveCount(2);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[0], queued[0]);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[1], queued[1]);

        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, delivered.Select(m => m.Id).ToArray());
        new PickupAnswer(await SendAsync(ack), Array.Empty<byte>()).Status(ack.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));

        var live = MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true);
        new PickupAnswer(await SendAsync(live), Array.Empty<byte>()).Problem(live.Id)
            .Should().Be(("e.m.live-mode-not-supported", "Connection does not support Live Delivery"));
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- delivery-request → delivery (FR-PICK-03) -------------------------------------------

    [Fact]
    public async Task Delivery_request_limit_3_with_5_queued_returns_one_delivery_of_three_in_arrival_order_relayed_byte_for_byte()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new List<RelayedMessage>();
        for (var i = 0; i < 5; i++)
        {
            queued.Add(await mediator.QueueAsync(recipient));
            mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 3);
        var response = await mediator.PostAsync(recipient, request);

        response.Status.Should().Be(HttpStatusCode.OK);
        response.ContentType.Should().Be("application/didcomm-encrypted+json");
        var answer = await mediator.AnswerAsync(recipient, response);
        var delivered = answer.Delivery(thid: request.Id);
        answer.Message.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "the mediator stamps what it sends back (FR-RR-09)");
        (answer.Message.Body?.ContainsKey("recipient_did") ?? false).Should().BeFalse("the request named no recipient_did");
        delivered.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
            await mediator.ShouldBeRelayedAsync(recipient, delivered[i], queued[i]);
        delivered.Select(m => m.Id).Should().OnlyHaveUniqueItems()
            .And.NotIntersectWith(queued.Select(q => q.InnerId), "an attachment id is the mediator's own, never the inner message id");
        mediator.Store.MessageCount.Should().Be(5, "a delivered message stays stored until it is acknowledged");

        // The other two, in order, on the next request.
        var next = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var rest = (await mediator.AskAsync(recipient, next)).Delivery(thid: next.Id);
        rest.Should().HaveCount(2);
        await mediator.ShouldBeRelayedAsync(recipient, rest[0], queued[3]);
        await mediator.ShouldBeRelayedAsync(recipient, rest[1], queued[4]);
    }

    [Fact]
    public async Task A_limit_above_the_batch_maximum_is_clamped_to_it()
    {
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxDeliveryBatch = 4);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new List<RelayedMessage>();
        for (var i = 0; i < 6; i++)
            queued.Add(await mediator.QueueAsync(recipient));

        var first = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, int.MaxValue);
        var second = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, int.MaxValue);

        (await mediator.AskAsync(recipient, first)).Delivery(first.Id).Select(m => m.Id).Should().Equal(queued.Take(4).Select(q => q.Id));
        (await mediator.AskAsync(recipient, second)).Delivery(second.Id).Select(m => m.Id).Should().Equal(queued.Skip(4).Select(q => q.Id));
    }

    [Fact]
    public async Task An_empty_queue_and_a_limit_of_zero_are_answered_with_status()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);

        var empty = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, empty)).Status(empty.Id)
            .Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));

        var queued = await mediator.QueueAsync(recipient);
        var zero = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 0);
        var status = (await mediator.AskAsync(recipient, zero)).Status(zero.Id);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(queued.Payload.Length);

        // limit: 0 leased nothing: the message is there for the next request.
        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 1);
        (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
    }

    [Theory]
    [InlineData(EnvelopeShape.Authcrypt)]
    [InlineData(EnvelopeShape.AnoncryptOfAuthcrypt)]
    [InlineData(EnvelopeShape.AuthcryptOfSigned)]
    public async Task A_request_in_any_envelope_with_an_authcrypt_layer_is_answered(EnvelopeShape shape)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = await mediator.QueueAsync(recipient);

        async Task<PickupAnswer> AskAsync(Message request)
        {
            var (packed, contentType) = await mediator.Host.PackAsync(recipient, mediator.Prepare(request), shape);
            return await mediator.AnswerAsync(recipient, await mediator.Host.PostAsync(packed, contentType: contentType));
        }

        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        await mediator.ShouldBeRelayedAsync(recipient, (await AskAsync(fetch)).Delivery(fetch.Id).Should().ContainSingle().Subject, queued);
        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued.Id! });
        (await AskAsync(ack)).Status(ack.Id).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_message_that_is_not_yet_due_is_skipped_until_its_time_and_one_that_expires_while_queued_is_never_delivered()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var start = mediator.Clock.Now;

        var delayed = await mediator.PackFromAliceAsync(recipient);
        var delayedResult = await mediator.Queue.DeliverAsync(recipient.Did, delayed.Payload, new PickupDeliveryOptions(NotBefore: start + TimeSpan.FromSeconds(30)));
        delayedResult.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        var expiring = await mediator.PackFromAliceAsync(recipient);
        (await mediator.Queue.DeliverAsync(recipient.Did, expiring.Payload, new PickupDeliveryOptions(ExpiresTime: start.ToUnixTimeSeconds() + 10)))
            .Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await mediator.Queue.DeliverAsync(recipient.Did, expiring.Payload, new PickupDeliveryOptions(ExpiresTime: start.ToUnixTimeSeconds() - 1)))
            .Outcome.Should().Be(PickupDeliveryOutcome.Expired, "a message that has already expired is not accepted");
        mediator.Store.MessageCount.Should().Be(2);

        // Now: only the one that is due is reported.
        var first = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        var status = (await mediator.AskAsync(recipient, first)).Status(first.Id);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(expiring.Payload.Length);

        // Its expiry passes while it waits (nobody asked for it): it is never delivered.
        mediator.Clock.Advance(TimeSpan.FromSeconds(11));
        var second = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, second)).Status(second.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));

        // The delayed one is delivered once its time has come, and not a second earlier.
        mediator.Clock.Advance(TimeSpan.FromSeconds(18));
        var early = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, early)).Status(early.Id).MessageCount.Should().Be(0);
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        var due = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivered = (await mediator.AskAsync(recipient, due)).Delivery(due.Id).Should().ContainSingle().Subject;
        await mediator.ShouldBeRelayedAsync(recipient, delivered, delayed with { Id = delayedResult.MessageId });
        mediator.Store.MessageCount.Should().Be(1, "the expired message is gone");
    }

    [Fact]
    public async Task A_recipient_did_filter_scopes_status_and_delivery_to_that_did_and_is_repeated_in_the_answer()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var second = (await mediator.Host.CreatePeersAsync(1))[0]; // a second DID of the same recipient agent
        await mediator.EnrolAsync(recipient, recipient.Did, second.Did);
        var a = await mediator.QueueAsync(recipient);
        var b = await mediator.QueueAsync(second);
        var c = await mediator.QueueAsync(recipient);

        var statusRequest = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator, second.Did);
        var status = (await mediator.AskAsync(recipient, statusRequest)).Status(statusRequest.Id);
        status.RecipientDid.Should().Be(second.Did, "a status MUST repeat the recipient_did of its request");
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(b.Payload.Length);

        var filtered = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10, second.Did);
        var only = (await mediator.AskAsync(recipient, filtered)).Delivery(filtered.Id, recipientDid: second.Did);
        await mediator.ShouldBeRelayedAsync(second, only.Should().ContainSingle().Subject, b);

        // A DID nobody registered: answered like an empty queue, the value repeated.
        const string unknown = "did:example:nobody-registered-this";
        var unknownRequest = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10, unknown);
        (await mediator.AskAsync(recipient, unknownRequest)).Status(unknownRequest.Id)
            .Should().Be(new PickupStatus(0, RecipientDid: unknown, TotalBytes: 0, LiveDelivery: false));

        var unfiltered = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, unfiltered)).Delivery(unfiltered.Id).Select(m => m.Id).Should().Equal(a.Id, c.Id);
    }

    // ---- status-request → status (FR-PICK-02) -----------------------------------------------

    [Fact]
    public async Task Status_request_reports_the_count_the_bytes_and_whole_second_times()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var start = mediator.Clock.Now.ToUnixTimeSeconds();

        // Arrivals at fractional seconds: what the status reports is whole seconds all the same.
        mediator.Clock.Advance(TimeSpan.FromMilliseconds(250));
        var first = await mediator.QueueAsync(recipient);
        mediator.Clock.Advance(TimeSpan.FromSeconds(10.5));
        var second = await mediator.QueueAsync(recipient);
        mediator.Clock.Advance(TimeSpan.FromSeconds(30.75)); // now = start + 41.5 s

        var request = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        var answer = await mediator.AskAsync(recipient, request);

        answer.Status(request.Id).Should().Be(new PickupStatus(
            MessageCount: 2,
            RecipientDid: null,
            LongestWaitedSeconds: 41,
            NewestReceivedTime: start + 10,
            OldestReceivedTime: start,
            TotalBytes: first.Payload.Length + second.Payload.Length,
            LiveDelivery: false));
        foreach (var member in new[] { "message_count", "longest_waited_seconds", "newest_received_time", "oldest_received_time", "total_bytes" })
        {
            var node = answer.Message.Body![member]!;
            node.GetValueKind().Should().Be(JsonValueKind.Number);
            long.TryParse(node.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                .Should().BeTrue("'{0}' is an integer on the wire, not a fraction or an exponent", member);
        }
        answer.Message.Body!["live_delivery"]!.GetValueKind().Should().Be(JsonValueKind.False);
        mediator.Store.MessageCount.Should().Be(2, "a status-request neither removes nor adds anything");
    }

    // ---- messages-received → status, leases (FR-PICK-04) ------------------------------------

    [Fact]
    public async Task Messages_received_removes_what_it_names_ignores_unknown_ids_and_is_answered_with_the_new_status()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var ids = (await mediator.AskAsync(recipient, fetch)).Delivery(fetch.Id).Select(m => m.Id).ToArray();
        ids.Should().Equal(queued.Select(q => q.Id));

        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { ids[0], "an-id-the-mediator-never-gave", ids[1] });
        var status = (await mediator.AskAsync(recipient, ack)).Status(ack.Id);

        status.MessageCount.Should().Be(1, "the count drops by what was acknowledged");
        status.TotalBytes.Should().Be(queued[2].Payload.Length);
        mediator.Store.MessageCount.Should().Be(1);

        // Only unknown ids: nothing removed, and the status is still sent.
        var unknown = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { "unknown-1", ids[0], "unknown-2" });
        (await mediator.AskAsync(recipient, unknown)).Status(unknown.Id).MessageCount.Should().Be(1);
        mediator.Store.MessageCount.Should().Be(1);

        var last = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { ids[2] });
        (await mediator.AskAsync(recipient, last)).Status(last.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_second_delivery_request_within_the_lease_gets_status_and_after_the_lease_the_same_messages_with_the_same_ids()
    {
        var lease = TimeSpan.FromSeconds(60); // the product default, stated so the arithmetic below reads
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.LeaseTimeout = lease);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var first = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, first)).Delivery(first.Id).Select(m => m.Id).Should().Equal(queued.Select(q => q.Id));

        // One second before the lease ends: a status, not a second delivery. Requests are stamped
        // from the clock the server's admission reads, so moving it keeps them fresh.
        mediator.Clock.Advance(lease - TimeSpan.FromSeconds(1));
        var within = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var status = (await mediator.AskAsync(recipient, within)).Status(within.Id);
        status.MessageCount.Should().Be(2, "delivered-but-unacknowledged messages are still held");

        // The lease ran out: the same records again — same ids, same bytes, no copy.
        mediator.Clock.Advance(TimeSpan.FromSeconds(1));
        var after = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var again = (await mediator.AskAsync(recipient, after)).Delivery(after.Id);
        again.Should().HaveCount(2);
        await mediator.ShouldBeRelayedAsync(recipient, again[0], queued[0]);
        await mediator.ShouldBeRelayedAsync(recipient, again[1], queued[1]);
        mediator.Store.MessageCount.Should().Be(2);
    }

    // ---- live-delivery-change over HTTP (FR-PICK-07) ----------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_delivery_change_true_over_http_is_answered_with_the_live_mode_not_supported_problem_report(bool requestHasThread)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var request = requestHasThread
            ? mediator.Raw(recipient, MessagePickup.LiveDeliveryChangeType, new JsonObject { ["live_delivery"] = true }, thid: "the-thread-of-the-change")
            : MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true);

        var response = await mediator.PostAsync(recipient, request);

        var answer = await mediator.AnswerAsync(recipient, response);
        answer.Message.Type.Should().Be("https://didcomm.org/report-problem/2.0/problem-report");
        answer.Message.Pthid.Should().Be(requestHasThread ? "the-thread-of-the-change" : request.Id, "pthid is the thread the problem occurred in: the request's thid, else its id");
        answer.Message.Body!["code"]!.GetValue<string>().Should().Be("e.m.live-mode-not-supported");
        answer.Message.Body!["comment"]!.GetValue<string>().Should().Be("Connection does not support Live Delivery");

        // The flag stayed off: the next status says so, and an arrival is queued.
        var statusRequest = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        (await mediator.AskAsync(recipient, statusRequest)).Status(statusRequest.Id).LiveDelivery.Should().BeFalse();
        await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
    }

    [Fact]
    public async Task Live_delivery_change_false_over_http_is_answered_with_status()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);

        var request = MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, false);

        (await mediator.AskAsync(recipient, request)).Status(request.Id)
            .Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
    }

    // ---- bytes (FR-PICK-03 / FR-PICK-10) ----------------------------------------------------

    [Fact]
    public async Task Five_300_KiB_payloads_under_the_default_budget_arrive_one_per_response_and_every_response_fits_one_mebibyte()
    {
        // Slow for this suite on purpose: 1.5 MiB of real ciphertext is packed, relayed and unpacked.
        await using var mediator = await PickupEndpointHost.StartAsync(); // max_delivery_bytes = MaxReceiveBytes = 1 MiB
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new List<RelayedMessage>();
        for (var i = 0; i < 5; i++)
            queued.Add(await mediator.QueueAsync(recipient, packedBytes: 300 * Kibibyte));
        queued.Should().OnlyContain(q => q.Payload.Length > 300 * Kibibyte - PickupEndpointHost.SizeSlack && q.Payload.Length <= 300 * Kibibyte);

        for (var i = 0; i < 5; i++)
        {
            var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
            var response = await mediator.PostAsync(recipient, request);
            response.Body.Length.Should().BeLessThanOrEqualTo(Mebibyte, "every packed response fits max_delivery_bytes");
            var delivered = (await mediator.AnswerAsync(recipient, response)).Delivery(request.Id);
            delivered.Should().ContainSingle("two 300 KiB payloads would not fit one mebibyte once packed");
            await mediator.ShouldBeRelayedAsync(recipient, delivered[0], queued[i]);

            var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { delivered[0].Id });
            var acked = await mediator.PostAsync(recipient, ack);
            acked.Body.Length.Should().BeLessThanOrEqualTo(Mebibyte);
            (await mediator.AnswerAsync(recipient, acked)).Status(ack.Id).MessageCount.Should().Be(4 - i);
        }

        var last = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, last)).Status(last.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_600_KiB_payload_is_refused_at_the_queue_and_a_500_KiB_payload_is_admitted_and_fetched_within_the_budget()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);

        var refused = await mediator.QueueAsync(recipient, packedBytes: 600 * Kibibyte, expected: PickupDeliveryOutcome.PayloadTooLarge);
        refused.Id.Should().BeNull();
        mediator.Store.MessageCount.Should().Be(0, "a payload whose single-item delivery would not fit the budget is not retained");
        mediator.Store.TotalBytes.Should().Be(0);

        var admitted = await mediator.QueueAsync(recipient, packedBytes: 500 * Kibibyte);
        var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var response = await mediator.PostAsync(recipient, request);

        response.Body.Length.Should().BeLessThanOrEqualTo(Mebibyte);
        var delivered = (await mediator.AnswerAsync(recipient, response)).Delivery(request.Id);
        await mediator.ShouldBeRelayedAsync(recipient, delivered.Should().ContainSingle().Subject, admitted);
    }

    [Fact]
    public async Task The_largest_admissible_payload_is_delivered_alone_within_the_budget_and_one_just_above_it_is_refused()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        max.Should().BeInRange(500 * Kibibyte, 600 * Kibibyte, "a little over half of the 1 MiB budget");

        var largest = await mediator.QueueAsync(recipient, packedBytes: max);
        var small = await mediator.QueueAsync(recipient);
        var above = await mediator.QueueAsync(recipient, packedBytes: max + 2 * PickupEndpointHost.SizeSlack, expected: PickupDeliveryOutcome.PayloadTooLarge);
        above.Payload.Length.Should().BeGreaterThan(max);
        mediator.Store.MessageCount.Should().Be(2);

        // Every admitted payload fits alone: the response carrying it is within the budget, and
        // the small message behind it waits for the next request rather than pushing it over.
        var first = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var response = await mediator.PostAsync(recipient, first);
        response.Body.Length.Should().BeLessThanOrEqualTo(Mebibyte);
        var delivered = (await mediator.AnswerAsync(recipient, response)).Delivery(first.Id);
        await mediator.ShouldBeRelayedAsync(recipient, delivered.Should().ContainSingle().Subject, largest);

        var second = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, second)).Delivery(second.Id).Should().ContainSingle().Which.Id.Should().Be(small.Id);
    }

    [Fact]
    public async Task Under_a_64_KiB_budget_messages_share_a_delivery_only_when_they_fit_together_and_every_response_is_within_the_budget()
    {
        const int budget = 64 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxDeliveryBytes = budget);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        max.Should().BeLessThan(budget);

        // Two that fit the batch together, then two that are each just over half of it.
        var fitting = new[] { await mediator.QueueAsync(recipient, packedBytes: max / 2), await mediator.QueueAsync(recipient, packedBytes: max / 2) };
        var over = new[]
        {
            await mediator.QueueAsync(recipient, packedBytes: max / 2 + 2 * PickupEndpointHost.SizeSlack),
            await mediator.QueueAsync(recipient, packedBytes: max / 2 + 2 * PickupEndpointHost.SizeSlack),
        };
        (over[0].Payload.Length + over[1].Payload.Length).Should().BeGreaterThan(max);
        await mediator.QueueAsync(recipient, packedBytes: max + 2 * PickupEndpointHost.SizeSlack, expected: PickupDeliveryOutcome.PayloadTooLarge);

        var expected = new[] { new[] { fitting[0], fitting[1] }, new[] { over[0] }, new[] { over[1] } };
        foreach (var batch in expected)
        {
            var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
            var response = await mediator.PostAsync(recipient, request);
            response.Body.Length.Should().BeLessThanOrEqualTo(budget, "whatever the batch, the packed response fits max_delivery_bytes");
            var delivered = (await mediator.AnswerAsync(recipient, response)).Delivery(request.Id);
            delivered.Should().HaveCount(batch.Length);
            for (var i = 0; i < batch.Length; i++)
                await mediator.ShouldBeRelayedAsync(recipient, delivered[i], batch[i]);

            var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, delivered.Select(m => m.Id).ToArray());
            var acked = await mediator.PostAsync(recipient, ack);
            acked.Body.Length.Should().BeLessThanOrEqualTo(budget);
            (await mediator.AnswerAsync(recipient, acked)).Status(ack.Id);
        }
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_the_estimate_let_through_is_trimmed_by_the_session_when_the_recipients_envelope_is_larger_than_allowed_for()
    {
        // The estimate that picks a batch sets 32 KiB aside for the envelope. A recipient that
        // publishes 38 key-agreement keys gets one recipients[] entry per key, each with a key id of
        // about 2 KiB: about 78 KiB of envelope. So for this recipient the estimate is short, and
        // what keeps the budget is the session measuring the packed bytes (TooLarge → one fewer).
        // A one-key recipient with the same payload sizes is the control: its pair fits.
        const int budget = 352 * Kibibyte;
        const int payload = 86 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxDeliveryBytes = budget);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        wide.Did.Length.Should().BeLessThanOrEqualTo(2048);
        var narrow = mediator.Recipient;
        await mediator.EnrolAsync(wide);
        await mediator.EnrolAsync(narrow);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        (2 * payload).Should().BeLessThanOrEqualTo(max, "the pair is within the batch byte budget, so the estimate lets it through");

        var control = new[] { await mediator.QueueAsync(narrow, packedBytes: payload), await mediator.QueueAsync(narrow, packedBytes: payload) };
        var controlRequest = MessagePickup.CreateDeliveryRequest(narrow.Did, mediator.Mediator, 10);
        var controlResponse = await mediator.PostAsync(narrow, controlRequest);
        controlResponse.Body.Length.Should().BeLessThanOrEqualTo(budget);
        (await mediator.AnswerAsync(narrow, controlResponse)).Delivery(controlRequest.Id).Select(m => m.Id)
            .Should().Equal(control.Select(c => c.Id), "for a one-key recipient the pair fits and travels together");

        var queued = new[] { await mediator.QueueAsync(wide, packedBytes: payload), await mediator.QueueAsync(wide, packedBytes: payload) };

        // The pair does not fit once packed for this recipient: one attachment, within the budget.
        var first = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var firstResponse = await mediator.PostAsync(wide, first);
        firstResponse.Body.Length.Should().BeLessThanOrEqualTo(budget, "the packed response is measured, not estimated");
        var firstDelivered = (await mediator.AnswerAsync(wide, firstResponse)).Delivery(first.Id);
        await mediator.ShouldBeRelayedAsync(wide, firstDelivered.Should().ContainSingle().Subject, queued[0]);

        // The message taken out of the batch lost its lease: it is delivered by the very next
        // request, with the clock where it was.
        var second = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var secondResponse = await mediator.PostAsync(wide, second);
        secondResponse.Body.Length.Should().BeLessThanOrEqualTo(budget);
        var secondDelivered = (await mediator.AnswerAsync(wide, secondResponse)).Delivery(second.Id);
        await mediator.ShouldBeRelayedAsync(wide, secondDelivered.Should().ContainSingle().Subject, queued[1]);

        var ack = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { queued[0].Id!, queued[1].Id! });
        (await mediator.AskAsync(wide, ack)).Status(ack.Id).MessageCount.Should().Be(0);

        // A single admitted message that this recipient's envelope cannot carry within the budget:
        // the request that meets it is answered with the status, within the budget, and nothing
        // is removed — the message stays queued and counted.
        var tooWide = await mediator.QueueAsync(wide, packedBytes: max);
        var third = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var thirdResponse = await mediator.PostAsync(wide, third);
        thirdResponse.Body.Length.Should().BeLessThanOrEqualTo(budget);
        var status = (await mediator.AnswerAsync(wide, thirdResponse)).Status(third.Id);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(tooWide.Payload.Length);
        mediator.Store.MessageCount.Should().Be(1 + control.Length, "nothing is dropped: the message that did not fit stays, and so does the control pair awaiting its acknowledgement");
    }

    [Fact]
    public async Task A_message_its_recipients_envelope_cannot_carry_is_passed_over_by_the_request_that_meets_it_which_delivers_what_is_behind_it()
    {
        // A recipient with 38 key-agreement keys (about 78 KiB of envelope against the 32 KiB the
        // estimate allows for) and a payload at the admission bound: admitted, yet its delivery is
        // over the budget even alone. Nothing is removed (a request framed otherwise, a larger
        // budget or a smaller envelope may still deliver it) and it is not given back either: it
        // keeps the lease of the request that met it, and that same request looks further and
        // delivers what is queued behind it. A status is the answer only when nothing deliverable
        // is left. After its lease the message is first in line again and is tried once more, by a
        // request that again goes on to what is behind it.
        const int budget = 352 * Kibibyte;
        const int small = 86 * Kibibyte;
        var lease = TimeSpan.FromSeconds(60);
        await using var mediator = await PickupEndpointHost.StartAsync(
            pickup: o =>
            {
                o.MaxDeliveryBytes = budget;
                o.LeaseTimeout = lease;
            },
            parkingStore: true);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        var narrow = mediator.Recipient;
        await mediator.EnrolAsync(wide);
        await mediator.EnrolAsync(narrow);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;

        // The control: a one-key recipient with the same two payload sizes gets both.
        var controlBig = await mediator.QueueAsync(narrow, packedBytes: max);
        var controlSmall = await mediator.QueueAsync(narrow, packedBytes: small);
        var control = await mediator.DrainAsync(narrow, request => mediator.AskAsync(narrow, request), budget, maxRequests: 2, acknowledge: true);
        control.SelectMany(step => step.Delivered).Select(m => m.Id).Should().Equal(controlBig.Id, controlSmall.Id);
        mediator.Store.MessageCount.Should().Be(0);

        var big = await mediator.QueueAsync(wide, packedBytes: max);
        var behind = await mediator.QueueAsync(wide, packedBytes: small);

        // One delivery-request of the many-key recipient: its answer, within the budget, and what
        // the store handed out in each lease round the request made.
        async Task<(PickupAnswer Answer, IReadOnlyList<IReadOnlyList<string>> Rounds)> FetchAsync()
        {
            var calls = mediator.Parking.Leased.Count;
            var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
            var response = await mediator.PostAsync(wide, request);
            response.Body.Length.Should().BeLessThanOrEqualTo(budget, "whatever is queued, the packed answer fits max_delivery_bytes");
            var answer = await mediator.AnswerAsync(wide, response);
            answer.Message.Thid.Should().Be(request.Id);
            return (answer, mediator.Parking.Leased.Skip(calls).ToList());
        }

        static (int Count, long? Bytes) Counted(PickupAnswer answer)
        {
            var status = answer.Status(answer.Message.Thid!);
            return (status.MessageCount, status.TotalBytes);
        }

        // First request: the big message is met alone and does not fit; the same request looks
        // further and delivers the message behind it. Nothing removed.
        var first = await FetchAsync();
        first.Rounds.Should().HaveCount(2, "one round that came to nothing, one that delivered");
        first.Rounds[0].Should().Equal(big.Id);
        first.Rounds[1].Should().Equal(behind.Id);
        var delivered = first.Answer.Delivery(first.Answer.Message.Thid).Should().ContainSingle().Subject;
        await mediator.ShouldBeRelayedAsync(wide, delivered, behind);
        mediator.Store.MessageCount.Should().Be(2);

        // Second request, the clock where it was: the big one is passed over, nothing else is
        // deliverable: one round, a status — and the big one is still counted.
        var second = await FetchAsync();
        second.Rounds.Should().ContainSingle().Which.Should().BeEmpty();
        Counted(second.Answer).Should().Be((2, big.Payload.Length + behind.Payload.Length), "the message that did not fit is still stored, due and unexpired: it is counted");
        var ack = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { delivered.Id });
        var afterAck = (await mediator.AskAsync(wide, ack)).Status(ack.Id);
        (afterAck.MessageCount, afterAck.TotalBytes).Should().Be((1, (long?)big.Payload.Length), "the recipient never learned the big one's id, so it cannot acknowledge it away");
        mediator.Store.MessageCount.Should().Be(1);
        mediator.Store.TotalBytes.Should().Be(big.Payload.Length);

        // One lease timeout later — the period it was passed over for the first time — with a new
        // message behind it: the big one is first in line again and is tried once more, by a
        // request that goes on to deliver what is behind it.
        var later = await mediator.QueueAsync(wide, packedBytes: small);
        mediator.Clock.Advance(lease);
        var third = await FetchAsync();
        third.Rounds.Should().HaveCount(2);
        third.Rounds[0].Should().Equal(new[] { big.Id }, "after its first period the message is at its original position and is tried again");
        third.Rounds[1].Should().Equal(later.Id);
        var deliveredLater = third.Answer.Delivery(third.Answer.Message.Thid).Should().ContainSingle().Subject;
        await mediator.ShouldBeRelayedAsync(wide, deliveredLater, later);
        var ackLater = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { deliveredLater.Id });
        (await mediator.AskAsync(wide, ackLater)).Status(ackLater.Id).MessageCount.Should().Be(1);

        // The second time it is passed over for twice as long: another lease timeout later it is
        // not even tried (one empty lease round, a status) …
        mediator.Clock.Advance(lease);
        var early = await FetchAsync();
        early.Rounds.Should().ContainSingle().Which.Should().BeEmpty();
        Counted(early.Answer).Should().Be((1, big.Payload.Length));

        // … and after the second lease timeout of that period it is: tried once more, then a status.
        mediator.Clock.Advance(lease);
        var fourth = await FetchAsync();
        fourth.Rounds.Should().HaveCount(2);
        fourth.Rounds[0].Should().Equal(big.Id);
        fourth.Rounds[1].Should().BeEmpty();
        Counted(fourth.Answer).Should().Be((1, big.Payload.Length));

        // Through all of it the mediator never removed the big message. It set it aside three
        // times, each time for twice as long, under no lease and no connection.
        mediator.Parking.Acknowledged.Should().NotContain(big.Id);
        mediator.Parking.GivenBack.Should().NotContain(big.Id);
        mediator.Parking.PassedOver.Select(p => p.Id).Should().Equal(big.Id, big.Id, big.Id);
        var untils = mediator.Parking.PassedOver.Select(p => p.Until).ToArray();
        (untils[1] - untils[0]).Should().Be(lease * 2, "tried again one lease timeout after the first time, then set aside for two");
        (untils[2] - untils[1]).Should().Be(lease * 4, "tried again two lease timeouts after the second time, then set aside for four");
        mediator.Store.MessageCount.Should().Be(1);

        // It is logged by the pickup classes — once at Warning, then quietly — and the lines name
        // no one and no message.
        var sensitive = wide.Kids.Append(wide.Did).Append(mediator.Mediator).Append(big.Id!).Append(behind.Id!).Append(later.Id!).ToArray();
        mediator.Logs.OfPickup.Where(r => r.Level >= LogLevel.Warning).Should().ContainSingle("the first message that does not fit is what an operator needs to see");
        mediator.Logs.OfPickup.Where(r => sensitive.Any(v => r.Rendered.Contains(v, StringComparison.Ordinal))).Select(r => r.Rendered).Should().BeEmpty();

        // What clears it is the host: a purge of the recipient DID's messages.
        (await mediator.Store.PurgeRecipientAsync(wide.Did, wide.Did, default)).Should().Be(1);
        var last = MessagePickup.CreateStatusRequest(wide.Did, mediator.Mediator);
        (await mediator.AskAsync(wide, last)).Status(last.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_recipient_that_polls_once_per_lease_period_or_slower_gets_what_is_queued_behind_a_message_that_cannot_be_carried_in_that_same_poll()
    {
        // The recipient asks once per poll and stops at whatever comes back, as the protocol page
        // describes; its polls are 61 seconds apart, the lease is 60. The message that does not fit
        // is set aside for 60 s the first time and 120 s the second, so the first two polls find it
        // first in line again and the third does not. Every poll brings what arrived behind it, in
        // that one request; a status means nothing deliverable is left.
        const int budget = 352 * Kibibyte;
        const int small = 86 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(
            pickup: o =>
            {
                o.MaxDeliveryBytes = budget;
                o.LeaseTimeout = TimeSpan.FromSeconds(60);
            },
            parkingStore: true);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var big = await mediator.QueueAsync(wide, packedBytes: mediator.Queue.MaxAdmissiblePayloadBytes);

        for (var poll = 0; poll < 3; poll++)
        {
            var fresh = await mediator.QueueAsync(wide, packedBytes: small);
            var calls = mediator.Parking.Leased.Count;

            var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
            var response = await mediator.PostAsync(wide, request);

            response.Body.Length.Should().BeLessThanOrEqualTo(budget);
            var delivered = (await mediator.AnswerAsync(wide, response)).Delivery(request.Id).Should().ContainSingle().Subject;
            await mediator.ShouldBeRelayedAsync(wide, delivered, fresh);
            var rounds = mediator.Parking.Leased.Skip(calls).Select(round => round.Single()).ToArray();
            if (poll < 2)
                rounds.Should().Equal(new[] { big.Id, fresh.Id }, "the message that does not fit is tried once, then the one behind it is delivered");
            else
                rounds.Should().Equal(new[] { fresh.Id }, "the period the message is passed over for has outgrown the polling interval: it costs this poll nothing");
            var ack = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { delivered.Id });
            (await mediator.AskAsync(wide, ack)).Status(ack.Id).MessageCount.Should().Be(1, "only the message that does not fit is left, and it is counted");

            mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        // A poll with nothing new behind it: a status, and the recipient rightly stops.
        var idle = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var status = (await mediator.AskAsync(wide, idle)).Status(idle.Id);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(big.Payload.Length);
        mediator.Parking.Acknowledged.Should().NotContain(big.Id);
        mediator.Store.MessageCount.Should().Be(1);
        (await mediator.Store.PurgeGrantAsync(wide.Did, default)).Should().Be(1);
    }

    [Fact]
    public async Task A_message_that_cannot_be_carried_counts_against_the_grants_quota_until_it_expires()
    {
        // The message that does not fit is retained like any other: it counts against the grant's
        // quota while it is passed over, and it leaves by its own expiry.
        const int budget = 352 * Kibibyte;
        const int small = 86 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(o =>
        {
            o.MaxDeliveryBytes = budget;
            o.MaxQueueMessages = 2;
        });
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        var expires = mediator.Clock.Now.ToUnixTimeSeconds() + 300;

        var big = await mediator.PackFromAliceAsync(wide, packedBytes: max);
        (await mediator.Queue.DeliverAsync(wide.Did, big.Payload, new PickupDeliveryOptions(ExpiresTime: expires))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        var met = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(wide, met)).Status(met.Id).MessageCount.Should().Be(1, "it does not fit even alone: a status, and it stays");

        // Passed over, and still one of the two messages the grant may hold.
        var behind = await mediator.QueueAsync(wide, packedBytes: small);
        await mediator.QueueAsync(wide, packedBytes: small, expected: PickupDeliveryOutcome.QueueFull);
        var fetch = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(wide, fetch)).Delivery(fetch.Id).Should().ContainSingle().Which.Id.Should().Be(behind.Id);
        var ack = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { behind.Id! });
        (await mediator.AskAsync(wide, ack)).Status(ack.Id).MessageCount.Should().Be(1);
        mediator.Store.MessageCount.Should().Be(1);

        // Its expiry passes: it is no longer counted, and its place in the quota is free again.
        mediator.Clock.Advance(TimeSpan.FromSeconds(301));
        var afterExpiry = MessagePickup.CreateStatusRequest(wide.Did, mediator.Mediator);
        (await mediator.AskAsync(wide, afterExpiry)).Status(afterExpiry.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
        await mediator.QueueAsync(wide, packedBytes: small);
        await mediator.QueueAsync(wide, packedBytes: small);
        mediator.Store.MessageCount.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queue_of_small_big_small_for_a_many_key_recipient_delivers_both_small_ones_once_in_two_requests_and_keeps_the_big_one(bool bigOneJoinsTheFirstBatch)
    {
        // false: every payload is for the recipient's own 38-key DID, so even a small one is 86 KiB
        //        and the batch estimate never puts the big one beside it; the big one is met alone.
        // true:  the payloads are for a one-key DID the recipient registered, so the small ones are
        //        about 1 KiB and the estimate takes all three for the first request; the session
        //        refuses the batch of three and the batch of two, and the first message goes alone.
        // Either way: the first request delivers the first small message; the second meets the big
        // one, which does not fit even alone and keeps that request's lease, looks further and
        // delivers the other small one. Requests needed: one per delivery — a message that does
        // not fit costs none — with the clock where it was.
        const int budget = 352 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxDeliveryBytes = budget);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        TestPeer addressee;
        int? smallSize;
        int bigSize;
        if (bigOneJoinsTheFirstBatch)
        {
            addressee = (await mediator.Host.CreatePeersAsync(1))[0];
            await mediator.EnrolAsync(wide, wide.Did, addressee.Did);
            smallSize = null;
            bigSize = max - 4 * Kibibyte;
        }
        else
        {
            addressee = wide;
            await mediator.EnrolAsync(wide);
            smallSize = 86 * Kibibyte;
            bigSize = max;
        }

        var first = await mediator.QueueAsync(addressee, packedBytes: smallSize);
        var big = await mediator.QueueAsync(addressee, packedBytes: bigSize);
        var last = await mediator.QueueAsync(addressee, packedBytes: smallSize);
        var all = first.Payload.Length + big.Payload.Length + last.Payload.Length;
        if (bigOneJoinsTheFirstBatch)
            all.Should().BeLessThanOrEqualTo(max, "all three are within the batch byte budget, so the estimate takes them together");
        else
            (first.Payload.Length + big.Payload.Length).Should().BeGreaterThan(max, "the estimate never puts the big one in a batch with another");

        var steps = await mediator.DrainAsync(wide, request => mediator.AskAsync(wide, request), budget, maxRequests: 2, acknowledge: true, left: 1);

        var delivered = steps.SelectMany(step => step.Delivered).ToList();
        delivered.Select(m => m.Id).Should().Equal(new[] { first.Id, last.Id }, "both small messages arrive, each once and in order; the big one never does");
        await mediator.ShouldBeRelayedAsync(addressee, delivered[0], first);
        await mediator.ShouldBeRelayedAsync(addressee, delivered[1], last);
        steps.Count.Should().BeLessThanOrEqualTo(2, "one request per delivery; the message that does not fit costs none");
        steps[^1].Status!.MessageCount.Should().Be(1, "the big one is still queued, and counted");
        steps[^1].Status!.TotalBytes.Should().Be(big.Payload.Length);
        mediator.Store.MessageCount.Should().Be(1, "the small ones were acknowledged; the big one was not removed");
        mediator.Store.TotalBytes.Should().Be(big.Payload.Length);

        // What clears it is the host: a purge of the grant's messages.
        (await mediator.Store.PurgeGrantAsync(wide.Did, default)).Should().Be(1);
        var after = MessagePickup.CreateStatusRequest(wide.Did, mediator.Mediator);
        (await mediator.AskAsync(wide, after)).Status(after.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
    }

    [Fact]
    public async Task A_message_too_large_for_a_request_on_a_long_thread_id_is_kept_and_delivered_by_an_ordinary_request_once_that_requests_lease_has_ended()
    {
        // "Does not fit even alone" is judged on the delivery built for the request at hand, and
        // that delivery repeats the request's thread id — up to 512 characters the recipient
        // chooses (a recipient_did filter is repeated too). For a recipient whose envelope is
        // larger than the allowance (38 keys here) a payload can sit so close to the budget that
        // its delivery fits on an ordinary thread and not on a long one. So the verdict of one
        // request says nothing final about the message: it is never removed. It keeps the lease of
        // the request that could not carry it, and when that lease has ended a request that can
        // carry it delivers it.
        const int budget = 352 * Kibibyte;
        var lease = TimeSpan.FromSeconds(60);
        await using var mediator = await PickupEndpointHost.StartAsync(
            pickup: o =>
            {
                o.MaxDeliveryBytes = budget;
                o.LeaseTimeout = lease;
            },
            parkingStore: true);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);

        // Measure this recipient: one known payload and the size of its packed delivery.
        var known = await mediator.QueueAsync(wide, packedBytes: 86 * Kibibyte);
        var measure = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var measured = await mediator.PostAsync(wide, measure);
        (await mediator.AnswerAsync(wide, measured)).Delivery(measure.Id).Should().ContainSingle();
        var ackKnown = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { known.Id! });
        (await mediator.AskAsync(wide, ackKnown)).Status(ackKnown.Id).MessageCount.Should().Be(0);

        // A payload whose delivery on an ordinary thread comes out about 320 bytes under the budget
        // (a packed delivery grows by 16/9 of its payload). A 512-character thread instead of a
        // 36-character one adds at least 600 bytes to it.
        var size = known.Payload.Length + (int)((budget - 320L - measured.Body.Length) * 9 / 16);

        // On an ordinary thread a message of that size is delivered, within the budget.
        var plain = await mediator.QueueAsync(wide, packedBytes: size);
        var ordinary = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var ordinaryResponse = await mediator.PostAsync(wide, ordinary);
        ordinaryResponse.Body.Length.Should().BeInRange(budget - 599, budget, "the delivery fits, and by less than a long thread id adds");
        (await mediator.AnswerAsync(wide, ordinaryResponse)).Delivery(ordinary.Id).Should().ContainSingle().Which.Id.Should().Be(plain.Id);
        var ackPlain = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { plain.Id! });
        (await mediator.AskAsync(wide, ackPlain)).Status(ackPlain.Id).MessageCount.Should().Be(0);

        // The same size again, asked for on a 512-character thread: too large for this request.
        var again = await mediator.QueueAsync(wide, packedBytes: size);
        var longThread = new string('t', 512);
        var heavy = mediator.Raw(wide, MessagePickup.DeliveryRequestType, new JsonObject { ["limit"] = 10 }, thid: longThread);
        var heavyResponse = await mediator.PostAsync(wide, heavy);
        heavyResponse.Body.Length.Should().BeLessThanOrEqualTo(budget);
        var status = (await mediator.AnswerAsync(wide, heavyResponse)).Status(longThread);
        status.MessageCount.Should().Be(1, "an ordinary request delivers a message of this very size, so it is not one that can never be delivered");
        status.TotalBytes.Should().Be(again.Payload.Length);
        mediator.Store.MessageCount.Should().Be(1);

        // It keeps that request's lease: an ordinary request inside it is not given the message.
        var within = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(wide, within)).Status(within.Id).MessageCount.Should().Be(1);

        // Once that lease has ended, an ordinary request delivers it, within the budget.
        mediator.Clock.Advance(lease);
        var after = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var afterResponse = await mediator.PostAsync(wide, after);
        afterResponse.Body.Length.Should().BeLessThanOrEqualTo(budget);
        await mediator.ShouldBeRelayedAsync(wide, (await mediator.AnswerAsync(wide, afterResponse)).Delivery(after.Id).Should().ContainSingle().Subject, again);

        // Nothing was ever removed by the mediator on its own: the only ids it asked the store to
        // remove are the ones the recipient acknowledged.
        mediator.Parking.Acknowledged.Should().BeEquivalentTo(new[] { known.Id, plain.Id });
        mediator.Parking.GivenBack.Should().NotContain(again.Id, "a message that did not fit alone is not given back: it keeps the lease");
        var ackAgain = MessagePickup.CreateMessagesReceived(wide.Did, mediator.Mediator, new[] { again.Id! });
        (await mediator.AskAsync(wide, ackAgain)).Status(ackAgain.Id).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
    }

    // ---- no session, no authcrypt (FR-PICK-01, JC-9) ----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(ReturnRouteMode.None)]
    [InlineData(ReturnRouteMode.Thread)]
    public async Task A_delivery_request_without_return_route_all_gets_202_with_no_body_and_leases_nothing(ReturnRouteMode? header)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };

        var request = mediator.Prepare(MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10), returnRoute: false);
        if (header is { } mode)
            ReturnRouteHeaderWriter.Write(request, mode, mode == ReturnRouteMode.Thread ? "some-thread" : null);
        var response = await mediator.Host.PostAsync(await mediator.Host.AuthcryptAsync(recipient, request));

        response.Status.Should().Be(HttpStatusCode.Accepted, "there is no session to carry a batch on");
        response.Body.Should().BeEmpty();
        mediator.Store.MessageCount.Should().Be(3);

        // Nothing was leased: a proper request gets everything at once.
        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id).Select(m => m.Id).Should().Equal(queued.Select(q => q.Id));
    }

    [Fact]
    public async Task Without_return_route_all_a_status_is_neither_sent_nor_queued_live_mode_is_not_reported_and_an_acknowledgement_still_applies()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };

        var status = await mediator.PostAsync(recipient, MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator), returnRoute: false);
        status.Status.Should().Be(HttpStatusCode.Accepted);
        status.Body.Should().BeEmpty();
        mediator.Store.MessageCount.Should().Be(2, "a status is only ever sent to a connected recipient; it is never put on the queue");

        var live = await mediator.PostAsync(recipient, MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true), returnRoute: false);
        live.Status.Should().Be(HttpStatusCode.Accepted);
        live.Body.Should().BeEmpty();
        mediator.Store.MessageCount.Should().Be(2);

        var ack = await mediator.PostAsync(recipient, MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued[0].Id! }), returnRoute: false);
        ack.Status.Should().Be(HttpStatusCode.Accepted);
        ack.Body.Should().BeEmpty();
        mediator.Store.MessageCount.Should().Be(1, "the acknowledgement of an authenticated peer is applied even when its status cannot be returned");

        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id).Should().ContainSingle().Which.Id.Should().Be(queued[1].Id);
    }

    [Theory]
    [InlineData(EnvelopeShape.Anoncrypt)]
    [InlineData(EnvelopeShape.AnoncryptOfSigned)]
    [InlineData(EnvelopeShape.SignedOnly)]
    [InlineData(EnvelopeShape.Plaintext)]
    public async Task A_request_without_an_authcrypt_layer_gets_202_with_no_body_is_logged_and_changes_nothing(EnvelopeShape shape)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var ids = queued.Select(q => q.Id!).ToArray();
        mediator.Logs.OfPickup.Where(r => r.Level >= LogLevel.Warning).Should().BeEmpty();

        // Each names the recipient as its plaintext `from`; none carries an authcrypt layer.
        var requests = new[]
        {
            MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator),
            MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, ids),
            MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10),
            MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true),
        };
        foreach (var request in requests)
        {
            var (packed, contentType) = await mediator.Host.PackAsync(recipient, mediator.Prepare(request), shape);
            var response = await mediator.Host.PostAsync(packed, contentType: contentType);
            response.Status.Should().Be(HttpStatusCode.Accepted, "a request without an authcrypt layer is not answered");
            response.Body.Should().BeEmpty();
        }

        mediator.Store.MessageCount.Should().Be(2, "such a messages-received removes nothing");
        mediator.Logs.OfPickup.Where(r => r.Level >= LogLevel.Warning).Should().NotBeEmpty("the drop is logged");

        // Nothing was leased either: the authenticated recipient gets both at once.
        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id).Select(m => m.Id).Should().Equal(ids);
    }

    // ---- another peer (FR-PICK-02/04) -------------------------------------------------------

    [Fact]
    public async Task Another_enrolled_peer_naming_the_recipients_did_or_acknowledging_its_ids_sees_an_empty_queue_and_changes_nothing()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var mallory = mediator.Mallory;
        await mediator.EnrolAsync(recipient);
        await mediator.EnrolAsync(mallory);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var ids = queued.Select(q => q.Id!).ToArray();
        var empty = new PickupStatus(0, RecipientDid: recipient.Did, TotalBytes: 0, LiveDelivery: false);

        var status = MessagePickup.CreateStatusRequest(mallory.Did, mediator.Mediator, recipient.Did);
        (await mediator.AskAsync(mallory, status)).Status(status.Id).Should().Be(empty, "one grant cannot see another grant's queue");

        var filtered = MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Mediator, 10, recipient.Did);
        (await mediator.AskAsync(mallory, filtered)).Status(filtered.Id).Should().Be(empty);

        var unfiltered = MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(mallory, unfiltered)).Status(unfiltered.Id).Should().Be(empty with { RecipientDid = null });

        var ack = MessagePickup.CreateMessagesReceived(mallory.Did, mediator.Mediator, ids);
        (await mediator.AskAsync(mallory, ack)).Status(ack.Id).Should().Be(empty with { RecipientDid = null }, "ids of another grant are plain misses");
        mediator.Store.MessageCount.Should().Be(3, "the recipient's queue is intact");

        // A forged identity: Mallory authcrypts requests whose plaintext `from` is the recipient.
        // The peer is the authcrypt sender, never `from`, and the envelope layer refuses the
        // disagreement before anything is dispatched.
        foreach (var forged in new[]
                 {
                     MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, ids),
                     MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10),
                 })
        {
            var refused = await mediator.Host.PostAsync(await mediator.Host.AuthcryptAsync(mallory, mediator.Prepare(forged)));
            refused.Status.Should().Be(HttpStatusCode.BadRequest);
            refused.Body.Should().BeEmpty();
        }
        mediator.Store.MessageCount.Should().Be(3);

        // …and unleased: the recipient fetches it in full.
        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivered = (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id);
        delivered.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
            await mediator.ShouldBeRelayedAsync(recipient, delivered[i], queued[i]);
    }

    [Fact]
    public async Task A_peer_without_a_grant_and_a_peer_whose_grant_was_revoked_are_answered_like_an_empty_queue()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var stranger = mediator.Mallory; // authcrypts like anyone, never enrolled
        await mediator.EnrolAsync(recipient);
        await mediator.QueueAsync(recipient);
        await mediator.QueueAsync(recipient);
        var empty = new PickupStatus(0, TotalBytes: 0, LiveDelivery: false);

        async Task ShouldSeeNothingAsync(TestPeer peer)
        {
            var status = MessagePickup.CreateStatusRequest(peer.Did, mediator.Mediator);
            (await mediator.AskAsync(peer, status)).Status(status.Id).Should().Be(empty);
            var fetch = MessagePickup.CreateDeliveryRequest(peer.Did, mediator.Mediator, 10);
            (await mediator.AskAsync(peer, fetch)).Status(fetch.Id).Should().Be(empty);
            var filtered = MessagePickup.CreateDeliveryRequest(peer.Did, mediator.Mediator, 10, recipient.Did);
            (await mediator.AskAsync(peer, filtered)).Status(filtered.Id).Should().Be(empty with { RecipientDid = recipient.Did });
        }

        await ShouldSeeNothingAsync(stranger);
        mediator.Store.MessageCount.Should().Be(2);

        // The host revokes the recipient's grant: what is still stored for it is not delivered…
        (await mediator.Mediation.RevokeAsync(recipient.Did, default)).Should().BeTrue();
        await ShouldSeeNothingAsync(recipient);
        // …nothing more is accepted for its DID, and removing the leftovers is the host's purge.
        await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.NotRegistered);
        mediator.Store.MessageCount.Should().Be(2);
        (await mediator.Store.PurgeGrantAsync(recipient.Did, default)).Should().Be(2);
        mediator.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Messages_held_for_a_did_the_recipient_has_since_removed_are_not_delivered_and_are_dropped()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var second = (await mediator.Host.CreatePeersAsync(1))[0];
        await mediator.EnrolAsync(recipient, recipient.Did, second.Did);
        var a = await mediator.QueueAsync(recipient);
        await mediator.QueueAsync(second);
        var c = await mediator.QueueAsync(recipient);

        // The recipient removes its second DID with a real recipient-update.
        var remove = CoordinateMediation.CreateRecipientUpdate(recipient.Did, mediator.Mediator, new[] { new RecipientUpdate(second.Did, RecipientUpdateAction.Remove) });
        CoordinateMediation.TryParseRecipientUpdateResponse((await mediator.AskAsync(recipient, remove)).Message, out var removed).Should().BeTrue();
        removed!.Should().OnlyContain(r => r.Result == RecipientUpdateOutcome.Success);

        // Registration is checked when messages are delivered: only what is still registered travels.
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, fetch)).Delivery(fetch.Id).Select(m => m.Id).Should().Equal(a.Id, c.Id);
        mediator.Store.MessageCount.Should().Be(2, "what was held for the removed DID can never be delivered and is removed");

        // Naming the removed DID is answered like any DID the peer has not registered.
        var filtered = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10, second.Did);
        (await mediator.AskAsync(recipient, filtered)).Status(filtered.Id)
            .Should().Be(new PickupStatus(0, RecipientDid: second.Did, TotalBytes: 0, LiveDelivery: false));
        await mediator.QueueAsync(second, expected: PickupDeliveryOutcome.NotRegistered);
    }

    // ---- replay admission and concurrency ---------------------------------------------------

    [Fact]
    public async Task A_replayed_request_and_a_request_without_a_fresh_created_time_get_202_and_neither_lease_nor_remove_anything()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var ids = queued.Select(q => q.Id!).ToArray();

        // The first delivery-request takes one message; the same bytes again are a replay.
        var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 1);
        var packed = await mediator.Host.AuthcryptAsync(recipient, mediator.Prepare(request));
        (await mediator.AnswerAsync(recipient, await mediator.Host.PostAsync(packed))).Delivery(request.Id).Should().ContainSingle().Which.Id.Should().Be(ids[0]);
        var replay = await mediator.Host.PostAsync(packed);
        replay.Status.Should().Be(HttpStatusCode.Accepted, "a duplicate (peer, id) is not dispatched (FR-RR-08)");
        replay.Body.Should().BeEmpty();

        // An acknowledgement that is not stamped, and one stamped an hour ago: not fresh.
        var unstamped = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, ids);
        ReturnRouteHeaderWriter.Write(unstamped, ReturnRouteMode.All, thread: null);
        var stale = mediator.Prepare(MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, ids));
        stale.CreatedTime -= 3600;
        foreach (var refused in new[] { unstamped, stale })
        {
            var response = await mediator.Host.PostAsync(await mediator.Host.AuthcryptAsync(recipient, refused));
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
        }
        mediator.Store.MessageCount.Should().Be(3, "a refused acknowledgement removes nothing");

        // The replay leased nothing: the next request gets exactly the two the first did not take.
        var next = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, next)).Delivery(next.Id).Select(m => m.Id).Should().Equal(ids[1], ids[2]);
    }

    [Fact]
    public async Task Concurrent_delivery_requests_hand_out_each_message_exactly_once()
    {
        const int count = 8;
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new List<RelayedMessage>();
        for (var i = 0; i < count; i++)
            queued.Add(await mediator.QueueAsync(recipient));

        // Packed first, so the POSTs overlap as far as the server lets them. Which request gets
        // which message is not asserted: only that each gets one and no message goes out twice.
        var requests = Enumerable.Range(0, count).Select(_ => MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 1)).ToArray();
        var packed = new string[count];
        for (var i = 0; i < count; i++)
            packed[i] = await mediator.Host.AuthcryptAsync(recipient, mediator.Prepare(requests[i]));
        var responses = await Task.WhenAll(packed.Select(p => mediator.Host.PostAsync(p)));

        var ids = new List<string>();
        for (var i = 0; i < count; i++)
            ids.Add((await mediator.AnswerAsync(recipient, responses[i])).Delivery(requests[i].Id).Should().ContainSingle().Subject.Id);
        ids.Should().BeEquivalentTo(queued.Select(q => q.Id), "every request took one message and no two took the same one");
        mediator.Store.MessageCount.Should().Be(count);
    }

    // ---- malformed (FR-PICK-03) and logs ----------------------------------------------------

    public static TheoryData<string, string> MalformedBodies => new()
    {
        { MessagePickup.DeliveryRequestType, """{"limit": -1}""" },
        { MessagePickup.DeliveryRequestType, """{"limit": "10"}""" },
        { MessagePickup.DeliveryRequestType, """{"limit": 1.5}""" },
        { MessagePickup.DeliveryRequestType, """{}""" },
        { MessagePickup.DeliveryRequestType, """{"limit": 10, "recipient_did": 7}""" },
        { MessagePickup.StatusRequestType, """{"recipient_did": ""}""" },
        { MessagePickup.MessagesReceivedType, """{}""" },
        { MessagePickup.MessagesReceivedType, """{"message_id_list": "not-a-list"}""" },
        { MessagePickup.LiveDeliveryChangeType, """{"live_delivery": "true"}""" },
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_malformed_request_gets_the_uniform_400_with_no_body_and_touches_nothing(string type, string body)
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };

        var response = await mediator.PostAsync(recipient, mediator.Raw(recipient, type, JsonNode.Parse(body)!.AsObject()));

        response.Status.Should().Be(HttpStatusCode.BadRequest, "a malformed message is the endpoint's uniform rejection");
        response.Body.Should().BeEmpty();
        mediator.Store.MessageCount.Should().Be(2);
        var proper = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, proper)).Delivery(proper.Id).Select(m => m.Id).Should().Equal(queued.Select(q => q.Id), "nothing was leased or removed");
    }

    [Fact]
    public async Task No_log_line_and_no_logged_exception_carries_a_did_a_recipient_did_an_attachment_id_or_a_thread_id()
    {
        const string thread = "SENTINEL-thread-0123456789";
        const string unregistered = "did:example:SENTINEL-nobody-registered";
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var mallory = mediator.Mallory;
        var second = (await mediator.Host.CreatePeersAsync(1))[0];
        await mediator.EnrolAsync(recipient, recipient.Did, second.Did);
        await mediator.EnrolAsync(mallory);
        var a = await mediator.QueueAsync(recipient);
        var b = await mediator.QueueAsync(second);

        JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();
        JsonObject Filter(string did, int? limit = null)
        {
            var body = new JsonObject { ["recipient_did"] = did };
            if (limit is { } l)
                body["limit"] = l;
            return body;
        }

        // The answered paths, on a thread of the recipient's own choosing.
        (await mediator.AskAsync(recipient, mediator.Raw(recipient, MessagePickup.StatusRequestType, Filter(second.Did), thread))).Status(thread);
        (await mediator.AskAsync(recipient, mediator.Raw(recipient, MessagePickup.StatusRequestType, Filter(unregistered), thread))).Status(thread);
        (await mediator.AskAsync(recipient, mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Filter(unregistered, 10), thread))).Status(thread);
        var ids = (await mediator.AskAsync(recipient, mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Body("""{"limit": 10}"""), thread)))
            .Delivery(thread).Select(m => m.Id).ToArray();
        ids.Should().Equal(a.Id, b.Id);
        (await mediator.AskAsync(recipient, mediator.Raw(recipient, MessagePickup.LiveDeliveryChangeType, Body("""{"live_delivery": true}"""), thread))).Problem(thread);

        // Another peer probing the recipient's DID and ids.
        (await mediator.AskAsync(mallory, mediator.Raw(mallory, MessagePickup.DeliveryRequestType, Filter(recipient.Did, 10), thread))).Status(thread);
        (await mediator.AskAsync(mallory, mediator.Raw(mallory, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = new JsonArray(ids[0], ids[1], "SENTINEL-unknown-id") }, thread))).Status(thread);

        // Not answered: no session; no authcrypt layer.
        (await mediator.PostAsync(recipient, mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Filter(second.Did, 10), thread), returnRoute: false))
            .Status.Should().Be(HttpStatusCode.Accepted);
        (await mediator.Host.PostAsync(await mediator.Host.AnoncryptAsync(mediator.Prepare(mediator.Raw(recipient, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = new JsonArray(ids[0], ids[1]) }, thread)))))
            .Status.Should().Be(HttpStatusCode.Accepted);

        // Not queued: unregistered, and too large.
        (await mediator.Queue.DeliverAsync(unregistered, a.Payload)).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await mediator.Queue.DeliverAsync(recipient.Did, new byte[mediator.Queue.MaxAdmissiblePayloadBytes + 1])).Outcome.Should().Be(PickupDeliveryOutcome.PayloadTooLarge);

        // Malformed: each is the endpoint's uniform 400, and the endpoint logs the exception's type.
        var malformed = new[]
        {
            mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Body($$"""{"limit": -1, "recipient_did": "{{unregistered}}"}"""), thread),
            mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Body($$"""{"limit": "SENTINEL-limit", "recipient_did": "{{recipient.Did}}"}"""), thread),
            mediator.Raw(recipient, MessagePickup.DeliveryRequestType, Body("""{"limit": 3, "recipient_did": ["SENTINEL-not-a-string"]}"""), thread),
            mediator.Raw(recipient, MessagePickup.StatusRequestType, new JsonObject { ["recipient_did"] = "did:example:SENTINEL-" + new string('x', 2048) }, thread),
            mediator.Raw(recipient, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = new JsonArray(ids[0], 7, "SENTINEL-id") }, thread),
            mediator.Raw(recipient, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = "SENTINEL-not-a-list" }, thread),
            mediator.Raw(recipient, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode)("SENTINEL-" + i)).ToArray()) }, thread),
            mediator.Raw(recipient, MessagePickup.LiveDeliveryChangeType, Body("""{"live_delivery": "SENTINEL-yes"}"""), thread),
            mediator.Raw(recipient, MessagePickup.StatusRequestType, new JsonObject(), "SENTINEL-" + new string('t', 512)),
        };
        foreach (var request in malformed)
        {
            var response = await mediator.PostAsync(recipient, request);
            response.Status.Should().Be(HttpStatusCode.BadRequest);
            response.Body.Should().BeEmpty();
        }
        mediator.Store.MessageCount.Should().Be(2, "none of this removed anything");

        var sensitive = mediator.Host.SensitiveValues
            .Concat(second.Kids).Append(second.Did)
            .Concat(ids)
            .Append("SENTINEL")
            .ToArray();
        bool Leaks(string text) => sensitive.Any(v => text.Contains(v, StringComparison.Ordinal));

        // (1) The rule under test: the pickup classes' own lines, at every level.
        mediator.Logs.OfPickup.Should().NotBeEmpty("the scenario made the pickup classes log (the drops and the refusals above)");
        mediator.Logs.OfPickup.Where(r => Leaks(r.Rendered)).Select(r => r.Rendered).Should().BeEmpty(
            "pickup log lines carry the id of the request, type names and counts — never a DID, a recipient_did, an attachment id or a thread id");

        // (2) …and what they throw: the endpoint writes the exception's type name, never the object
        //     (#95 AD-5; it used to log the object, whose text a stranger can shape).
        mediator.Logs.Records.Where(r => r.Exception is not null).Should().BeEmpty("no line carries an exception object");
        mediator.Logs.Records.Count(r => r.Text.Contains("rejecting inbound envelope (MalformedMessageException", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(malformed.Length, "each malformed request was logged by its type");

        // (3) The rest of the host (endpoint, dispatcher, client): pre-existing code, whose lines at
        //     Information or above are held to the same rule here. They are listed for the report.
        var others = mediator.Logs.OfOthers.Where(r => r.Level >= LogLevel.Information && Leaks(r.Rendered)).ToList();
        foreach (var record in others)
            _output.WriteLine($"[{record.Level}] {record.Category}: {record.Rendered}");
        others.Select(r => $"{r.Category}: {r.Text}").Should().BeEmpty("no line of the host at Information or above carries one of these values either");
    }
}
