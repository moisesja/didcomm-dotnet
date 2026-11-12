using System.Net;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #94 red-team pass ADV-1 (identity, authorisation, oracles) at the wire: the registry-aware HTTP
/// and WebSocket endpoints of a TestServer host with the real pickup role behind them. Every test
/// here is an attack that was tried and is <strong>defeated</strong>; the findings of the pass are
/// in <c>DidComm.Mediator.Tests</c> (<c>MessagePickupAdversarialIdentityFindingsTests</c>).
/// </summary>
public sealed class MessagePickupAdversarialIdentityTests
{
    /// <summary>No line the pickup classes wrote, at any level, carries one of <paramref name="values"/> — in its text or in a logged exception.</summary>
    private static void PickupLogsShouldNotCarry(PickupEndpointHost mediator, params string?[] values)
    {
        var lines = mediator.Logs.OfPickup.ToArray();
        lines.Should().NotBeEmpty("the role logs at least its start-up line");
        foreach (var line in lines)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                    line.Rendered.Should().NotContain(value);
            }
        }
    }

    [Fact]
    public async Task Another_recipients_requests_arriving_on_mallorys_live_socket_bind_nothing_write_nothing_and_lease_nothing()
    {
        // Defeated. The recipient's own, fresh requests (never seen by the replay guard) reach the
        // mediator over a socket whose session belongs to Mallory — a shared relay, or an attacker
        // who took them off the wire. They are dispatched without a session: nothing is written to
        // Mallory's socket, the session is not re-bound, live mode is not turned on for anybody,
        // and the delivery-request leases nothing, so the recipient's own connection is unaffected.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var mallory = mediator.Mallory;
        await mediator.EnrolAsync(recipient);
        await mediator.EnrolAsync(mallory);
        var queued = await mediator.QueueAsync(recipient);
        var (socket, _) = await mediator.OpenAsync(mallory);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, mallory)).LiveDelivery.Should().BeTrue();

        var live = MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true);
        var fetchOnTheWrongSocket = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var count = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        await mediator.SendAsync(socket, recipient, live);
        await mediator.SendAsync(socket, recipient, fetchOnTheWrongSocket);
        await mediator.SendAsync(socket, recipient, count);

        // The socket's messages are handled in order and its writer is first-in first-out: the next
        // frame being the answer to Mallory's own request proves nothing was written for the three.
        (await mediator.StatusAsync(socket, mallory)).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: true));

        // Nothing was leased in the recipient's name: its own request gets the message at once.
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, fetch)).Delivery(thid: fetch.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);

        // Live mode was not turned on for the recipient: its next arrival is queued, and Mallory's socket stays silent.
        await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
        (await mediator.StatusAsync(socket, mallory)).MessageCount.Should().Be(0);

        // The session is still Mallory's: her own arrival is pushed to it, encrypted to her.
        var hers = await mediator.QueueAsync(mallory, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(socket, mallory)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(hers.Id);
        mediator.Store.MessageCount.Should().Be(3);
        await mediator.CloseAsync(socket);
        PickupLogsShouldNotCarry(mediator, recipient.Did, mallory.Did, mediator.Mediator, queued.Id, hers.Id, live.Id == fetch.Id ? null : fetch.Thid);
    }

    [Fact]
    public async Task Over_http_mallory_gets_the_same_answer_of_the_same_length_for_the_recipients_did_a_did_nobody_holds_and_a_string_that_is_no_did()
    {
        // Defeated. What an enrolled attacker can observe on the wire for a recipient_did that is
        // registered to someone else, to nobody, or is not a DID: the status code, the media type,
        // the length of the encrypted answer and — once she decrypts it — its members. All equal,
        // for both requests that take a filter, while the recipient holds messages in two states.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var mallory = mediator.Mallory;
        await mediator.EnrolAsync(recipient);
        await mediator.EnrolAsync(mallory);
        var first = await mediator.QueueAsync(recipient);
        await mediator.QueueAsync(recipient);
        var warmUp = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 1);
        (await mediator.AskAsync(recipient, warmUp)).Delivery(thid: warmUp.Id).Should().ContainSingle().Which.Id.Should().Be(first.Id);

        var registeredElsewhere = recipient.Did;
        var registeredNowhere = recipient.Did[..^1] + (recipient.Did[^1] == 'A' ? 'B' : 'A');
        var notADid = new string('x', recipient.Did.Length);

        foreach (var build in new Func<string, Message>[]
                 {
                     value => MessagePickup.CreateStatusRequest(mallory.Did, mediator.Mediator, value),
                     value => MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Mediator, 10, value),
                 })
        {
            var lengths = new List<int>();
            foreach (var value in new[] { registeredElsewhere, registeredNowhere, notADid })
            {
                var request = build(value);
                var response = await mediator.PostAsync(mallory, request);

                response.Status.Should().Be(HttpStatusCode.OK);
                var answer = await mediator.AnswerAsync(mallory, response);
                answer.Status(request.Id).Should().Be(new PickupStatus(0, RecipientDid: value, TotalBytes: 0, LiveDelivery: false));
                answer.Message.Body!.Select(member => member.Key).Should().BeEquivalentTo("recipient_did", "message_count", "total_bytes", "live_delivery");
                lengths.Add(response.Body.Length);
            }
            lengths.Distinct().Should().ContainSingle("the encrypted answers are equally long whoever holds the DID, or nobody");
        }

        mediator.Store.MessageCount.Should().Be(2);
        PickupLogsShouldNotCarry(mediator, recipient.Did, registeredNowhere, notADid, mallory.Did, first.Id);
    }

    [Fact]
    public async Task A_request_that_asks_for_an_acknowledgement_or_names_a_parent_thread_gets_exactly_the_plain_answer_and_nothing_more()
    {
        // Defeated. please_ack / ack / pthid on a pickup request: the dispatcher's ACK machinery and
        // the handler's own push (a delivery through the session, then no reply) do not combine
        // into a second message, an ACK, or an echo of the parent thread.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = await mediator.QueueAsync(recipient);

        var count = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        count.PleaseAck = new List<string> { count.Id };
        count.Pthid = "adv1-parent-thread";
        var counted = await mediator.AskAsync(recipient, count);
        counted.Status(count.Id).MessageCount.Should().Be(1);
        counted.Message.Ack.Should().BeNull();
        counted.Message.PleaseAck.Should().BeNull();
        counted.Message.Pthid.Should().BeNull();

        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        fetch.PleaseAck = new List<string> { fetch.Id };
        fetch.Ack = new List<string> { "adv1-an-id-of-someone-else" };
        fetch.Pthid = "adv1-parent-thread";
        var fetched = await mediator.AskAsync(recipient, fetch);
        fetched.Delivery(thid: fetch.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        fetched.Message.Ack.Should().BeNull("one message per HTTP response: the delivery itself, not an ACK around it");
        fetched.Message.PleaseAck.Should().BeNull();
        fetched.Message.Pthid.Should().BeNull();

        var acknowledge = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued.Id! });
        acknowledge.PleaseAck = new List<string> { acknowledge.Id };
        var acknowledged = await mediator.AskAsync(recipient, acknowledge);
        acknowledged.Status(acknowledge.Id).MessageCount.Should().Be(0);
        acknowledged.Message.Ack.Should().BeNull();
        mediator.Store.MessageCount.Should().Be(0);
        PickupLogsShouldNotCarry(mediator, recipient.Did, mediator.Mediator, queued.Id, "adv1-parent-thread", "adv1-an-id-of-someone-else");
    }
}
