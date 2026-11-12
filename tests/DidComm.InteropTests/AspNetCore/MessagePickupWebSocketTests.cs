using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #94 QA-5 — Message Pickup 3.0 (mediator role) end to end over the registry-aware WebSocket
/// endpoint of a TestServer host (<c>AllowSameSocketReplies</c> on): a socket becomes a
/// return-route session at its first authcrypted request with <c>return_route: all</c>, answers and
/// deliveries come back on it, and live delivery pushes new arrivals over it (FR-PICK-06/08/09).
/// </summary>
/// <remarks>
/// <para>
/// "Nothing was pushed" is proven without waiting: the endpoint's receive loop handles a socket's
/// messages in order and the session has one first-in first-out writer, so when the next frame
/// after a request is that request's answer, nothing was written before it. A push, on the other
/// hand, is queued to the writer before <c>DeliverAsync</c> returns <c>PushedLive</c>.
/// </para>
/// <para>
/// What the server does after a socket closes (ending the session, releasing its leases) runs on
/// the server's own time, so the tests poll for it with a deadline — by asking again, as a
/// recipient would — and never move the clock while doing so: a lease that became free there was
/// freed by the close, not by its timeout.
/// </para>
/// </remarks>
public sealed class MessagePickupWebSocketTests
{
    private const int Kibibyte = PickupEndpointHost.Kibibyte;
    private const int Mebibyte = PickupEndpointHost.Mebibyte;

    // ---- live delivery (FR-PICK-06/08/09) ---------------------------------------------------

    [Fact]
    public async Task Live_delivery_pushes_a_new_arrival_as_a_delivery_of_one_without_a_thread_and_the_acknowledgement_clears_it()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);

        var (socket, opened) = await mediator.OpenAsync(recipient);
        using var _ = socket;
        opened.Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false), "live delivery is off until the recipient turns it on");

        (await mediator.GoLiveAsync(socket, recipient)).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: true));

        var arrival = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        mediator.Store.MessageCount.Should().Be(1, "a live push is the store's own copy, held until it is acknowledged");

        var push = await mediator.ReceiveAsync(socket, recipient);
        var delivered = push.Delivery(thid: null).Should().ContainSingle("a live delivery carries exactly one message").Subject;
        push.Message.CreatedTime.Should().Be(mediator.Clock.Now.ToUnixTimeSeconds(), "the mediator stamps what it pushes (FR-RR-09)");
        await mediator.ShouldBeRelayedAsync(recipient, delivered, arrival);

        // Pushed and not yet acknowledged: counted, and not handed out a second time.
        var again = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var held = (await mediator.AskAsync(socket, recipient, again)).Status(again.Id);
        held.MessageCount.Should().Be(1);
        held.LiveDelivery.Should().BeTrue();

        // The flag is the socket's: a status the same recipient fetches over HTTP reports it off.
        var overHttp = MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator);
        var seenOverHttp = (await mediator.AskAsync(recipient, overHttp)).Status(overHttp.Id);
        seenOverHttp.MessageCount.Should().Be(1);
        seenOverHttp.LiveDelivery.Should().BeFalse();

        (await mediator.AcknowledgeAsync(socket, recipient, delivered.Id)).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: true));
        mediator.Store.MessageCount.Should().Be(0);

        // Turned off again: the next arrival is queued, and nothing is written for it.
        (await mediator.GoLiveAsync(socket, recipient, live: false)).LiveDelivery.Should().BeFalse();
        await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
        var status = await mediator.StatusAsync(socket, recipient);
        status.MessageCount.Should().Be(1);
        status.LiveDelivery.Should().BeFalse();

        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task Messages_queued_before_live_mode_stay_queued_when_it_turns_on_and_are_fetched_with_a_delivery_request()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };

        var (socket, opened) = await mediator.OpenAsync(recipient);
        using var _ = socket;
        opened.MessageCount.Should().Be(2);

        var live = await mediator.GoLiveAsync(socket, recipient);
        live.LiveDelivery.Should().BeTrue();
        live.MessageCount.Should().Be(2);

        // Nothing was pushed when live mode came on: the next frame is the answer to this request.
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(2);

        // They are fetched like any queued message: one delivery on the request's thread.
        var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivered = (await mediator.AskAsync(socket, recipient, request)).Delivery(thid: request.Id);
        delivered.Should().HaveCount(2);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[0], queued[0]);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[1], queued[1]);

        (await mediator.AcknowledgeAsync(socket, recipient, delivered[0].Id, delivered[1].Id))
            .Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: true));
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task A_push_not_acknowledged_before_the_socket_closes_is_deliverable_on_the_next_socket_with_the_same_id_and_live_mode_starts_off()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);

        var (first, _) = await mediator.OpenAsync(recipient);
        using var disposeFirst = first;
        (await mediator.GoLiveAsync(first, recipient)).LiveDelivery.Should().BeTrue();
        var lost = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(first, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(lost.Id);
        mediator.Store.MessageCount.Should().Be(1);

        // Not acknowledged; the socket closes.
        await mediator.CloseAsync(first);
        mediator.Store.MessageCount.Should().Be(1, "the close removes nothing and copies nothing");

        // A new connection starts with live mode off, and the message is still counted.
        var (second, reopened) = await mediator.OpenAsync(recipient);
        using var disposeSecond = second;
        reopened.LiveDelivery.Should().BeFalse("live delivery belongs to the session that turned it on, and ended with it");
        reopened.MessageCount.Should().Be(1);

        // The lost push comes back by delivery-request: the same record, the same id, and the clock
        // has not moved — the close released the lease, not the timeout.
        var (request, answer) = await mediator.FetchUntilDeliveredAsync(second, recipient);
        await mediator.ShouldBeRelayedAsync(recipient, answer.Delivery(thid: request.Id).Should().ContainSingle().Subject, lost);
        mediator.Store.MessageCount.Should().Be(1);

        // An arrival is queued, not pushed, until this socket turns live mode on itself.
        var queued = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
        (await mediator.StatusAsync(second, recipient)).MessageCount.Should().Be(2, "and the next frame was this status, so nothing was pushed");

        (await mediator.GoLiveAsync(second, recipient)).LiveDelivery.Should().BeTrue();
        var pushed = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(second, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(pushed.Id);

        (await mediator.AcknowledgeAsync(second, recipient, lost.Id!, pushed.Id!)).MessageCount.Should().Be(1);
        var last = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(second, recipient, last)).Delivery(thid: last.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        await mediator.CloseAsync(second);
    }

    [Fact]
    public async Task A_delivery_over_a_socket_that_closes_without_an_acknowledgement_is_deliverable_again_on_the_next_socket()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };

        var first = await mediator.ConnectAsync();
        using var disposeFirst = first;
        var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(first, recipient, request)).Delivery(thid: request.Id).Select(m => m.Id).Should().Equal(queued.Select(q => q.Id));

        // Within the lease the same socket is not given them twice.
        var repeat = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(first, recipient, repeat)).Status(repeat.Id).MessageCount.Should().Be(2);

        await mediator.CloseAsync(first);

        var (second, reopened) = await mediator.OpenAsync(recipient);
        using var disposeSecond = second;
        reopened.MessageCount.Should().Be(2);
        var (again, answer) = await mediator.FetchUntilDeliveredAsync(second, recipient);
        var delivered = answer.Delivery(thid: again.Id);
        delivered.Should().HaveCount(2, "a WebSocket session's leases end together when it closes");
        await mediator.ShouldBeRelayedAsync(recipient, delivered[0], queued[0]);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[1], queued[1]);
        mediator.Store.MessageCount.Should().Be(2);
        await mediator.CloseAsync(second);
    }

    [Fact]
    public async Task Arrivals_racing_the_close_of_a_live_socket_are_each_retained_once_and_all_deliverable_on_the_next_socket()
    {
        // Messages keep arriving while the client closes its live socket. Which arrival is the last
        // one pushed is not asserted. What is: every arrival is retained exactly once, pushed or
        // queued; once the session has closed arrivals are queued; and no lease of the closed
        // socket outlives it — all of them are fetched on the next socket with the clock where it
        // was. A lease left behind would only end by its timeout, which never comes here.
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.MaxQueueMessages = 5_000);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (first, _) = await mediator.OpenAsync(recipient);
        (await mediator.GoLiveAsync(first, recipient)).LiveDelivery.Should().BeTrue();
        var session = mediator.Host.Registry.Find(recipient.Did).Should().ContainSingle().Subject;
        var payload = (await mediator.PackFromAliceAsync(recipient)).Payload; // the same bytes every time: each arrival gets its own id

        var results = new ConcurrentQueue<(PickupDeliveryResult Result, bool ClosedBefore)>();
        var pusher = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + PickupEndpointHost.Timeout;
            var afterClose = 0;
            while (afterClose < 3 && DateTime.UtcNow < deadline)
            {
                var closedBefore = session.Closed.IsCancellationRequested;
                results.Enqueue((await mediator.Queue.DeliverAsync(recipient.Did, payload), closedBefore));
                if (closedBefore)
                    afterClose++;
                await Task.Yield();
            }
        });

        // The stream is running: three pushes arrive. Then the client closes under it.
        for (var i = 0; i < 3; i++)
            (await mediator.ReceiveAsync(first, recipient)).Delivery(thid: null).Should().ContainSingle();
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing under load", default).WaitAsync(PickupEndpointHost.Timeout);
        first.Dispose();
        await pusher.WaitAsync(PickupEndpointHost.Timeout);

        var all = results.ToArray();
        all.Should().OnlyContain(r => r.Result.Outcome == PickupDeliveryOutcome.PushedLive || r.Result.Outcome == PickupDeliveryOutcome.Queued);
        all.Should().Contain(r => !r.ClosedBefore && r.Result.Outcome == PickupDeliveryOutcome.PushedLive, "arrivals were pushed before the close");
        all.Where(r => r.ClosedBefore).Should().HaveCount(3, "the stream was still running when the session closed (the overlap happened)")
            .And.OnlyContain(r => r.Result.Outcome == PickupDeliveryOutcome.Queued, "a closed session is not a live target");
        var ids = all.Select(r => r.Result.MessageId!).ToArray();
        ids.Should().OnlyHaveUniqueItems();
        mediator.Store.MessageCount.Should().Be(ids.Length, "every arrival is retained exactly once, pushed or not");

        var (second, reopened) = await mediator.OpenAsync(recipient);
        using var disposeSecond = second;
        reopened.MessageCount.Should().Be(ids.Length);
        var received = new List<string>();
        var until = DateTime.UtcNow + PickupEndpointHost.Timeout;
        while (received.Count < ids.Length)
        {
            var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
            var answer = await mediator.AskAsync(second, recipient, request);
            if (answer.Message.Type == MessagePickup.DeliveryType)
            {
                var batch = answer.Delivery(thid: request.Id).Select(m => m.Id).ToArray();
                received.AddRange(batch);
                (await mediator.AcknowledgeAsync(second, recipient, batch)).MessageCount.Should().Be(ids.Length - received.Count);
                continue;
            }

            // A status while messages are still counted: the closed socket's leases are on their way out.
            answer.Status(request.Id).MessageCount.Should().Be(ids.Length - received.Count);
            (DateTime.UtcNow < until).Should().BeTrue("every retained message is deliverable again without waiting for the lease timeout (FR-PICK-04/08)");
            await Task.Delay(10);
        }
        received.Should().BeEquivalentTo(ids, "each arrival reaches the recipient exactly once");
        mediator.Store.MessageCount.Should().Be(0);
        await mediator.CloseAsync(second);
    }

    // ---- bytes and quotas (FR-PICK-03 / FR-PICK-10) -----------------------------------------

    [Fact]
    public async Task Five_300_KiB_payloads_over_one_socket_arrive_one_per_delivery_and_every_frame_fits_one_mebibyte()
    {
        // Slow for this suite on purpose: 1.5 MiB of real ciphertext is packed, relayed and unpacked.
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new List<RelayedMessage>();
        for (var i = 0; i < 5; i++)
            queued.Add(await mediator.QueueAsync(recipient, packedBytes: 300 * Kibibyte));

        var socket = await mediator.ConnectAsync();
        using var _ = socket;
        for (var i = 0; i < 5; i++)
        {
            var request = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
            var answer = await mediator.AskAsync(socket, recipient, request);
            answer.Packed.Length.Should().BeLessThanOrEqualTo(Mebibyte, "every frame fits max_delivery_bytes");
            var delivered = answer.Delivery(thid: request.Id);
            delivered.Should().ContainSingle("two 300 KiB payloads would not fit one mebibyte once packed");
            await mediator.ShouldBeRelayedAsync(recipient, delivered[0], queued[i]);

            var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { delivered[0].Id });
            var acked = await mediator.AskAsync(socket, recipient, ack);
            acked.Packed.Length.Should().BeLessThanOrEqualTo(Mebibyte);
            acked.Status(ack.Id).MessageCount.Should().Be(4 - i);
        }

        var last = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socket, recipient, last)).Status(last.Id).Should().Be(new PickupStatus(0, TotalBytes: 0, LiveDelivery: false));
        mediator.Store.MessageCount.Should().Be(0);
        await mediator.CloseAsync(socket);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_live_stream_that_is_never_acknowledged_stays_within_the_grant_quota_through_the_disconnect_and_is_fetched_whole_afterwards(bool quotaInMessages)
    {
        // The grant's quota holds five of these messages, counted in messages or in bytes.
        const int quota = 5;
        const int size = 4 * Kibibyte;
        const long byteQuota = quota * size;
        await using var mediator = await PickupEndpointHost.StartAsync(o =>
        {
            if (quotaInMessages)
                o.MaxQueueMessages = quota;
            else
                o.MaxQueueBytes = byteQuota;
        });
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (first, _) = await mediator.OpenAsync(recipient);
        using var disposeFirst = first;
        (await mediator.GoLiveAsync(first, recipient)).LiveDelivery.Should().BeTrue();

        // Eight arrivals, none acknowledged: five are admitted and pushed, three are refused.
        var kept = new List<RelayedMessage>();
        for (var i = 0; i < 8; i++)
        {
            var relayed = await mediator.PackFromAliceAsync(recipient, packedBytes: size);
            var result = await mediator.Queue.DeliverAsync(recipient.Did, relayed.Payload);
            if (i < quota)
            {
                result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
                kept.Add(relayed with { Id = result.MessageId });
            }
            else
            {
                result.Outcome.Should().Be(PickupDeliveryOutcome.QueueFull, "a live push never bypasses the grant's quota");
                result.MessageId.Should().BeNull();
            }
            mediator.Store.MessageCount.Should().BeLessThanOrEqualTo(quota);
            mediator.Store.TotalBytes.Should().BeLessThanOrEqualTo(byteQuota);
        }
        mediator.Store.MessageCount.Should().Be(quota);
        mediator.Store.TotalBytes.Should().Be(kept.Sum(k => (long)k.Payload.Length));

        // Exactly the five admitted ones were pushed, in order…
        foreach (var expected in kept)
            await mediator.ShouldBeRelayedAsync(recipient, (await mediator.ReceiveAsync(first, recipient)).Delivery(thid: null).Should().ContainSingle().Subject, expected);
        // …and nothing for the three refused ones: the next frame is the answer to this request.
        var streamed = await mediator.StatusAsync(first, recipient);
        streamed.MessageCount.Should().Be(quota);
        streamed.LiveDelivery.Should().BeTrue();

        // The disconnect releases the leases in place: no copy is made, the quota still holds.
        await mediator.CloseAsync(first);
        mediator.Store.MessageCount.Should().Be(quota);
        (await mediator.Queue.DeliverAsync(recipient.Did, (await mediator.PackFromAliceAsync(recipient, packedBytes: size)).Payload))
            .Outcome.Should().Be(PickupDeliveryOutcome.QueueFull, "unacknowledged messages keep counting against the quota after the disconnect");
        mediator.Store.MessageCount.Should().Be(quota);
        mediator.Store.TotalBytes.Should().BeLessThanOrEqualTo(byteQuota);

        // All five are fetched on the next socket: the same ids, in arrival order.
        var (second, reopened) = await mediator.OpenAsync(recipient);
        using var disposeSecond = second;
        reopened.MessageCount.Should().Be(quota);
        reopened.LiveDelivery.Should().BeFalse();
        var (request, answer) = await mediator.FetchUntilDeliveredAsync(second, recipient);
        var delivered = answer.Delivery(thid: request.Id);
        delivered.Should().HaveCount(quota);
        for (var i = 0; i < quota; i++)
            await mediator.ShouldBeRelayedAsync(recipient, delivered[i], kept[i]);
        mediator.Store.MessageCount.Should().Be(quota);

        (await mediator.AcknowledgeAsync(second, recipient, delivered.Select(m => m.Id).ToArray())).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
        await mediator.CloseAsync(second);
    }

    [Fact]
    public async Task Over_a_socket_with_one_push_slot_a_batch_over_the_budget_is_trimmed_and_resent_and_a_push_over_the_budget_is_queued_instead()
    {
        // The recipient publishes 38 key-agreement keys, so an envelope for it is about 78 KiB
        // larger than a one-key peer's and the mediator's size estimate is short (see the HTTP
        // twin of this test). The session measures the packed frame and refuses it; the mediator
        // then sends one message fewer. With a single push slot that only works if the refused
        // frame gave its slot back.
        const int budget = 352 * Kibibyte;
        const int payload = 86 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(pickup: o => o.MaxDeliveryBytes = budget, sessions: s => s.MaxPendingPushes = 1);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        (2 * payload).Should().BeLessThanOrEqualTo(max, "the pair is within the batch byte budget, so the estimate lets it through");
        var queued = new[] { await mediator.QueueAsync(wide, packedBytes: payload), await mediator.QueueAsync(wide, packedBytes: payload) };

        var socket = await mediator.ConnectAsync();
        using var _ = socket;
        foreach (var expected in queued)
        {
            // One per delivery, although both were leased for the first: the one taken out of the
            // batch lost its lease and is there for the very next request.
            var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
            var answer = await mediator.AskAsync(socket, wide, request);
            answer.Packed.Length.Should().BeLessThanOrEqualTo(budget, "the packed frame is measured, not estimated");
            await mediator.ShouldBeRelayedAsync(wide, answer.Delivery(thid: request.Id).Should().ContainSingle().Subject, expected);
        }
        (await mediator.AcknowledgeAsync(socket, wide, queued[0].Id!, queued[1].Id!)).MessageCount.Should().Be(0);

        // Live delivery: an arrival whose push would be over the budget for this recipient is not
        // pushed. The push only gives its lease back: the message is queued, and nothing is
        // written for it.
        (await mediator.GoLiveAsync(socket, wide)).LiveDelivery.Should().BeTrue();
        var tooWide = await mediator.QueueAsync(wide, packedBytes: max, expected: PickupDeliveryOutcome.Queued);
        var status = await mediator.StatusAsync(socket, wide);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(tooWide.Payload.Length);

        // The refused push gave its slot back too: the next arrival that fits is pushed.
        var fits = await mediator.QueueAsync(wide, packedBytes: payload, expected: PickupDeliveryOutcome.PushedLive);
        var push = await mediator.ReceiveAsync(socket, wide);
        push.Packed.Length.Should().BeLessThanOrEqualTo(budget);
        await mediator.ShouldBeRelayedAsync(wide, push.Delivery(thid: null).Should().ContainSingle().Subject, fits);
        mediator.Store.MessageCount.Should().Be(2);

        // The delivery-request that then meets the queued one finds that it does not fit even
        // alone. It is answered with the status, and nothing is removed: the message is still
        // there and counted, beside the pushed one that waits for its acknowledgement.
        var fetch = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
        var met = await mediator.AskAsync(socket, wide, fetch);
        met.Packed.Length.Should().BeLessThanOrEqualTo(budget);
        met.Status(fetch.Id).MessageCount.Should().Be(2);
        mediator.Store.MessageCount.Should().Be(2);
        (await mediator.AcknowledgeAsync(socket, wide, fits.Id!)).MessageCount.Should().Be(1);
        mediator.Store.MessageCount.Should().Be(1);
        mediator.Store.TotalBytes.Should().Be(tooWide.Payload.Length);
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task Over_a_socket_messages_the_recipients_envelope_cannot_carry_are_passed_over_by_the_request_that_meets_them_and_a_reconnect_does_not_bring_them_back()
    {
        // Two payloads at the admission bound for a 38-key recipient (over the budget even alone,
        // see the HTTP twin), and a deliverable one behind them. Each keeps the lease of the request
        // that met it — a lease that belongs to the socket's session — and that same request looks
        // further and delivers what is behind them. When the socket closes its leases end: on the
        // next socket the two are first in line again, are tried once more, and again the request
        // that meets them delivers what is behind them. The clock never moves.
        const int budget = 352 * Kibibyte;
        const int small = 86 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(pickup: o => o.MaxDeliveryBytes = budget, parkingStore: true);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        await mediator.EnrolAsync(wide);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        var bigs = new[] { await mediator.QueueAsync(wide, packedBytes: max), await mediator.QueueAsync(wide, packedBytes: max) };
        var behind = await mediator.QueueAsync(wide, packedBytes: small);

        // One delivery-request on a socket: its answer, within the budget, and what the store
        // handed out in each lease round the request made.
        async Task<(PickupAnswer Answer, IReadOnlyList<IReadOnlyList<string>> Rounds)> FetchAsync(WebSocket socket)
        {
            var calls = mediator.Parking.Leased.Count;
            var request = MessagePickup.CreateDeliveryRequest(wide.Did, mediator.Mediator, 10);
            var answer = await mediator.AskAsync(socket, wide, request);
            answer.Packed.Length.Should().BeLessThanOrEqualTo(budget, "whatever is queued, a frame fits max_delivery_bytes");
            answer.Message.Thid.Should().Be(request.Id);
            return (answer, mediator.Parking.Leased.Skip(calls).ToList());
        }

        var first = await mediator.ConnectAsync();
        using var disposeFirst = first;

        // The first request meets the two, one after the other, each alone; neither fits. It looks
        // further and delivers the message behind them. Nothing is removed.
        var met = await FetchAsync(first);
        met.Rounds.Should().HaveCount(3);
        met.Rounds[0].Should().Equal(bigs[0].Id);
        met.Rounds[1].Should().Equal(bigs[1].Id);
        met.Rounds[2].Should().Equal(behind.Id);
        await mediator.ShouldBeRelayedAsync(wide, met.Answer.Delivery(met.Answer.Message.Thid).Should().ContainSingle().Subject, behind);
        mediator.Store.MessageCount.Should().Be(3);

        // The second request: both are passed over and nothing else is deliverable. One round, a
        // status, all three still counted. Then the delivered one is acknowledged.
        var idle = await FetchAsync(first);
        idle.Rounds.Should().ContainSingle().Which.Should().BeEmpty();
        var counted = idle.Answer.Status(idle.Answer.Message.Thid!);
        counted.MessageCount.Should().Be(3);
        counted.TotalBytes.Should().Be(bigs[0].Payload.Length + bigs[1].Payload.Length + behind.Payload.Length);
        (await mediator.AcknowledgeAsync(first, wide, behind.Id!)).MessageCount.Should().Be(2);

        // The socket closes: the leases its requests took end with it (waited for: the server ends
        // them on its own time). Nothing is removed.
        var ended = mediator.Parking.OwnerReleases;
        await mediator.CloseAsync(first);
        (await ReturnRouteTestHost.WaitUntilAsync(() => mediator.Parking.OwnerReleases > ended, PickupEndpointHost.Timeout))
            .Should().BeTrue("a lease kept on a socket ends when that socket closes, not by its timeout");
        mediator.Store.MessageCount.Should().Be(2);

        // On the next socket the two are NOT first in line again: that they did not fit is the
        // records' own memory, not a lease that ended with the first socket. The first request on
        // the new connection spends nothing on them and delivers the message that arrived behind.
        var later = await mediator.QueueAsync(wide, packedBytes: small);
        var (second, reopened) = await mediator.OpenAsync(wide);
        using var disposeSecond = second;
        reopened.MessageCount.Should().Be(3);
        var again = await FetchAsync(second);
        again.Rounds.Should().ContainSingle("a reconnect does not bring the passed-over messages up again").Which.Should().Equal(later.Id);
        await mediator.ShouldBeRelayedAsync(wide, again.Answer.Delivery(again.Answer.Message.Thid).Should().ContainSingle().Subject, later);
        (await mediator.AcknowledgeAsync(second, wide, later.Id!)).MessageCount.Should().Be(2);

        // And the request after that: one round, nothing deliverable, a status.
        var last = await FetchAsync(second);
        last.Rounds.Should().ContainSingle().Which.Should().BeEmpty();
        last.Answer.Status(last.Answer.Message.Thid!).MessageCount.Should().Be(2);

        // Through all of it the mediator neither removed the two nor gave them back by id: each
        // was set aside once, by the request that met it.
        mediator.Parking.Acknowledged.Should().BeEquivalentTo(new[] { behind.Id, later.Id });
        mediator.Parking.GivenBack.Should().NotContain(new[] { bigs[0].Id, bigs[1].Id });
        mediator.Parking.PassedOver.Select(p => p.Id).Should().Equal(bigs[0].Id, bigs[1].Id);
        mediator.Store.MessageCount.Should().Be(2);
        mediator.Store.TotalBytes.Should().Be(bigs[0].Payload.Length + bigs[1].Payload.Length);

        // What clears them is the host: a purge of the grant's messages.
        (await mediator.Store.PurgeGrantAsync(wide.Did, default)).Should().Be(2);
        (await mediator.StatusAsync(second, wide)).MessageCount.Should().Be(0);
        await mediator.CloseAsync(second);
    }

    [Fact]
    public async Task Over_a_socket_a_queue_of_small_big_small_delivers_both_small_ones_once_in_two_requests_and_keeps_the_big_one()
    {
        // The payloads are for a one-key DID the 38-key recipient registered: the small ones are
        // about 1 KiB, so the batch estimate takes all three for the first request. The session
        // refuses the batch of three and the batch of two; the first message goes alone. The second
        // request meets the big one first; it does not fit even alone and keeps the request's
        // lease, and the same request looks further and delivers the other small one. Nothing is
        // acknowledged until the end, so every delivered message stays under its lease and none
        // comes twice.
        const int budget = 352 * Kibibyte;
        await using var mediator = await PickupEndpointHost.StartAsync(pickup: o => o.MaxDeliveryBytes = budget);
        var wide = await mediator.CreateManyKeyPeerAsync(38);
        var addressee = (await mediator.Host.CreatePeersAsync(1))[0];
        await mediator.EnrolAsync(wide, wide.Did, addressee.Did);
        var max = mediator.Queue.MaxAdmissiblePayloadBytes;
        var first = await mediator.QueueAsync(addressee);
        var big = await mediator.QueueAsync(addressee, packedBytes: max - 4 * Kibibyte);
        var last = await mediator.QueueAsync(addressee);
        var all = first.Payload.Length + big.Payload.Length + last.Payload.Length;
        all.Should().BeLessThanOrEqualTo(max, "all three are within the batch byte budget, so the estimate takes them together");

        var socket = await mediator.ConnectAsync();
        using var _ = socket;
        // Three requests at most: one per delivery, and the status that says nothing more can be
        // fetched.
        var steps = await mediator.DrainAsync(wide, request => mediator.AskAsync(socket, wide, request), budget, maxRequests: 3, acknowledge: false, left: 1);

        var delivered = steps.SelectMany(step => step.Delivered).ToList();
        delivered.Select(m => m.Id).Should().Equal(new[] { first.Id, last.Id }, "both small messages arrive, each once and in order; the big one never does");
        await mediator.ShouldBeRelayedAsync(addressee, delivered[0], first);
        await mediator.ShouldBeRelayedAsync(addressee, delivered[1], last);
        steps.Take(2).SelectMany(step => step.Delivered).Should().HaveCount(2, "both are delivered within two requests: one per delivery; the message that does not fit costs none");
        steps[^1].Status!.TotalBytes.Should().Be(all, "all three are still stored, and counted");
        mediator.Store.MessageCount.Should().Be(3);

        (await mediator.AcknowledgeAsync(socket, wide, first.Id!, last.Id!)).MessageCount.Should().Be(1);
        mediator.Store.MessageCount.Should().Be(1, "the big one was not removed");
        mediator.Store.TotalBytes.Should().Be(big.Payload.Length);

        // What clears it is the host: a purge of that recipient DID's messages.
        (await mediator.Store.PurgeRecipientAsync(wide.Did, addressee.Did, default)).Should().Be(1);
        (await mediator.StatusAsync(socket, wide)).MessageCount.Should().Be(0);
        await mediator.CloseAsync(socket);
    }

    // ---- what is pushed, and when (FR-PICK-09, JC-14) ---------------------------------------

    [Fact]
    public async Task A_message_that_is_not_yet_due_or_must_not_go_live_is_queued_on_a_live_socket_and_fetched_by_request()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, recipient)).LiveDelivery.Should().BeTrue();

        // Due in 30 seconds (a forward's delay): never pushed, and not reported until it is due.
        var delayed = await mediator.PackFromAliceAsync(recipient);
        var delayedResult = await mediator.Queue.DeliverAsync(recipient.Did, delayed.Payload, new PickupDeliveryOptions(NotBefore: mediator.Clock.Now + TimeSpan.FromSeconds(30)));
        delayedResult.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(0, "and the next frame was this status, so nothing was pushed");
        mediator.Store.MessageCount.Should().Be(1, "it is retained, and counts against the quota, from the moment it arrives");

        // Becoming due does not push it either: it is fetched with a delivery-request.
        mediator.Clock.Advance(TimeSpan.FromSeconds(30));
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(1);
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socket, recipient, fetch)).Delivery(thid: fetch.Id).Should().ContainSingle().Which.Id.Should().Be(delayedResult.MessageId);

        // The pipeline may also ask for the queue outright.
        var held = await mediator.PackFromAliceAsync(recipient);
        (await mediator.Queue.DeliverAsync(recipient.Did, held.Payload, new PickupDeliveryOptions(AllowLive: false))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(2);
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task An_arrival_for_any_did_registered_under_the_grant_is_pushed_to_the_grant_holders_live_socket()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var second = (await mediator.Host.CreatePeersAsync(1))[0]; // a second DID of the same recipient agent
        await mediator.EnrolAsync(recipient, recipient.Did, second.Did);
        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, recipient)).LiveDelivery.Should().BeTrue();

        var arrival = await mediator.QueueAsync(second, expected: PickupDeliveryOutcome.PushedLive);

        // Pushed to the peer that holds the grant; the payload is for the second DID's keys.
        var delivered = (await mediator.ReceiveAsync(socket, recipient)).Delivery(thid: null).Should().ContainSingle().Subject;
        await mediator.ShouldBeRelayedAsync(second, delivered, arrival);
        (await mediator.AcknowledgeAsync(socket, recipient, delivered.Id)).MessageCount.Should().Be(0);
        await mediator.CloseAsync(socket);
    }

    // ---- several connections (JC-13) --------------------------------------------------------

    [Fact]
    public async Task With_live_mode_on_two_sockets_of_one_recipient_an_arrival_is_pushed_once_to_the_newest()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (older, _) = await mediator.OpenAsync(recipient);
        using var disposeOlder = older;
        (await mediator.GoLiveAsync(older, recipient)).LiveDelivery.Should().BeTrue();
        var (newer, opened) = await mediator.OpenAsync(recipient);
        using var disposeNewer = newer;
        opened.LiveDelivery.Should().BeFalse("live delivery is per session: the second socket has not turned it on");
        (await mediator.GoLiveAsync(newer, recipient)).LiveDelivery.Should().BeTrue();

        var arrival = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);

        mediator.Store.MessageCount.Should().Be(1, "one record, one lease, one push");
        (await mediator.ReceiveAsync(newer, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(arrival.Id);
        // Exactly one push in total: on each socket the next frame is the answer to a new request.
        var onOlder = await mediator.StatusAsync(older, recipient);
        onOlder.MessageCount.Should().Be(1);
        onOlder.LiveDelivery.Should().BeTrue();
        (await mediator.StatusAsync(newer, recipient)).MessageCount.Should().Be(1);
        (await mediator.AcknowledgeAsync(newer, recipient, arrival.Id!)).MessageCount.Should().Be(0);

        // The newest closes; the older one is still live and takes the next arrival.
        await mediator.CloseAsync(newer);
        var next = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(older, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(next.Id);
        (await mediator.AcknowledgeAsync(older, recipient, next.Id!)).MessageCount.Should().Be(0);
        await mediator.CloseAsync(older);
    }

    [Fact]
    public async Task An_arrival_for_one_recipient_is_never_pushed_to_another_peers_live_socket()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        var mallory = mediator.Mallory;
        await mediator.EnrolAsync(recipient);
        await mediator.EnrolAsync(mallory);
        var (socket, _) = await mediator.OpenAsync(mallory);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, mallory)).LiveDelivery.Should().BeTrue();

        // The recipient has no live session: its message is queued, whatever Mallory has open.
        var queued = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);

        var status = await mediator.StatusAsync(socket, mallory);
        status.MessageCount.Should().Be(0, "and the next frame on Mallory's socket was this status, so nothing was pushed to it");
        var filtered = MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Mediator, 10, recipient.Did);
        (await mediator.AskAsync(socket, mallory, filtered)).Status(filtered.Id)
            .Should().Be(new PickupStatus(0, RecipientDid: recipient.Did, TotalBytes: 0, LiveDelivery: true));
        (await mediator.AcknowledgeAsync(socket, mallory, queued.Id!)).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(1, "another peer's acknowledgement of the recipient's id removes nothing");
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task A_message_delivered_over_http_is_neither_pushed_nor_delivered_again_on_a_socket_within_its_lease_and_the_sockets_close_does_not_free_it()
    {
        var lease = TimeSpan.FromSeconds(60);
        await using var mediator = await PickupEndpointHost.StartAsync(o => o.LeaseTimeout = lease);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var overHttp = await mediator.QueueAsync(recipient);

        // Delivered over HTTP: leased by time only.
        var http = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, http)).Delivery(thid: http.Id).Should().ContainSingle().Which.Id.Should().Be(overHttp.Id);

        // A socket of the same recipient, live mode on: the message is counted, not pushed…
        var (first, opened) = await mediator.OpenAsync(recipient);
        using var disposeFirst = first;
        opened.MessageCount.Should().Be(1);
        (await mediator.GoLiveAsync(first, recipient)).MessageCount.Should().Be(1);
        // …and not delivered again by a delivery-request on the socket.
        var repeat = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(first, recipient, repeat)).Status(repeat.Id).MessageCount.Should().Be(1);

        // A second message is pushed over the socket (leased to the socket), and the socket closes
        // without acknowledging either.
        var overSocket = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(first, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(overSocket.Id);
        await mediator.CloseAsync(first);

        // The close frees the socket's own lease and only that one: when the pushed message is
        // deliverable again, the HTTP-delivered one (ahead of it in the queue) still is not.
        var (second, _) = await mediator.OpenAsync(recipient);
        using var disposeSecond = second;
        var (again, answer) = await mediator.FetchUntilDeliveredAsync(second, recipient);
        answer.Delivery(thid: again.Id).Select(m => m.Id).Should().Equal(new[] { overSocket.Id }, "an HTTP lease ends by its timeout only");
        var overHttpAgain = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, overHttpAgain)).Status(overHttpAgain.Id).MessageCount.Should().Be(2);

        // After the lease timeout it is delivered again — over the socket, with the same id.
        mediator.Clock.Advance(lease);
        var late = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var delivered = (await mediator.AskAsync(second, recipient, late)).Delivery(thid: late.Id);
        delivered.Select(m => m.Id).Should().Equal(overHttp.Id, overSocket.Id);
        await mediator.ShouldBeRelayedAsync(recipient, delivered[0], overHttp);
        mediator.Store.MessageCount.Should().Be(2);

        // Acknowledged over HTTP: the store is keyed by the grant, not by the connection.
        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { overHttp.Id!, overSocket.Id! });
        (await mediator.AskAsync(recipient, ack)).Status(ack.Id).MessageCount.Should().Be(0);
        (await mediator.StatusAsync(second, recipient)).MessageCount.Should().Be(0);
        await mediator.CloseAsync(second);
    }

    // ---- the session as pickup uses it (FR-RR-05) -------------------------------------------

    [Fact]
    public async Task Return_route_all_is_needed_once_per_socket_and_later_requests_without_the_header_are_answered_on_it()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = new[] { await mediator.QueueAsync(recipient), await mediator.QueueAsync(recipient) };
        var socket = await mediator.ConnectAsync();
        using var _ = socket;

        async Task<PickupAnswer> AskWithoutHeaderAsync(Message request)
        {
            await mediator.SendAsync(socket, recipient, request, returnRoute: false);
            return await mediator.ReceiveAsync(socket, recipient);
        }

        // Before any return_route: all the socket is not a session: a delivery-request on it leases
        // nothing and nothing is written back.
        await mediator.SendAsync(socket, recipient, MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10), returnRoute: false);

        // The first request with the header opens the session; its answer is the first frame.
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(2);

        // From here on the header is not needed ("once per established websocket").
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await AskWithoutHeaderAsync(fetch)).Delivery(thid: fetch.Id).Select(m => m.Id)
            .Should().Equal(new[] { queued[0].Id, queued[1].Id }, "both are still there: the request before the session leased nothing");
        var ack = MessagePickup.CreateMessagesReceived(recipient.Did, mediator.Mediator, new[] { queued[0].Id!, queued[1].Id! });
        (await AskWithoutHeaderAsync(ack)).Status(ack.Id).MessageCount.Should().Be(0);
        var live = MessagePickup.CreateLiveDeliveryChange(recipient.Did, mediator.Mediator, true);
        (await AskWithoutHeaderAsync(live)).Status(live.Id).LiveDelivery.Should().BeTrue();

        var arrival = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(socket, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(arrival.Id);
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task While_a_live_socket_has_its_return_route_set_to_none_an_arrival_is_queued_and_nothing_is_written()
    {
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, recipient)).LiveDelivery.Should().BeTrue();
        var session = mediator.Host.Registry.Find(recipient.Did).Should().ContainSingle().Subject;

        // The recipient stops pushes on this socket: return_route none on a later request. That
        // request is not answered; wait until the server has processed it.
        var none = mediator.Prepare(MessagePickup.CreateStatusRequest(recipient.Did, mediator.Mediator), returnRoute: false);
        ReturnRouteHeaderWriter.Write(none, ReturnRouteMode.None, thread: null);
        await ReturnRouteTestHost.SendAsync(socket, await mediator.Host.AuthcryptAsync(recipient, none));
        (await ReturnRouteTestHost.WaitUntilAsync(() => session.Mode == ReturnRouteMode.None, PickupEndpointHost.Timeout)).Should().BeTrue();

        // A session that does not take pushes is not a live target: the arrival waits in the queue.
        var queued = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);

        // return_route all again: the first frame on the socket is the answer to this request, so
        // neither the unanswered status nor a push was written in between.
        (await mediator.StatusAsync(socket, recipient)).MessageCount.Should().Be(1);
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socket, recipient, fetch)).Delivery(thid: fetch.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        await mediator.CloseAsync(socket);
    }

    [Fact]
    public async Task A_socket_evicted_by_a_newer_one_loses_its_live_mode_and_its_leases_without_waiting_for_its_own_close()
    {
        await using var mediator = await PickupEndpointHost.StartAsync(sessions: s => s.MaxSessionsPerPeer = 1);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (older, _) = await mediator.OpenAsync(recipient);
        using var disposeOlder = older;
        (await mediator.GoLiveAsync(older, recipient)).LiveDelivery.Should().BeTrue();
        var lost = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(older, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(lost.Id);

        // A second socket of the same recipient: at one session per peer it evicts the first, whose
        // peer is told with a normal close frame — and does not answer it yet.
        var (newer, opened) = await mediator.OpenAsync(recipient);
        using var disposeNewer = newer;
        opened.LiveDelivery.Should().BeFalse();
        opened.MessageCount.Should().Be(1);
        (await ReturnRouteTestHost.ReceiveAsync(older, PickupEndpointHost.Timeout)).Type.Should().Be(WebSocketMessageType.Close);

        // The evicted session's own end signal released its lease: the unacknowledged push is
        // deliverable on the new socket, with the clock where it was and the old connection open.
        var (request, answer) = await mediator.FetchUntilDeliveredAsync(newer, recipient);
        answer.Delivery(thid: request.Id).Should().ContainSingle().Which.Id.Should().Be(lost.Id);

        // Live mode ended with the evicted session: an arrival is queued, not pushed anywhere.
        var queued = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.Queued);
        (await mediator.StatusAsync(newer, recipient)).MessageCount.Should().Be(2);
        mediator.Store.MessageCount.Should().Be(2);

        await older.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);
        (await mediator.AcknowledgeAsync(newer, recipient, lost.Id!, queued.Id!)).MessageCount.Should().Be(0);
        await newer.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
    }

    [Fact]
    public async Task When_the_session_has_no_room_for_a_push_the_arrival_is_queued_instead_and_every_message_arrives_exactly_once()
    {
        // One pending push per session, and eight arrivals at once: while one is being packed for
        // the socket the session has no room for another. Which arrivals find room depends on the
        // interleaving, so that is not asserted. What is: each arrival is either pushed or queued,
        // every pushed one arrives as its own frame, the others are deliverable at once (a push
        // that was not taken keeps no lease), and nothing is lost or doubled.
        const int arrivals = 8;
        await using var mediator = await PickupEndpointHost.StartAsync(sessions: s => s.MaxPendingPushes = 1);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, recipient)).LiveDelivery.Should().BeTrue();

        // Packed beforehand (and sized so that all of them fit one delivery), so the calls overlap.
        var relayed = new RelayedMessage[arrivals];
        for (var i = 0; i < arrivals; i++)
            relayed[i] = await mediator.PackFromAliceAsync(recipient, packedBytes: 60 * Kibibyte);
        var results = await Task.WhenAll(relayed.Select(r => Task.Run(async () => await mediator.Queue.DeliverAsync(recipient.Did, r.Payload))));

        var pushed = new Dictionary<string, RelayedMessage>();
        var queuedInstead = new Dictionary<string, RelayedMessage>();
        for (var i = 0; i < arrivals; i++)
        {
            results[i].Outcome.Should().BeOneOf(PickupDeliveryOutcome.PushedLive, PickupDeliveryOutcome.Queued);
            (results[i].Outcome == PickupDeliveryOutcome.PushedLive ? pushed : queuedInstead).Add(results[i].MessageId!, relayed[i] with { Id = results[i].MessageId });
        }
        mediator.Store.MessageCount.Should().Be(arrivals, "every arrival is retained, pushed or not");
        pushed.Should().NotBeEmpty("the session was idle, so whichever arrival reached it first was taken");

        // One frame per pushed arrival, each a delivery of one.
        var received = new HashSet<string>();
        for (var i = 0; i < pushed.Count; i++)
        {
            var delivered = (await mediator.ReceiveAsync(socket, recipient)).Delivery(thid: null).Should().ContainSingle().Subject;
            pushed.Should().ContainKey(delivered.Id);
            await mediator.ShouldBeRelayedAsync(recipient, delivered, pushed[delivered.Id]);
            received.Add(delivered.Id).Should().BeTrue("a message is pushed once");
        }

        // The next frame answers this request: a delivery of exactly the arrivals that were not
        // pushed, or a status when all of them were.
        var fetch = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        var answer = await mediator.AskAsync(socket, recipient, fetch);
        if (queuedInstead.Count == 0)
        {
            answer.Status(fetch.Id).MessageCount.Should().Be(arrivals);
        }
        else
        {
            var delivered = answer.Delivery(thid: fetch.Id);
            delivered.Select(m => m.Id).Should().BeEquivalentTo(queuedInstead.Keys);
            foreach (var message in delivered)
            {
                await mediator.ShouldBeRelayedAsync(recipient, message, queuedInstead[message.Id]);
                received.Add(message.Id).Should().BeTrue("a message that was not pushed is not delivered twice either");
            }
        }

        var all = relayed.Select((_, i) => results[i].MessageId!).ToArray();
        received.Should().BeEquivalentTo(all, "every arrival reached the recipient exactly once");
        (await mediator.AcknowledgeAsync(socket, recipient, all)).MessageCount.Should().Be(0);
        mediator.Store.MessageCount.Should().Be(0);
        await mediator.CloseAsync(socket);
    }

    // ---- a lease is given back only by its taker (two connections of one recipient) ----------

    [Fact]
    public async Task A_lease_given_back_late_by_a_socket_does_not_free_a_message_that_was_delivered_over_http_in_the_meantime()
    {
        // Socket A asks for a delivery and takes its return route away in the same message
        // (return_route: none): the mediator leases the message, the session does not take the
        // push, and the mediator gives A's lease back. That give-back is held at the store's door.
        // While it waits, A's lease runs out and the recipient fetches the message over HTTP, under
        // a new lease. When A's give-back finally runs it names A's lease, which is no longer on
        // the record: the HTTP lease stands, and a third request inside it gets a status.
        var lease = TimeSpan.FromSeconds(60);
        await using var mediator = await PickupEndpointHost.StartAsync(pickup: o => o.LeaseTimeout = lease, parkingStore: true);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socketA, _) = await mediator.OpenAsync(recipient);
        using var disposeA = socketA;
        var queued = await mediator.QueueAsync(recipient);

        var parked = mediator.Parking.ParkNextRelease();
        await SendDeliveryRequestTheSessionWillNotAnswerAsync(mediator, socketA, recipient);
        var giveBack = await parked.Arrived.WaitAsync(PickupEndpointHost.Timeout);
        giveBack.Ids.Should().Equal(queued.Id);
        var leaseOfA = mediator.Parking.LeaseRequests.Should().ContainSingle().Subject.Lease;
        leaseOfA.Owner.Should().NotBeNull("a lease taken over a socket belongs to the socket's session");
        giveBack.LeaseId.Should().Be(leaseOfA.Id, "the mediator gives back the lease it took, by its id");

        // A's lease runs out; the recipient fetches the message over HTTP.
        mediator.Clock.Advance(lease);
        var overHttp = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, overHttp)).Delivery(thid: overHttp.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);

        // Now A's give-back runs, to its end.
        parked.Resume();
        var endedByA = await parked.Finished.WaitAsync(PickupEndpointHost.Timeout);

        // A third request inside the HTTP lease gets a status, not the message again: over HTTP…
        var third = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, third)).Status(third.Id).MessageCount.Should().Be(1);
        // …and on A, whose first frame since it opened is the answer to this request.
        var onA = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socketA, recipient, onA)).Status(onA.Id).MessageCount.Should().Be(1);
        endedByA.Should().Be(0, "the record carried the HTTP request's lease by then, and that one is not A's to end");
        mediator.Store.MessageCount.Should().Be(1);
        await mediator.CloseAsync(socketA);
    }

    [Fact]
    public async Task A_socket_evicted_while_it_gives_a_lease_back_cannot_free_the_message_once_it_was_delivered_over_http()
    {
        // As above, but what ends A's lease is the end of A's session: a newer socket of the same
        // recipient evicts it (one session per peer) while A's give-back is held at the door. The
        // session's end releases what A held, the recipient fetches the message over HTTP, and
        // only then does A's own give-back run. The clock never moves.
        await using var mediator = await PickupEndpointHost.StartAsync(sessions: s => s.MaxSessionsPerPeer = 1, parkingStore: true);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var (socketA, _) = await mediator.OpenAsync(recipient);
        using var disposeA = socketA;
        var queued = await mediator.QueueAsync(recipient);

        var parked = mediator.Parking.ParkNextRelease();
        await SendDeliveryRequestTheSessionWillNotAnswerAsync(mediator, socketA, recipient);
        (await parked.Arrived.WaitAsync(PickupEndpointHost.Timeout)).Ids.Should().Equal(queued.Id);

        var (socketB, opened) = await mediator.OpenAsync(recipient);
        using var disposeB = socketB;
        opened.MessageCount.Should().Be(1);
        (await ReturnRouteTestHost.ReceiveAsync(socketA, PickupEndpointHost.Timeout)).Type.Should().Be(WebSocketMessageType.Close, "the evicted peer is told with a close frame");

        // Asked again until the session's end has released A's lease.
        var (overHttp, answer) = await mediator.FetchUntilDeliveredAsync(recipient, request => mediator.AskAsync(recipient, request));
        answer.Delivery(thid: overHttp.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);

        parked.Resume();
        var endedByA = await parked.Finished.WaitAsync(PickupEndpointHost.Timeout);

        // Inside the HTTP lease the message is not delivered again, over HTTP or on the new socket.
        var third = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, third)).Status(third.Id).MessageCount.Should().Be(1);
        var onB = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socketB, recipient, onB)).Status(onB.Id).MessageCount.Should().Be(1);
        endedByA.Should().Be(0, "A's lease was no longer on the record");
        mediator.Store.MessageCount.Should().Be(1);

        await socketA.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", default);
        await socketB.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", default);
    }

    [Fact]
    public async Task After_a_socket_that_was_given_a_message_closes_and_http_delivers_it_again_nothing_done_in_the_sockets_name_frees_it()
    {
        // The plain sequence: socket A is given the message and closes without acknowledging; the
        // close releases A's lease and the recipient fetches the message over HTTP. What the real
        // endpoints do for A after that cannot be held back from here, so it is replayed: the two
        // ways the mediator ends a lease for a connection — by the lease's id, and by the session
        // that owned it — are applied to the store with A's own lease, as the mediator would.
        await using var mediator = await PickupEndpointHost.StartAsync(parkingStore: true);
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = await mediator.QueueAsync(recipient);

        var socketA = await mediator.ConnectAsync();
        using var disposeA = socketA;
        var onA = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(socketA, recipient, onA)).Delivery(thid: onA.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        var leaseOfA = mediator.Parking.LeaseRequests.Should().ContainSingle().Subject.Lease;
        await mediator.CloseAsync(socketA);

        var (overHttp, answer) = await mediator.FetchUntilDeliveredAsync(recipient, request => mediator.AskAsync(recipient, request));
        answer.Delivery(thid: overHttp.Id).Should().ContainSingle().Which.Id.Should().Be(queued.Id);

        var endedById = await mediator.Parking.ReleaseAsync(recipient.Did, new[] { queued.Id! }, leaseOfA.Id, default);
        var endedByOwner = await mediator.Parking.ReleaseLeasesAsync(leaseOfA.Owner!, default);

        var third = MessagePickup.CreateDeliveryRequest(recipient.Did, mediator.Mediator, 10);
        (await mediator.AskAsync(recipient, third)).Status(third.Id).MessageCount.Should().Be(1, "inside the HTTP lease the message is not delivered again");
        endedById.Should().Be(0, "A's lease is not on the record any more");
        endedByOwner.Should().Be(0, "A's session owns nothing any more");
        mediator.Store.MessageCount.Should().Be(1);
    }

    /// <summary>
    /// A <c>delivery-request</c> with <c>return_route: none</c> on a socket that is already a
    /// session: the mediator leases what is deliverable, the session does not take the push, and
    /// nothing is written back.
    /// </summary>
    private static async Task SendDeliveryRequestTheSessionWillNotAnswerAsync(PickupEndpointHost mediator, WebSocket socket, TestPeer peer)
    {
        var request = mediator.Prepare(MessagePickup.CreateDeliveryRequest(peer.Did, mediator.Mediator, 10), returnRoute: false);
        ReturnRouteHeaderWriter.Write(request, ReturnRouteMode.None, thread: null);
        await ReturnRouteTestHost.SendAsync(socket, await mediator.Host.AuthcryptAsync(peer, request));
    }

    // ---- malformed requests and logs --------------------------------------------------------

    [Fact]
    public async Task A_malformed_request_on_a_socket_writes_nothing_keeps_the_session_and_no_log_line_or_logged_exception_carries_a_value()
    {
        const string thread = "SENTINEL-thread-0123456789";
        const string unregistered = "did:example:SENTINEL-nobody-registered";
        await using var mediator = await PickupEndpointHost.StartAsync();
        var recipient = mediator.Recipient;
        await mediator.EnrolAsync(recipient);
        var queued = await mediator.QueueAsync(recipient);

        var (socket, _) = await mediator.OpenAsync(recipient);
        using var disposeSocket = socket;
        (await mediator.GoLiveAsync(socket, recipient)).LiveDelivery.Should().BeTrue();
        var pushed = await mediator.QueueAsync(recipient, expected: PickupDeliveryOutcome.PushedLive);
        (await mediator.ReceiveAsync(socket, recipient)).Delivery(thid: null).Should().ContainSingle().Which.Id.Should().Be(pushed.Id);

        var malformed = new[]
        {
            mediator.Raw(recipient, MessagePickup.DeliveryRequestType, new JsonObject { ["limit"] = -1, ["recipient_did"] = unregistered }, thread),
            mediator.Raw(recipient, MessagePickup.DeliveryRequestType, new JsonObject { ["limit"] = "SENTINEL-limit", ["recipient_did"] = recipient.Did }, thread),
            mediator.Raw(recipient, MessagePickup.MessagesReceivedType, new JsonObject { ["message_id_list"] = new JsonArray(pushed.Id, 7, "SENTINEL-id") }, thread),
            mediator.Raw(recipient, MessagePickup.LiveDeliveryChangeType, new JsonObject { ["live_delivery"] = "SENTINEL-yes" }, thread),
            mediator.Raw(recipient, MessagePickup.StatusRequestType, new JsonObject(), "SENTINEL-" + new string('t', 512)),
        };
        foreach (var request in malformed)
            await mediator.SendAsync(socket, recipient, request);

        // Nothing was written for any of them, the session is still there and still live, and
        // nothing was removed or leased: the next frame is the answer to this request…
        var status = await mediator.StatusAsync(socket, recipient);
        status.MessageCount.Should().Be(2);
        status.LiveDelivery.Should().BeTrue();
        // …and the queued message is still deliverable.
        var fetch = mediator.Raw(recipient, MessagePickup.DeliveryRequestType, new JsonObject { ["limit"] = 10 }, thread);
        (await mediator.AskAsync(socket, recipient, fetch)).Delivery(thid: thread).Should().ContainSingle().Which.Id.Should().Be(queued.Id);
        (await mediator.AcknowledgeAsync(socket, recipient, queued.Id!, pushed.Id!, "SENTINEL-unknown-id")).MessageCount.Should().Be(0);
        await mediator.CloseAsync(socket);

        var sensitive = mediator.Host.SensitiveValues.Append(queued.Id!).Append(pushed.Id!).Append("SENTINEL").ToArray();
        bool Leaks(string text) => sensitive.Any(v => text.Contains(v, StringComparison.Ordinal));

        mediator.Logs.OfPickup.Where(r => Leaks(r.Rendered)).Select(r => r.Rendered).Should().BeEmpty(
            "pickup log lines carry the id of the request, type names and counts — never a DID, a recipient_did, an attachment id or a thread id");
        var logged = mediator.Logs.Records.Where(r => r.Exception is not null).ToList();
        logged.Should().HaveCountGreaterThanOrEqualTo(malformed.Length, "the endpoint logs each malformed request with its exception");
        logged.Where(r => Leaks(r.Exception!.ToString())).Select(r => r.Exception!.Message).Should().BeEmpty(
            "a MalformedMessageException names the member and the rule, never a value");
        mediator.Logs.OfOthers.Where(r => r.Level >= LogLevel.Information && Leaks(r.Rendered)).Select(r => $"{r.Category}: {r.Text}").Should().BeEmpty(
            "no line of the host at Information or above carries one of these values either");
    }
}
