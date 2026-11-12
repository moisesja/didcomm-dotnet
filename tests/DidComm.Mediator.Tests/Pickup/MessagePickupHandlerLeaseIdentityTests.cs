using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// A lease has an identity (FR-PICK-04): every <c>delivery-request</c> takes its messages under a
/// lease of its own, and whatever it gives back — in a trim, after a send that was not taken, in
/// the clean-up of a fault — it gives back under that lease and no other. So a request whose lease
/// has run out in the meantime cannot make a message deliverable again that a later request has
/// delivered since. These are the plain two-request cases over request-scoped sessions; the
/// session-close variants are in <c>MessagePickupLiveSessionLifecycleTests</c>.
/// </summary>
public sealed class MessagePickupHandlerLeaseIdentityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Every_request_takes_a_lease_of_its_own_and_gives_back_under_that_lease_only()
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var http = h.Http(h.Alice);
        var socket = h.Live(h.Alice);
        h.ResetTraces();

        // 1. Not taken by the session: everything goes back in one release.
        http.OnSend = (_, _) => ReturnRouteSendResult.NotSent;
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), http)).ShouldBeNoReply();
        // 2. The send throws: the same, from the clean-up.
        http.OnSend = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), http)).ShouldBeNoReply();
        // 3. Trimmed twice, then taken: two releases of one message each, over a live session.
        socket.OnSend = (message, _) => message.Attachments!.Count > 1 ? ReturnRouteSendResult.TooLarge : null;
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), socket)).ShouldBeNoReply();
        // 4. A second request on the same live session, for what the trim gave back.
        socket.OnSend = (_, _) => ReturnRouteSendResult.NotSent;
        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), socket)).ShouldBeNoReply();

        var leases = h.Store.LeaseRequests.Select(r => r.Lease).ToArray();
        leases.Should().HaveCount(4);
        leases.Select(l => l.Id).Should().OnlyHaveUniqueItems("two requests never share a lease")
            .And.OnlyContain(id => !string.IsNullOrEmpty(id) && !id.Contains("did:"));
        // The owner says which connection (so a closing socket can end all of its leases); the id
        // says which lease. One socket, one owner, two leases.
        leases[0].Owner.Should().BeNull();
        leases[2].Owner.Should().NotBeNull().And.Be(leases[3].Owner);
        leases[2].Id.Should().NotBe(leases[3].Id).And.NotBe(leases[2].Owner);

        // Release by release: whose ids, under whose lease.
        h.Store.Released.Select(r => string.Join(",", r.Ids)).Should().Equal(
            string.Join(",", ids),          // request 1
            string.Join(",", ids),          // request 2
            ids[2], ids[1],                 // request 3, last out first
            ids[1] + "," + ids[2]);         // request 4
        h.Store.ReleasedLeaseIds.Should().Equal(leases[0].Id, leases[1].Id, leases[2].Id, leases[2].Id, leases[3].Id);

        // A lease id is the mediator's own bookkeeping: never on the wire, never in a log line.
        foreach (var lease in leases)
        {
            foreach (var pushed in http.Sent.Concat(socket.Sent))
                JsonSerializer.Serialize(pushed, DidCommJson.Default).Should().NotContain(lease.Id);
            h.Fixture.PickupLogs.Should().OnlyContain(e => !e.Everything.Contains(lease.Id));
        }
    }

    [Theory]
    [InlineData("the session does not take the delivery")]
    [InlineData("the send throws")]
    [InlineData("the delivery is trimmed and then not taken")]
    public async Task A_request_whose_lease_ran_out_cannot_end_the_lease_a_later_request_took_on_the_same_messages(string firstRequestEndsBecause)
    {
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2), await h.QueueAsync(h.Alice, 30, 3) };
        var first = h.Http(h.Alice);
        var second = h.Http(h.Alice);
        first.OnSend = firstRequestEndsBecause switch
        {
            "the session does not take the delivery" => (_, _) => ReturnRouteSendResult.NotSent,
            "the send throws" => (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker),
            _ => (message, _) => message.Attachments!.Count == 3 ? ReturnRouteSendResult.TooLarge : ReturnRouteSendResult.NotSent,
        };

        // The first request is parked inside the store, just before it gives back what it leased.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        h.Store.OnRelease = async (grant, messageIds, leaseId, ct) =>
        {
            if (Interlocked.Increment(ref releases) == 1)
            {
                parked.TrySetResult();
                await resume.Task.WaitAsync(Deadline);
            }
            return await h.Store.Inner.ReleaseAsync(grant, messageIds, leaseId, ct);
        };
        h.ResetTraces();
        var firstRequest = h.SendAsync(h.DeliveryRequest(h.Alice, 10), first);
        await parked.Task.WaitAsync(Deadline);

        // Meanwhile its lease runs out, and the recipient — on another connection — is delivered
        // the same three messages. They are now held under the second request's lease.
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var later = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(later, second)).ShouldBeNoReply();
        second.Sent.Should().ContainSingle().Which.ShouldBeDelivery(later, h.Mediator, h.Alice, null, ids);

        // Now the first request finishes giving back.
        resume.SetResult();
        (await firstRequest.WaitAsync(Deadline)).ShouldBeNoReply();

        var leases = h.Store.LeaseRequests.Select(r => r.Lease.Id).ToArray();
        leases.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        h.Store.ReleasedLeaseIds.Should().NotBeEmpty().And.OnlyContain(id => id == leases[0], "the first request names its own lease, whatever has happened to the records since");
        h.ReleasedIds.Should().BeEquivalentTo(ids);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty(
            "the messages are delivered and unacknowledged under the second request's lease (FR-PICK-04); the first request may give back its own lease, not one taken after it");
        first.Sent.Should().BeEmpty();

        // And the second delivery is acknowledged as usual.
        (await h.SendAsync(h.MessagesReceived(h.Alice, ids), second)).Status().MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_late_too_large_verdict_changes_nothing_about_a_message_a_later_request_has_delivered_under_its_own_lease()
    {
        // A message that does not fit even alone "keeps the request's lease" — which has to mean
        // that the request does nothing to the record, not that it puts its lease on it again. The
        // case that tells the two apart: a request whose lease has run out, and whose (larger — a
        // long thread id and a filter, or an older view of the peer's keys) delivery of the first
        // message alone comes back TooLarge only now, while another request has delivered that
        // message in the meantime. Whatever the verdict, the message is not removed ("Delivered
        // messages MUST NOT be deleted until delivery is acknowledged by a messages-received
        // message"), and it stays under the later request's lease, for that lease's whole time.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var first = h.Http(h.Alice);
        var second = h.Http(h.Alice);
        first.OnSend = (_, _) => ReturnRouteSendResult.TooLarge;

        // The first request is parked in the middle of its trim: it has learnt that both messages
        // together are too large and is giving the last one back.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        h.Store.OnRelease = async (grant, messageIds, leaseId, ct) =>
        {
            if (Interlocked.Increment(ref releases) == 1)
            {
                parked.TrySetResult();
                await resume.Task.WaitAsync(Deadline);
            }
            return await h.Store.Inner.ReleaseAsync(grant, messageIds, leaseId, ct);
        };
        h.ResetTraces();
        var firstRequest = h.SendAsync(h.DeliveryRequest(h.Alice, 10), first);
        await parked.Task.WaitAsync(Deadline);

        // Its lease runs out; the recipient, on another connection, is delivered both messages.
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var later = h.DeliveryRequest(h.Alice, 10);
        (await h.SendAsync(later, second)).ShouldBeNoReply();
        second.Sent.Should().ContainSingle().Which.ShouldBeDelivery(later, h.Mediator, h.Alice, null, ids);

        // The first request goes on: the first message alone is too large for ITS delivery too.
        resume.SetResult();
        await firstRequest.WaitAsync(Deadline);

        h.Store.Acknowledged.Should().BeEmpty("the message is no longer this request's: it was delivered under a later lease and is waiting for its messages-received");
        h.Store.Inner.MessageCount.Should().Be(2);
        // Two requests, two leases — the first request leased more than once (it looks further
        // after passing a message over), always under its one lease, and found nothing to take.
        var leases = h.Store.LeaseRequests.Select(r => r.Lease).Distinct().ToArray();
        leases.Should().HaveCount(2);
        h.Store.ReleasedLeaseIds.Should().Equal(new[] { leases[0].Id }, "the one release — of the trimmed message — names the first request's own lease, and finds nothing");
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty("both are still under the second request's lease");

        // Had the second delivery been lost on the way, both would be delivered again once its
        // lease runs out — and not a tick earlier.
        h.Clock.Now = leases[1].ExpiresAt - TimeSpan.FromTicks(1);
        (await h.DeliverableAsync(h.Alice)).Should().BeEmpty();
        h.Clock.Now = leases[1].ExpiresAt;
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }

    [Fact]
    public async Task A_lease_that_ran_out_and_was_not_taken_again_is_still_given_back_by_its_own_request()
    {
        // The other side of compare-and-release: nobody took the messages in the meantime, so the
        // late release is still this request's to make and changes nothing that matters.
        await using var h = await MessagePickupHandlerHarness.CreateAsync();
        await h.EnrolAsync(h.Alice, h.Alice);
        var ids = new[] { await h.QueueAsync(h.Alice, 10, 1), await h.QueueAsync(h.Alice, 20, 2) };
        var session = h.Http(h.Alice);
        session.OnSend = (_, _) =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(61)); // the send took longer than the lease
            return ReturnRouteSendResult.NotSent;
        };
        h.ResetTraces();

        (await h.SendAsync(h.DeliveryRequest(h.Alice, 10), session)).ShouldBeNoReply();

        h.Store.ReleasedLeaseIds.Should().Equal(h.Store.LeaseRequests.Single().Lease.Id);
        (await h.DeliverableAsync(h.Alice)).Should().Equal(ids);
    }
}
