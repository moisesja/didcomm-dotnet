using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The message that does not fit even alone (FR-PICK-03, JC-19 in its final form): when a
/// <c>delivery</c> is over the byte budget once packed, the batch is trimmed from the end; when
/// one message is left and its delivery is still too large, that message — the head of the
/// batch — is neither removed nor given back. It keeps the request's lease, so it is passed over
/// until that lease ends (by time, or when the live session that owns it closes), and the same
/// request leases again and delivers what is queued behind it. It stays stored and counted until
/// it is delivered, expires or is purged by the host. One request does a bounded amount of this:
/// at most twice the batch maximum of lease rounds and of sends.
/// </summary>
public sealed class MessagePickupHandlerUndeliverableTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static HandlerLogEntry[] PassedOverLines(MessagePickupHandlerHarness h, int since)
        => h.Fixture.PickupLogs.Skip(since).Where(e => e.Formatted.Contains("even alone; it stays queued and is passed over", StringComparison.Ordinal)).ToArray();

    /// <summary>A session that finds every delivery carrying <paramref name="id"/> too large, however few others travel with it.</summary>
    private static Func<Message, int, ReturnRouteSendResult?> TooLargeWith(string id)
        => (message, _) => message.Attachments!.Any(a => a.Id == id) ? ReturnRouteSendResult.TooLarge : null;

    [Fact]
    public async Task The_message_passed_over_is_the_head_of_the_batch_it_stays_stored_and_counted_and_the_same_request_delivers_what_is_behind_it()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3), await h.QueueAsync(h.Alice, 40, 4) };
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(ids[0]);
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        // Trimmed from the end: the last one out is the head of the batch, and that is the one
        // passed over. Then the request leases again and delivers the other three.
        session.Attempts.Select(a => string.Join(",", a.Message.Attachments!.Select(x => Array.IndexOf(ids, x.Id))) + " " + a.Result).Should().Equal(
            "0,1,2,3 TooLarge", "0,1,2 TooLarge", "0,1 TooLarge", "0 TooLarge", "1,2,3 Sent");
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(request, h.Mediator, h.Alice, null, ids.Skip(1).ToArray());
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[3], ids[2], ids[1]);
        h.Store.Acknowledged.Should().BeEmpty();
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync", "ReleaseAsync", "LeaseAsync");
        var lease = h.Store.LeaseRequests.Select(r => r.Lease).Distinct().Should().ContainSingle("both rounds are under the request's one lease").Subject;
        h.Store.ReleasedLeaseIds.Should().OnlyContain(id => id == lease.Id);
        PassedOverLines(h, logged).Should().ContainSingle().Which.State["Bytes"].Should().Be(10);

        // Still stored and still counted.
        h.Store.Inner.MessageCount.Should().Be(4);
        var status = h.StatusRequest(h.Alice);
        (await h.SendAsync(status, session)).ShouldBeStatus(status, h.Mediator, h.Alice, 4, 100, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));

        // It is exactly as the request leased it: no owner (a request-scoped session), the
        // request's lease id and nobody else's.
        lease.Owner.Should().BeNull();
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("one is passed over, three are delivered and waiting for their messages-received");
        (await h.Store.Inner.ReleaseAsync(h.Alice, new[] { ids[0] }, "not-a-lease-of-anyone", default)).Should().Be(0);
        (await h.Store.Inner.ReleaseAsync(h.Alice, new[] { ids[0] }, lease.Id, default)).Should().Be(1);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids[0]);
    }

    [Theory]
    [InlineData(0, "delivery:1,2")]
    [InlineData(1, "delivery:0 | delivery:2")]
    [InlineData(2, "delivery:0,1 | status:1")]
    public async Task A_message_that_does_not_fit_even_alone_does_not_block_what_is_queued_around_it(int position, string expected)
    {
        // Three messages; the one at `position` makes every delivery that contains it too large.
        // The recipient asks, acknowledges what it got, and asks again — the clock does not move.
        // Whatever the position: the other two arrive, in order, with no request wasted on a
        // status while something deliverable is waiting; the one that does not fit never arrives,
        // is passed over by exactly one request, and is still stored at the end.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var stuck = ids[position];
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(stuck);
        var logged = h.Fixture.PickupLogs.Count;

        var steps = new List<string>();
        var delivered = new List<string>();
        for (var round = 0; round < 6 && !(delivered.Count == 2 && PassedOverLines(h, logged).Length > 0); round++)
        {
            var sentBefore = session.Sent.Count;
            var outcome = await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session);
            if (session.Sent.Count > sentBefore)
            {
                outcome.ShouldBeNoReply();
                MessagePickup.TryParseDelivery(session.Sent.Last(), out var batch, out _).Should().BeTrue();
                steps.Add("delivery:" + string.Join(",", batch!.Select(m => Array.IndexOf(ids, m.Id))));
                delivered.AddRange(batch!.Select(m => m.Id));
                (await h.SendAsync(h.MessagesReceived(h.Alice, batch!.Select(m => m.Id).ToArray()), session)).Status();
            }
            else
            {
                steps.Add("status:" + outcome.Status().MessageCount);
            }
        }

        string.Join(" | ", steps).Should().Be(expected);
        delivered.Should().Equal(ids.Where(id => id != stuck));
        session.Sent.Should().OnlyContain(m => m.Attachments!.All(a => a.Id != stuck), "it is never delivered");
        h.Store.Acknowledged.SelectMany(a => a.Ids).Should().NotContain(stuck, "and never removed");
        h.Store.Inner.MessageCount.Should().Be(1);
        PassedOverLines(h, logged).Should().ContainSingle().Which.State["Bytes"].Should().Be(10 * (position + 1));

        // One more request, the clock still not moved: there is nothing to try, and nothing is said.
        var attempts = session.Attempts.Count;
        h.ResetTraces();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(1);
        session.Attempts.Should().HaveCount(attempts);
        h.StoreTrace.Should().Equal("LeaseAsync", "GetStatusAsync");
        PassedOverLines(h, logged).Should().ContainSingle();

        // When the lease ends it is tried again, alone, and passed over again.
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        h.ResetTraces();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(1);
        session.Attempts.Should().HaveCount(attempts + 1);
        session.Attempts.Last().Message.Attachments!.Select(a => a.Id).Should().Equal(stuck);
        h.StoreTrace.Should().Equal("LeaseAsync", "LeaseAsync", "GetStatusAsync");
        PassedOverLines(h, logged).Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
    }

    [Fact]
    public async Task A_recipient_that_polls_gets_what_is_queued_behind_a_message_that_does_not_fit_with_its_very_first_request()
    {
        // The reason a message that does not fit is passed over at all is that "left in place it
        // would be offered first for ever and nothing behind it would ever be delivered". That has
        // to hold for a recipient that polls, too: one delivery-request per poll, a status answer
        // read as the protocol page defines it ("no messages are available to be sent"), the next
        // poll after longer than LeaseTimeout — so every poll finds the lease ended and the same
        // message first in line again. The request that meets it looks further itself.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var stuck = await h.QueueAsync(h.Alice, 50, 1);
        var behind = new[] { await h.QueueAsync(h.Alice, 10, 2), await h.QueueAsync(h.Alice, 20, 3) };
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(stuck);

        // The first poll.
        (await h.Fixture.FetchAsync(h.Alice, h.Mediator, session, 10))!.Value.Messages.Select(m => m.Id).Should().Equal(behind);
        (await h.SendAsync(h.MessagesReceived(h.Alice, behind), session)).Status().MessageCount.Should().Be(1);

        // Every later poll, and a message that arrives between two of them.
        for (var poll = 0; poll < 3; poll++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            var arrival = await h.QueueAsync(h.Alice, 5, (byte)(10 + poll));
            (await h.Fixture.FetchAsync(h.Alice, h.Mediator, session, 10))!.Value.Messages.Select(m => m.Id).Should().Equal(new[] { arrival }, "the message that does not fit is first in line again, and the same request delivers what is behind it");
            (await h.SendAsync(h.MessagesReceived(h.Alice, new[] { arrival }), session)).Status().MessageCount.Should().Be(1);
        }

        session.Sent.Should().OnlyContain(m => m.Attachments!.All(a => a.Id != stuck));
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task A_message_tried_alone_that_is_too_large_costs_no_release_and_no_removal(int limit)
    {
        // limit 1 against a queue of three, or a queue of one: either way the first answer already
        // is the verdict on that message alone. It keeps the lease; the request leases again and
        // comes to the next one, until a round finds nothing.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string> { await h.QueueAsync(h.Alice, 10, 1) };
        if (limit == 1)
        {
            ids.Add(await h.QueueAsync(h.Alice, 20, 2));
            ids.Add(await h.QueueAsync(h.Alice, 30, 3));
        }
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, limit);
        var outcome = await h.SendAsync(request, session);

        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, ids.Count, limit == 1 ? 60 : 10, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        session.Attempts.Select(a => string.Join(",", a.Message.Attachments!.Select(x => x.Id))).Should().Equal(ids, "each is tried alone, in order");
        h.StoreTrace.Should().Equal(Enumerable.Repeat("LeaseAsync", ids.Count + 1).Append("GetStatusAsync"));
        h.Store.Released.Should().BeEmpty("a message that does not fit alone keeps the lease");
        h.Store.Acknowledged.Should().BeEmpty("and is not removed");
        h.Store.Inner.MessageCount.Should().Be(ids.Count);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_passed_over_line_is_rate_limited_and_carries_the_request_id_the_size_and_the_count_only()
    {
        // 1001 messages, none of which fits alone, asked for one at a time (limit 1). A request
        // comes to 20 of them (twice the batch maximum of ten) before it stops, so 51 requests come
        // to all of them. Any enrolled peer can cause the line, so: the first at Warning, then
        // Debug, the 1000th at Warning again. And every one of them is still stored at the end.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxQueueMessages = 1100);
        await h.EnrolAsync(h.Alice, h.Alice);
        var now = h.Clock.Now;
        for (var i = 0; i < 1001; i++)
        {
            var record = new QueuedMessage("queued-secret-" + i, h.Alice, h.Alice, PickupFixture.Payload(1 + i % 7), now);
            (await h.Store.Inner.EnqueueAsync(record, null, now, default)).Should().Be(EnqueueOutcome.Enqueued);
        }
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var request = h.DeliveryRequest(h.Alice, 1);
        var received = await h.Fixture.ReceiveAsync(request, h.Mediator);
        _ = h.Fixture.Queue;
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        for (var i = 0; i < 51; i++)
        {
            var outcome = await h.Fixture.Host.Dispatcher.DispatchAsync(received, h.Fixture.Host.Client, h.Fixture.Host.Options, Task.CompletedTask, session, CancellationToken.None);
            outcome.Status().MessageCount.Should().Be(1001);
            session.Attempts.Should().HaveCount(Math.Min(20 * (i + 1), 1001));
        }

        h.Store.Inner.MessageCount.Should().Be(1001);
        h.Store.Acknowledged.Should().BeEmpty();
        h.Store.Released.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("each of them is held by the lease of the request that tried it");
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Should().HaveCount(1001).And.OnlyContain(e => e.Formatted.Contains("even alone; it stays queued and is passed over until this request's lease ends"));
        lines.Count(e => e.Level == LogLevel.Warning).Should().Be(2, "the 1st and the 1000th");
        lines.Count(e => e.Level == LogLevel.Debug).Should().Be(999);
        lines[0].Level.Should().Be(LogLevel.Warning);
        lines[1].Level.Should().Be(LogLevel.Debug);
        lines[998].Level.Should().Be(LogLevel.Debug);
        lines[999].Level.Should().Be(LogLevel.Warning);
        lines[1000].Level.Should().Be(LogLevel.Debug);
        lines.Select(e => e.State["Count"]).Should().Equal(Enumerable.Range(1, 1001).Select(n => (object?)(long)n));
        lines.Select(e => e.State["Bytes"]).Should().Equal(Enumerable.Range(0, 1001).Select(i => (object?)(1 + i % 7)));
        lines.Should().OnlyContain(e => Equals(e.State["MessageId"], request.Id) && e.State.Count == 4 && e.Exception == null);
        lines.Should().OnlyContain(e => !e.Everything.Contains("did:") && !e.Everything.Contains("queued-secret-"));
    }

    // ---- bounded work per request ---------------------------------------------------------------

    [Fact]
    public async Task One_request_makes_at_most_twice_the_batch_maximum_of_sends_then_gives_back_what_it_holds_and_the_next_request_continues()
    {
        // A batch of three, ten messages, none of which fits even alone. A send is a full pack of up
        // to max_delivery_bytes, so one request makes six of them and no more.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 3);
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
            ids.Add(await h.QueueAsync(h.Alice, 10 + i, (byte)i));
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);
        var logged = h.Fixture.PickupLogs.Count;
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeStatus(request, h.Mediator, h.Alice, 10, 145, live: false, times: times);

        // Two full trims — #0 and #1 passed over — and the third batch is leased when the send
        // budget is found spent: it goes back whole, under the request's lease, and the answer is
        // the status.
        session.Attempts.Select(a => string.Join(",", a.Message.Attachments!.Select(x => ids.IndexOf(x.Id!)))).Should().Equal("0,1,2", "0,1", "0", "1,2,3", "1,2", "1");
        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "ReleaseAsync", "LeaseAsync", "ReleaseAsync", "ReleaseAsync", "LeaseAsync", "ReleaseAsync", "GetStatusAsync");
        h.Store.Released.Select(r => string.Join(",", r.Ids.Select(id => ids.IndexOf(id)))).Should().Equal("2", "1", "3", "2", "2,3,4");
        h.Store.ReleasedLeaseIds.Should().OnlyContain(id => id == h.Store.LeaseRequests.First().Lease.Id);
        h.Store.LeaseRequests.Select(r => r.Lease.Id).Distinct().Should().ContainSingle();
        h.Store.LeaseRequests.Should().OnlyContain(r => r.MaxMessages == 3);
        h.Store.Acknowledged.Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids.Skip(2), "what the request still held when it stopped is deliverable at once");
        PassedOverLines(h, logged).Select(e => e.State["Bytes"]).Should().Equal(10, 11);

        // The next requests continue where it stopped: two more each, six sends each. The fifth
        // finds only two left (three sends) and a round with nothing; the sixth has nothing to try.
        for (var n = 2; n <= 4; n++)
        {
            (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(10);
            session.Attempts.Should().HaveCount(6 * n);
            (await h.DeliverableAsync(h.Alice)).Should().Equal(ids.Skip(2 * n));
        }
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(10);
        session.Attempts.Should().HaveCount(27);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(10);
        session.Attempts.Should().HaveCount(27);
        session.Sent.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(10);
        PassedOverLines(h, logged).Should().HaveCount(10);
    }

    [Fact]
    public async Task One_request_makes_at_most_twice_the_batch_maximum_of_lease_rounds_and_the_next_request_continues()
    {
        // A batch maximum of two, so four lease rounds. Six leftovers of a DID that is gone, asked
        // for one at a time, then a message of a registered DID. A round that comes to nothing —
        // its one record was a leftover, removed by id — costs no send, so the bound that ends the
        // request is the one on rounds.
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBatch = 2);
        var gone = await h.NewDidAsync();
        await h.EnrolAsync(h.Alice, h.Alice, gone);
        var stale = new List<string>();
        for (var i = 0; i < 6; i++)
            stale.Add(await h.QueueAsync(gone, 10, (byte)i));
        var kept = await h.QueueAsync(h.Alice, 30, 9);
        await h.Mediation.Inner.UpdateRecipientsAsync(h.Alice, new[] { new RecipientUpdate(gone, RecipientUpdateAction.Remove) }, default);
        var session = h.Http(h.Alice);
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();

        var first = h.DeliveryRequest(h.Alice, 1);
        (await h.SendAsync(first, session)).ShouldBeStatus(first, h.Mediator, h.Alice, 3, 50, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));

        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync", "LeaseAsync", "AcknowledgeAsync", "LeaseAsync", "AcknowledgeAsync", "LeaseAsync", "AcknowledgeAsync", "GetStatusAsync");
        h.Store.Acknowledged.Select(a => string.Join(",", a.Ids)).Should().Equal(stale.Take(4), "each round removes exactly the one leftover it leased");
        session.Attempts.Should().BeEmpty();
        h.Store.Released.Should().BeEmpty();
        h.Store.PurgedRecipients.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(3);

        // The next request comes to the last two leftovers and then to the message behind them.
        h.ResetTraces();
        var second = h.DeliveryRequest(h.Alice, 1);
        (await h.SendAsync(second, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(second, h.Mediator, h.Alice, null, new[] { kept });
        h.StoreTrace.Should().Equal("LeaseAsync", "AcknowledgeAsync", "LeaseAsync", "AcknowledgeAsync", "LeaseAsync");
        h.Store.Acknowledged.Select(a => string.Join(",", a.Ids)).Should().Equal(stale.Skip(4));
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    // ---- the lease period ---------------------------------------------------------------------

    [Fact]
    public async Task A_message_that_does_not_fit_is_passed_over_for_one_lease_period_at_a_time_and_tried_once_when_each_ends()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var stuck = await h.QueueAsync(h.Alice, 50, 1);
        var behind = new[] { await h.QueueAsync(h.Alice, 10, 2), await h.QueueAsync(h.Alice, 20, 3) };
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(stuck);
        var logged = h.Fixture.PickupLogs.Count;
        h.ResetTraces();
        int Tries() => session.Attempts.Count(a => a.Message.Attachments!.Count == 1 && a.Message.Attachments[0].Id == stuck);

        // t = 0: tried (after the trim, alone), does not fit, keeps this request's lease — and the
        // same request delivers what is behind it.
        var first = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(first, session)).ShouldBeNoReply();
        session.Sent.Should().ContainSingle().Which.ShouldBeDelivery(first, h.Mediator, h.Alice, null, behind);
        var firstLease = h.Store.LeaseRequests.Last().Lease;
        Tries().Should().Be(1);
        (await h.SendAsync(h.MessagesReceived(h.Alice, behind), session)).Status().MessageCount.Should().Be(1);

        // t = 20 s: a message that arrives now is delivered; the one that does not fit is passed over.
        h.Clock.Advance(TimeSpan.FromSeconds(20));
        var arrival = await h.QueueAsync(h.Alice, 30, 4);
        (await h.Fixture.FetchAsync(h.Alice, h.Mediator, session, 10))!.Value.Messages.Select(m => m.Id).Should().Equal(arrival);
        (await h.SendAsync(h.MessagesReceived(h.Alice, new[] { arrival }), session)).Status().MessageCount.Should().Be(1);
        Tries().Should().Be(1);

        // One tick before the lease ends (t = 60 s): still passed over — not tried, nothing logged.
        h.Clock.Now = h.Start + TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1);
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(1);
        Tries().Should().Be(1);

        // t = 60 s: the lease has ended, the message is where it always was, and it is tried again
        // — once — and now held by the lease of the request that tried it.
        h.Clock.Now = h.Start + TimeSpan.FromSeconds(60);
        var second = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(second, session)).Status().MessageCount.Should().Be(1);
        var secondLease = h.Store.LeaseRequests.Last().Lease;
        Tries().Should().Be(2);
        secondLease.Id.Should().NotBe(firstLease.Id);
        secondLease.ExpiresAt.Should().Be(h.Start + TimeSpan.FromSeconds(120));
        (await h.Store.Inner.ReleaseAsync(h.Alice, new[] { stuck }, firstLease.Id, default)).Should().Be(0, "the first request's lease is over; giving back under it finds nothing");
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(1);
        Tries().Should().Be(2, "one try per lease period");

        // t = 120 s − 1 tick: passed over. t = 120 s: the third try.
        h.Clock.Now = h.Start + TimeSpan.FromSeconds(120) - TimeSpan.FromTicks(1);
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).Status().MessageCount.Should().Be(1);
        Tries().Should().Be(2);
        h.Clock.Now = h.Start + TimeSpan.FromSeconds(120);
        var third = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(third, session)).Status().MessageCount.Should().Be(1);
        Tries().Should().Be(3);

        // Three tries, three lines; it was never delivered, never removed, and is still stored.
        var lines = PassedOverLines(h, logged);
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Debug);
        lines.Select(e => e.State["MessageId"]).Should().Equal(first.Id, second.Id, third.Id);
        lines.Select(e => e.State["Count"]).Should().Equal(1L, 2L, 3L);
        session.Sent.Should().OnlyContain(m => m.Attachments!.All(a => a.Id != stuck));
        h.Store.Acknowledged.SelectMany(a => a.Ids).Should().NotContain(stuck);
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_passed_over_under_a_live_sessions_lease_is_tried_again_as_soon_as_that_session_closes()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var stuck = await h.QueueAsync(h.Alice, 50, 1);
        var behind = await h.QueueAsync(h.Alice, 10, 2);
        var socket = h.Live(h.Alice);
        var other = h.Http(h.Alice);
        socket.OnSend = TooLargeWith(stuck);
        other.OnSend = TooLargeWith(stuck);
        // The close of a live session ends its leases in the background: wait for exactly that.
        var ownerReleased = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.OnReleaseLeases = async (owner, ct) =>
        {
            var ended = await h.Store.Inner.ReleaseLeasesAsync(owner, ct);
            ownerReleased.TrySetResult(ended);
            return ended;
        };
        h.ResetTraces();

        // Over the socket: does not fit, keeps the request's lease — which the socket owns — and
        // the same request delivers what is behind it over the socket.
        var first = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(first, socket)).ShouldBeNoReply();
        socket.Sent.Should().ContainSingle().Which.ShouldBeDelivery(first, h.Mediator, h.Alice, null, new[] { behind });
        var socketLease = h.Store.LeaseRequests.Last().Lease;
        socketLease.Owner.Should().NotBeNullOrEmpty();
        (await h.SendAsync(h.MessagesReceived(h.Alice, new[] { behind }), other)).Status().MessageCount.Should().Be(1);

        // On another connection, the clock not moved: there is nothing to try.
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), other)).Status().MessageCount.Should().Be(1);
        other.Attempts.Should().BeEmpty();

        // The socket closes: its leases end, this one with them.
        socket.Close();
        (await ownerReleased.Task.WaitAsync(Deadline)).Should().Be(1);
        h.Store.ReleasedOwners.Should().Equal(socketLease.Owner!);

        // The very next request — the clock still has not moved — tries it again, alone, and passes
        // it over under its own lease, which no session owns.
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), other)).Status().MessageCount.Should().Be(1);
        var retried = other.Attempts.Should().ContainSingle().Subject;
        retried.Message.Attachments!.Select(a => a.Id).Should().Equal(stuck);
        retried.Result.Should().Be(ReturnRouteSendResult.TooLarge);
        var laterLease = h.Store.LeaseRequests.Last().Lease;
        laterLease.Owner.Should().BeNull();
        laterLease.Id.Should().NotBe(socketLease.Id);
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), other)).Status().MessageCount.Should().Be(1);
        other.Attempts.Should().ContainSingle("passed over again until that lease ends");

        h.Store.Acknowledged.SelectMany(a => a.Ids).Should().NotContain(stuck);
        h.Store.Inner.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_too_large_for_one_requests_framing_is_delivered_later_by_a_request_whose_framing_lets_it_fit()
    {
        // Real envelopes here (four packs of about 30 KiB — the slowest test of the class), because
        // the point is the measured size: the thread id (up to 512 characters) and the filter (up to
        // 2048) that a delivery echoes are part of what the session weighs. The record is larger
        // than this mediator would admit today — one held from an older, larger budget (JC-18).
        const int size = 30_000;
        const string id = "held-from-an-older-budget";
        var longDid = "did:example:" + new string('a', 2048 - "did:example:".Length);
        var longThread = new string('t', 512);

        async Task<(MessagePickupHandlerHarness H, PickupFakeSession Session, byte[] Payload)> WorldAsync(int? maxDeliveryBytes)
        {
            var world = await MessagePickupHandlerHarness.CreateAsync(o => o.MaxDeliveryBytes = maxDeliveryBytes);
            await world.EnrolAsync(world.Alice, world.Alice, longDid);
            var payload = PickupFixture.Payload(size, 7);
            var now = world.Clock.Now;
            (await world.Store.Inner.EnqueueAsync(new QueuedMessage(id, world.Alice, longDid, payload, now), null, now, default)).Should().Be(EnqueueOutcome.Enqueued);
            return (world, world.Fixture.Session(world.Alice, world.Mediator, live: false, register: false, pack: true), payload);
        }

        // What the two deliveries weigh, measured under the default budget.
        int plainPacked, framedPacked;
        {
            var (calibration, session, _) = await WorldAsync(null);
            await using var disposal = calibration;
            (await calibration.SendAsync(calibration.DeliveryRequest(calibration.Alice, 10), session)).ShouldBeNoReply();
            plainPacked = session.Attempts.Last().PackedBytes!.Value;
            calibration.Clock.Advance(TimeSpan.FromSeconds(60));
            (await calibration.SendAsync(calibration.DeliveryRequest(calibration.Alice, 10, longDid, longThread), session)).ShouldBeNoReply();
            framedPacked = session.Attempts.Last().PackedBytes!.Value;
        }
        framedPacked.Should().BeGreaterThan(plainPacked + 2000, "a long thread id and a long filter make the same delivery larger");
        var budget = (plainPacked + framedPacked) / 2;

        // A mediator whose budget lies between the two.
        var (h, http, expectedPayload) = await WorldAsync(budget);
        await using var harness = h;
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);
        h.ResetTraces();

        // The request with the long thread and the filter: its delivery of this one message is too large.
        var framed = h.DeliveryRequest(h.Alice, 10, longDid, longThread);
        (await h.SendAsync(framed, http)).ShouldBeStatus(framed, h.Mediator, h.Alice, 1, size, live: false, recipientDid: longDid, times: times);
        var refused = http.Attempts.Should().ContainSingle().Subject;
        refused.Result.Should().Be(ReturnRouteSendResult.TooLarge);
        refused.MaxPackedBytes.Should().Be(budget);
        refused.PackedBytes.Should().BeGreaterThan(budget);
        h.Store.Acknowledged.Should().BeEmpty("the verdict belongs to this request's framing, not to the message");
        h.Store.Inner.MessageCount.Should().Be(1);

        // A plain request while that lease runs: the message is passed over.
        var early = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(early, http)).ShouldBeStatus(early, h.Mediator, h.Alice, 1, size, live: false, times: times);
        http.Attempts.Should().ContainSingle();

        // The plain request after the lease has ended: delivered, byte for byte.
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        var plain = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(plain, http)).ShouldBeNoReply();
        var delivery = http.Sent.Should().ContainSingle().Subject;
        delivery.Thid.Should().Be(plain.Id);
        MessagePickup.TryParseDelivery(delivery, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().BeNull();
        var message = messages!.Should().ContainSingle().Subject;
        message.Id.Should().Be(id);
        message.Payload.ToArray().Should().Equal(expectedPayload);
        http.Attempts.Last().PackedBytes.Should().BeLessThanOrEqualTo(budget);
    }

    // ---- what a passed-over message costs, and who can clear it ---------------------------------

    [Fact]
    public async Task A_message_that_is_passed_over_still_counts_in_status_and_against_the_grants_quota()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(o =>
        {
            o.MaxQueueMessages = 3;
            o.MaxQueueBytes = 100;
        });
        await h.EnrolAsync(h.Alice, h.Alice);
        var stuck = await h.QueueAsync(h.Alice, 60, 1);
        var behind = new[] { await h.QueueAsync(h.Alice, 10, 2), await h.QueueAsync(h.Alice, 10, 3) };
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(stuck);
        var t0 = h.Start.ToUnixTimeSeconds();
        var times = new MessagePickupHandlerTimes(0, t0, t0);
        async Task<PickupDeliveryOutcome> ArrivesAsync(int size) => (await h.Fixture.Queue.DeliverAsync(h.Alice, PickupFixture.Payload(size, 9))).Outcome;

        // Passed over, the other two delivered by the same request and acknowledged: one message,
        // 60 bytes, still held.
        (await h.Fixture.FetchAsync(h.Alice, h.Mediator, session, 10))!.Value.Messages.Select(m => m.Id).Should().Equal(behind);
        var ack = h.MessagesReceived(h.Alice, behind);
        (await h.SendAsync(ack, session)).ShouldBeStatus(ack, h.Mediator, h.Alice, 1, 60, live: false, times: times);

        // The grant has room for two more messages and 40 more bytes — not three, and not 100.
        (await ArrivesAsync(30)).Should().Be(PickupDeliveryOutcome.Queued);
        (await ArrivesAsync(20)).Should().Be(PickupDeliveryOutcome.QueueFull, "60 + 30 + 20 bytes is over max_queue_bytes");
        (await ArrivesAsync(10)).Should().Be(PickupDeliveryOutcome.Queued);
        (await ArrivesAsync(1)).Should().Be(PickupDeliveryOutcome.QueueFull, "the passed-over message is one of the three max_queue_messages allows");
        var status = h.StatusRequest(h.Alice);
        (await h.SendAsync(status, session)).ShouldBeStatus(status, h.Mediator, h.Alice, 3, 100, live: false, times: times);
    }

    [Theory]
    [InlineData("IMessageStore.PurgeRecipientAsync")]
    [InlineData("IMessageStore.PurgeGrantAsync")]
    [InlineData("MessagePickupQueue.RemoveAsync")]
    [InlineData("the message expires")]
    public async Task The_recipient_cannot_name_a_message_it_was_never_delivered_but_the_host_or_its_expiry_clears_it(string clearedBy)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var expires = clearedBy == "the message expires" ? new PickupDeliveryOptions(ExpiresTime: h.Clock.Now.ToUnixTimeSeconds() + 3600) : null;
        var stuck = await h.Fixture.QueueAsync(h.Alice, PickupFixture.Payload(50, 1), expires);
        var behind = await h.QueueAsync(h.Alice, 10, 2);
        var session = h.Http(h.Alice);
        session.OnSend = TooLargeWith(stuck);
        var replies = new List<Message>();
        async Task<int?> AskAsync(Message request)
        {
            var outcome = await h.SendAsync(request, session);
            if (outcome.Reply is null)
                return null;
            replies.Add(outcome.Reply);
            return outcome.Status().MessageCount;
        }

        (await AskAsync(h.DeliveryRequest(h.Alice, 10))).Should().BeNull("the message behind it is delivered by the request that passed it over");
        session.Sent.Should().ContainSingle();

        // The recipient acknowledges everything it was ever delivered, and guesses: the message
        // stays. Its id was never on the wire — not in a delivery, not in a status, not in a log.
        (await AskAsync(h.MessagesReceived(h.Alice, new[] { behind, "guess-1", Guid.NewGuid().ToString() }))).Should().Be(1);
        (await AskAsync(h.DeliveryRequest(h.Alice, 10))).Should().Be(1);
        foreach (var onTheWire in session.Sent.Concat(replies))
            JsonSerializer.Serialize(onTheWire, DidCommJson.Default).Should().NotContain(stuck);
        h.Fixture.PickupLogs.Should().OnlyContain(e => !e.Everything.Contains(stuck));
        h.Store.Inner.MessageCount.Should().Be(1);

        // The host can clear it; so does its own expiry.
        switch (clearedBy)
        {
            case "IMessageStore.PurgeRecipientAsync":
                (await h.Fixture.Store.PurgeRecipientAsync(h.Alice, h.Alice, default)).Should().Be(1);
                break;
            case "IMessageStore.PurgeGrantAsync":
                (await h.Fixture.Store.PurgeGrantAsync(h.Alice, default)).Should().Be(1);
                break;
            case "MessagePickupQueue.RemoveAsync":
                (await h.Fixture.Queue.RemoveAsync(h.Alice, stuck)).Should().BeTrue();
                break;
            default:
                h.Clock.Advance(TimeSpan.FromHours(2));
                break;
        }

        (await AskAsync(h.StatusRequest(h.Alice))).Should().Be(0);
        h.Store.Inner.MessageCount.Should().Be(0);
        session.Sent.Should().OnlyContain(m => m.Attachments!.All(a => a.Id != stuck), "it was never delivered");
    }

    // ---- nothing is left to fail at that step ---------------------------------------------------

    [Theory]
    [InlineData("IMessageStore.AcknowledgeAsync throws")]
    [InlineData("IMessageStore.ReleaseAsync throws")]
    [InlineData("IMessageStore.PurgeRecipientAsync throws")]
    [InlineData("the logging provider throws")]
    public async Task Passing_a_message_over_calls_no_store_method_that_changes_anything_so_none_of_them_can_make_it_fail(string broken)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync(throwingLogger: broken == "the logging provider throws");
        await h.EnrolAsync(h.Alice, h.Alice);
        var id = await h.QueueAsync(h.Alice, 10);
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        h.Store.OnAcknowledge = broken.Contains("AcknowledgeAsync", StringComparison.Ordinal) ? (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) : null;
        h.Store.OnRelease = broken.Contains("ReleaseAsync", StringComparison.Ordinal) ? (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) : null;
        h.Store.OnPurgeRecipient = broken.Contains("PurgeRecipientAsync", StringComparison.Ordinal) ? (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) : null;
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Alice, 10);
        var outcome = await h.SendAsync(request, session);

        // Answered exactly as with everything healthy: the status, and the message under its lease.
        // The only store calls are the lease rounds and the status read.
        outcome.ShouldBeStatus(request, h.Mediator, h.Alice, 1, 10, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));
        h.StoreTrace.Should().Equal("LeaseAsync", "LeaseAsync", "GetStatusAsync");
        h.Store.Inner.MessageCount.Should().Be(1);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        (await h.DeliverableAsync(h.Alice)).Should().Equal(id);
    }

    [Fact]
    public async Task When_the_status_cannot_be_read_after_messages_were_passed_over_there_is_no_reply_and_nothing_else_changes()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        h.Store.OnGetStatus = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker + " " + h.Alice);
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var request = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(request, session)).ShouldBeNoReply();

        h.StoreTrace.Should().Equal("LeaseAsync", "ReleaseAsync", "LeaseAsync", "LeaseAsync", "GetStatusAsync");
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[1]);
        h.Store.Acknowledged.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(2);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("both keep the lease whether or not the status could be sent");

        // Three lines: two messages were passed over, then the request could not be answered.
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Error);
        lines[2].State["ExceptionType"].Should().Be(nameof(HandlerBoomException));
        lines.Should().OnlyContain(e => Equals(e.State["MessageId"], request.Id) && e.Exception == null && !e.Everything.Contains("did:") && !e.Everything.Contains(HandlerBoomException.Marker));
    }

    [Theory]
    [InlineData("the token is cancelled as the session gives its verdict")]
    [InlineData("the store throws its own exception as the token is cancelled")]
    public async Task When_the_dispatch_token_is_cancelled_while_a_message_is_passed_over_only_cancellation_leaves_and_nothing_is_removed(string how)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var session = h.Http(h.Alice);
        using var cts = new CancellationTokenSource();
        var atTheVerdict = how.StartsWith("the token is cancelled", StringComparison.Ordinal);
        if (atTheVerdict)
        {
            // The verdict on the first message alone arrives together with the cancellation; the
            // real store then refuses the next lease round under a cancelled token.
            session.OnSend = (message, _) =>
            {
                if (message.Attachments!.Count == 1)
                    cts.Cancel();
                return ReturnRouteSendResult.TooLarge;
            };
        }
        else
        {
            // Both messages are passed over; the status read notices the cancellation and fails
            // with its own exception and its own text.
            session.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
            h.Store.OnGetStatus = (_, _, _, _) =>
            {
                cts.Cancel();
                throw new HandlerBoomException(HandlerBoomException.Marker + " " + h.Alice);
            };
        }
        h.ResetTraces();
        var logged = h.Fixture.PickupLogs.Count;

        var thrown = await Record.ExceptionAsync(() => h.SendAsync(h.DeliveryRequest(h.Alice, 10), session, ct: cts.Token));

        thrown.Should().BeOfType<OperationCanceledException>("only cancellation leaves a cancelled dispatch");
        thrown!.Message.Should().NotContain(HandlerBoomException.Marker).And.NotContain("did:");
        thrown.InnerException.Should().BeNull();
        ((OperationCanceledException)thrown).CancellationToken.Should().Be(cts.Token);

        // Exactly what an uncancelled request would have left behind up to that point: the trimmed
        // message given back, what did not fit under its lease, nothing removed.
        session.Sent.Should().BeEmpty();
        h.StoreTrace.Should().Equal(atTheVerdict
            ? new[] { "LeaseAsync", "ReleaseAsync", "LeaseAsync" }
            : new[] { "LeaseAsync", "ReleaseAsync", "LeaseAsync", "LeaseAsync", "GetStatusAsync" });
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(ids[1]);
        h.Store.Acknowledged.Should().BeEmpty();
        h.Store.Inner.MessageCount.Should().Be(2);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(atTheVerdict ? new[] { ids[1] } : Array.Empty<string>());
        var lines = h.Fixture.PickupLogs.Skip(logged).ToArray();
        lines.Select(e => e.Level).Should().Equal(atTheVerdict ? new[] { LogLevel.Warning } : new[] { LogLevel.Warning, LogLevel.Debug },
            "the messages were passed over before the cancellation was noticed; the cancellation itself is not a fault to log");
        lines.Should().OnlyContain(e => !e.Everything.Contains(HandlerBoomException.Marker) && !e.Everything.Contains("did:"));
    }

    [Fact]
    public async Task Passing_a_message_over_touches_the_requesting_peers_own_record_only()
    {
        // The same message id under two grants: Bob's record keeps Bob's request's lease, Alice's
        // is neither leased nor counted for Bob.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        await h.EnrolAsync(h.Bob, h.Bob);
        var now = h.Clock.Now;
        await h.Store.Inner.EnqueueAsync(new QueuedMessage("same-id", h.Alice, h.Alice, PickupFixture.Payload(11, 1), now), null, now, default);
        await h.Store.Inner.EnqueueAsync(new QueuedMessage("same-id", h.Bob, h.Bob, PickupFixture.Payload(22, 2), now), null, now, default);
        var bobSession = h.Http(h.Bob);
        bobSession.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;
        var t0 = h.Start.ToUnixTimeSeconds();
        h.ResetTraces();

        var request = h.DeliveryRequest(h.Bob, 10);
        (await h.SendAsync(request, bobSession)).ShouldBeStatus(request, h.Mediator, h.Bob, 1, 22, live: false, times: new MessagePickupHandlerTimes(0, t0, t0));

        h.Store.Acknowledged.Should().BeEmpty();
        h.Store.LeaseRequests.Should().HaveCount(2).And.OnlyContain(r => r.GrantPeerDid == h.Bob);
        (await h.StoredAsync(h.Bob)).Should().Be(1);
        (await h.DeliverableAsync(h.Bob)).Should().BeEmpty();
        (await h.DeliverableAsync(h.Alice)).Should().Equal("same-id");
    }
}
