using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// ADV-2, goal 1 at the level of the role: what one sender, or one enrolled peer, can make the
/// mediator hold or do. The quota that is used up without the recipient being able to see or free
/// it; JC-19's message "no envelope of this peer can carry" as a weapon against the messages
/// queued behind it; the number of full packs one small request can cost.
/// </summary>
public sealed class MessagePickupAdversarialResourceBoundsTests
{
    /// <summary>A payload of this size stands for one the recipient's envelope cannot carry even alone (JC-19): the scripted session answers <c>TooLarge</c> for any delivery that contains it.</summary>
    private const int Unfit = 777;

    private static PickupFakeSession EnvelopeThatCannotCarryTheUnfit(PickupFixture fixture, string peer, string mediator, bool live = false)
    {
        var session = fixture.Session(peer, mediator, live);
        session.OnSend = (message, bound) =>
            bound > 0 && MessagePickup.TryParseDelivery(message, out var delivered, out string? _) && delivered!.Any(m => m.Payload.Length == Unfit)
                ? ReturnRouteSendResult.TooLarge
                : null;
        return session;
    }

    private static async Task<IReadOnlyList<string>> PollAsync(PickupFixture fixture, string peer, string mediator)
    {
        var session = EnvelopeThatCannotCarryTheUnfit(fixture, peer, mediator);
        var fetched = await fixture.FetchAsync(peer, mediator, session, 10);
        return fetched is null ? Array.Empty<string>() : fetched.Value.Messages.Select(m => m.Id).ToArray();
    }

    // ---- JC-19: does the message no envelope can carry still block what is queued behind it? ----

    /// <summary>
    /// JC-19 exists because releasing the unfit message at once meant "the record is then offered
    /// first for ever and nothing behind it is ever delivered, and any sender can cause that for a
    /// many-key recipient". Keeping the request's lease fixes that only for a recipient that asks
    /// again WITHIN the lease. One that polls once per lease period or slower (the default
    /// <c>LeaseTimeout</c> is 60 seconds; a minute between polls is ordinary) finds the lease run
    /// out at every poll: the unfit message is leased again at the head of the batch, the batch is
    /// trimmed from its end down to that message, everything behind it is given back, and the
    /// answer is <c>status</c>. For ever.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(300)]
    public async Task A_message_no_envelope_can_carry_does_not_keep_the_messages_behind_it_from_a_recipient_that_polls_once_per_lease_period(int secondsBetweenPolls)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(Unfit, 1));
        var behind = await fixture.QueueAsync(peer, PickupFixture.Payload(50, 2));

        var delivered = new List<string>();
        for (var poll = 0; poll < 6 && delivered.Count == 0; poll++)
        {
            delivered.AddRange(await PollAsync(fixture, peer, mediator));
            fixture.Clock.Advance(TimeSpan.FromSeconds(secondsBetweenPolls));
        }

        delivered.Should().Contain(behind, "a message one sender made undeliverable must not block the recipient's other messages (JC-19), however often the recipient polls");
    }

    /// <summary>The same in one request: the request that passes the unfit message over goes on to what is behind it (JC-19).</summary>
    [Fact]
    public async Task A_request_that_meets_a_message_no_envelope_can_carry_goes_on_to_what_is_queued_behind_it()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(Unfit, 1));
        var behind = await fixture.QueueAsync(peer, PickupFixture.Payload(50, 2));

        (await PollAsync(fixture, peer, mediator)).Should().Equal(new[] { behind }, "the request passes the unfit message over and delivers what is queued behind it");
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        (await PollAsync(fixture, peer, mediator)).Should().BeEmpty("the unfit message is still passed over and the other one is under its own lease");
        fixture.Memory.MessageCount.Should().Be(2, "nothing was removed");
    }

    // ---- the quota a recipient cannot see or free --------------------------------------------------

    /// <summary>The control: the same flood with messages that are due is visible in <c>status</c>, and the recipient frees its quota by receiving and acknowledging.</summary>
    [Fact]
    public async Task A_recipient_whose_quota_is_used_up_by_messages_that_are_due_sees_it_and_frees_it()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = 5);
        var (mediator, peer) = await fixture.EnrolledAsync();
        for (var i = 0; i < 5; i++)
            await fixture.QueueAsync(peer, PickupFixture.Payload(1000, (byte)i));
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 9))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);

        var status = await MessagePickupAdversarialResource.StatusAsync(fixture, peer, mediator);
        status.MessageCount.Should().Be(5);
        status.TotalBytes.Should().Be(5000);

        var ids = await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator);
        ids.Should().HaveCount(5);
        await fixture.SendAsync(MessagePickup.CreateMessagesReceived(peer, mediator, ids), mediator, fixture.Session(peer, mediator));
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 9))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
    }

    // ---- what one small request can cost -----------------------------------------------------------

    /// <summary>
    /// The trim loop packs the batch again after every <c>TooLarge</c>. For a peer whose envelope
    /// makes every batch too large the number of full packs one <c>delivery-request</c> costs is
    /// the batch it leased — at most <c>MaxDeliveryBatch</c> — and never more, whatever
    /// <c>limit</c> says. Then one more message is under the request's lease, so the next request
    /// costs one pack fewer; once all are, a request costs none until a lease ends.
    /// </summary>
    [Fact]
    public async Task One_delivery_request_costs_at_most_twice_MaxDeliveryBatch_packs_and_a_queue_of_unfit_messages_costs_nothing_more_until_a_lease_ends()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxDeliveryBatch = 4);
        var (mediator, peer) = await fixture.EnrolledAsync();
        for (var i = 0; i < 6; i++)
            await fixture.QueueAsync(peer, PickupFixture.Payload(Unfit, (byte)i));

        var packsPerRequest = new List<int>();
        for (var request = 0; request < 9; request++)
        {
            var session = EnvelopeThatCannotCarryTheUnfit(fixture, peer, mediator);
            var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, int.MaxValue), mediator, session);
            session.Sent.Should().BeEmpty();
            MessagePickup.TryParseStatus(outcome.Reply!, out var status).Should().BeTrue();
            status!.MessageCount.Should().Be(6, "nothing is removed");
            packsPerRequest.Add(session.Attempts.Count);
        }

        // 6 messages, batches of 4, a budget of 2 × 4 packs per request. Request 1: 4 packs pass #1
        // over, 4 more pass #2 over, budget used up. Request 2: 4 packs for #3, 3 for #4, one more,
        // budget used up. Request 3: 2 packs for #5, 1 for #6. Then every message is passed over
        // and a request costs nothing.
        packsPerRequest.Should().Equal(8, 8, 3, 0, 0, 0, 0, 0, 0);

        // The leases end one request-instant apart; here all at once: the cycle starts again, no faster.
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        var again = EnvelopeThatCannotCarryTheUnfit(fixture, peer, mediator);
        await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator, int.MaxValue), mediator, again);
        again.Attempts.Count.Should().Be(8);
        fixture.Memory.MessageCount.Should().Be(6);
    }

    /// <summary>
    /// "TooLarge is decided before the closed check": a session that closes while the first batch
    /// is being packed answers <c>TooLarge</c> for it and <c>NotSent</c> from then on. The trim loop
    /// must stop there — one pack, not one per message — and give everything back.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_that_closes_during_the_first_pack_ends_the_trim_loop_after_that_one_pack_and_every_lease_is_given_back(bool live)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
            ids.Add(await fixture.QueueAsync(peer, PickupFixture.Payload(40, (byte)i)));

        var session = fixture.Session(peer, mediator, live);
        var packs = 0;
        session.OnSend = (_, _) =>
        {
            // The pack finds the envelope over the bound; the connection has gone meanwhile.
            Interlocked.Increment(ref packs);
            session.Close();
            return ReturnRouteSendResult.TooLarge;
        };
        var outcome = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, session);

        outcome.Reply.Should().BeNull("the session did not take the delivery");
        packs.Should().Be(1, "after a close every further send is refused before it packs");
        session.Attempts.Count.Should().Be(2, "the trimmed batch is offered once more and refused unpacked");

        // Nothing waits for a timeout: all five are deliverable at once, in order.
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Equal(ids);
    }

    // ---- a clock at the end of time ------------------------------------------------------------------

    /// <summary>
    /// <c>now + LeaseTimeout</c> does not exist one second before the last instant. Nothing may
    /// leave dispatch, no lease may be left behind, and a message handed to the queue is either
    /// retained and reported so, or not retained.
    /// </summary>
    [Fact]
    public async Task With_the_clock_at_the_end_of_time_nothing_leaves_dispatch_and_nothing_is_left_leased()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromHours(24));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1));
        var live = fixture.Session(peer, mediator, live: true);
        (await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, live)).LiveDelivery.Should().BeTrue();

        var before = fixture.Clock.Now;
        fixture.Clock.Now = DateTimeOffset.MaxValue.AddSeconds(-1);

        var request = await fixture.SendAsync(MessagePickupAdversarialResource.DeliveryRequest(peer, mediator), mediator, fixture.Session(peer, mediator));
        request.Reply.Should().BeNull("a request that cannot take a lease is not answered");

        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 2));
        pushed.Outcome.Should().BeOneOf(PickupDeliveryOutcome.Faulted, PickupDeliveryOutcome.Queued);
        fixture.Memory.MessageCount.Should().Be(pushed.Outcome == PickupDeliveryOutcome.Queued ? 2 : 1, "the outcome says whether the message is retained");
        live.Sent.Should().BeEmpty();

        var status = await MessagePickupAdversarialResource.StatusAsync(fixture, peer, mediator);
        status.MessageCount.Should().Be(fixture.Memory.MessageCount);
        status.LongestWaitedSeconds.Should().BeGreaterThan(0);

        // Back in ordinary time nothing is stuck under a lease.
        fixture.Clock.Now = before;
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().Contain(id);
    }

    /// <summary>The extremes of what a sender's <c>expires_time</c> and delay can be: pinned to the ends of time, never thrown.</summary>
    [Theory]
    [InlineData(long.MaxValue, PickupDeliveryOutcome.Queued)]
    [InlineData(253402300799L, PickupDeliveryOutcome.Queued)]   // 9999-12-31T23:59:59Z
    [InlineData(253402300800L, PickupDeliveryOutcome.Queued)]
    [InlineData(0L, PickupDeliveryOutcome.Expired)]
    [InlineData(-62135596800L, PickupDeliveryOutcome.Expired)]  // 0001-01-01T00:00:00Z
    [InlineData(long.MinValue, PickupDeliveryOutcome.Expired)]
    public async Task An_expires_time_at_the_ends_of_the_number_line_is_judged_not_thrown(long expiresTime, PickupDeliveryOutcome expected)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 1), new PickupDeliveryOptions(NotBefore: DateTimeOffset.MinValue, ExpiresTime: expiresTime));

        result.Outcome.Should().Be(expected);
        (await MessagePickupAdversarialResource.FetchIdsAsync(fixture, peer, mediator)).Should().HaveCount(expected == PickupDeliveryOutcome.Queued ? 1 : 0);
        (await fixture.Queue.PurgeExpiredAsync()).Should().Be(0);
    }
}
