using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Forward progress past messages that do not fit the recipient's envelope even alone (FR-PICK-03),
/// when there are more of them than one request's work budget and the recipient comes back only
/// after the lease has run out, or on a new connection (PR #107 review, finding 1). The memory that
/// a record did not fit is the record's own — <see cref="IMessageStore.PassOverAsync"/>, with a
/// period that doubles — not a lease that expires or ends with the connection. Nothing is ever
/// removed, and one request never does more than its bounded work.
/// </summary>
public sealed class MessagePickupForwardProgressTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private sealed class Scenario : IAsyncDisposable
    {
        public required PickupFixture Fixture { get; init; }
        public required string Mediator { get; init; }
        public required string Peer { get; init; }
        public HashSet<string> Unfit { get; } = new(StringComparer.Ordinal);
        public List<string> Fit { get; } = new();
        public int Sends { get; private set; }

        public static async Task<Scenario> CreateAsync(int batch, int blockers, int behind)
        {
            var fixture = PickupFixture.Create(
                pickup: o =>
                {
                    o.MaxDeliveryBatch = batch;
                    o.LeaseTimeout = Lease;
                    // The subject is forward progress, not aging: polls a day apart run past the
                    // mailbox age limit (#95), which would remove the messages under test.
                    o.MaxMessageAge = null;
                },
                fakeStore: true);
            var (mediator, peer) = await fixture.EnrolledAsync();
            var scenario = new Scenario { Fixture = fixture, Mediator = mediator, Peer = peer };
            for (var i = 0; i < blockers; i++)
                scenario.Unfit.Add(await fixture.QueueAsync(peer, PickupFixture.Payload(40 + i, (byte)i)));
            for (var i = 0; i < behind; i++)
                scenario.Fit.Add(await fixture.QueueAsync(peer, PickupFixture.Payload(10 + i, (byte)(100 + i))));
            return scenario;
        }

        /// <summary>A session whose envelope cannot carry any delivery that holds one of the unfit records.</summary>
        public PickupFakeSession Session(bool live = false)
        {
            var session = Fixture.Session(Peer, Mediator, live);
            session.OnSend = (message, _) =>
            {
                Sends++;
                return message.Attachments!.Any(a => Unfit.Contains(a.Id!)) ? ReturnRouteSendResult.TooLarge : null;
            };
            return session;
        }

        /// <summary>
        /// One <c>delivery-request</c> asking for everything; the ids it was delivered (empty when
        /// answered <c>status</c>). What was delivered is acknowledged at once, as a recipient does —
        /// straight on the real store, so that the fake's trace shows only what the role itself did.
        /// </summary>
        public async Task<IReadOnlyList<string>> PollAsync(PickupFakeSession session)
        {
            var before = session.Sent.Count;
            var outcome = await Fixture.SendAsync(MessagePickup.CreateDeliveryRequest(Peer, Mediator, int.MaxValue), Mediator, session);
            var pushed = session.Sent.Skip(before).ToArray();
            if (pushed.Length == 0)
            {
                MessagePickup.TryParseStatus(outcome.Reply!, out _).Should().BeTrue("a request that delivers nothing is answered with the status");
                return Array.Empty<string>();
            }
            MessagePickup.TryParseDelivery(pushed.Should().ContainSingle().Subject, out var messages, out _).Should().BeTrue();
            var ids = messages!.Select(m => m.Id).ToArray();
            (await Fixture.FakeStore!.Inner.AcknowledgeAsync(Peer, ids, CancellationToken.None)).Should().Be(ids.Length);
            return ids;
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    [Fact]
    public async Task Two_unfit_messages_a_batch_of_one_and_a_poll_every_61_seconds_still_deliver_the_third()
    {
        // The reviewer's reproduction: MaxDeliveryBatch 1 (two sends and two lease rounds per
        // request), the default 60 s lease, two records that each produce TooLarge, a deliverable
        // third, one request every 61 s. Before the fix every request spent its two sends on the
        // same two records and answered status, for ever.
        await using var s = await Scenario.CreateAsync(batch: 1, blockers: 2, behind: 1);
        var store = s.Fixture.FakeStore!;
        var t0 = s.Fixture.Clock.Now;

        // Poll 1: both are tried alone, do not fit, and are passed over for one lease period.
        (await s.PollAsync(s.Session())).Should().BeEmpty();
        s.Sends.Should().Be(2);
        store.PassedOver.Select(p => p.Until).Should().Equal(t0 + Lease, t0 + Lease);

        // Poll 2, 61 s later: their period is over; tried again, passed over for twice as long.
        s.Fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        (await s.PollAsync(s.Session())).Should().BeEmpty();
        s.Sends.Should().Be(4);
        store.PassedOver.Skip(2).Select(p => p.Until).Should().Equal(t0.AddSeconds(61 + 120), t0.AddSeconds(61 + 120));

        // Poll 3, 61 s later: both are still passed over, so the request's work reaches the third.
        s.Fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        (await s.PollAsync(s.Session())).Should().Equal(s.Fit);
        s.Sends.Should().Be(5, "the two blockers cost this request nothing");

        s.Fixture.Memory.MessageCount.Should().Be(2, "nothing is removed by the role: the two that do not fit stay stored (the third was acknowledged by its recipient)");
        store.CountOf(nameof(IMessageStore.AcknowledgeAsync)).Should().Be(0);
    }

    public static TheoryData<int, int, int> BlockersBeyondTheBudget => new()
    {
        // batch, blockers, seconds between polls
        { 1, 3, 60 }, { 1, 3, 61 }, { 1, 9, 61 }, { 1, 9, 300 }, { 1, 9, 3600 },
        { 2, 5, 60 }, { 2, 9, 61 }, { 2, 9, 600 },
        { 3, 7, 61 }, { 3, 20, 61 }, { 3, 20, 900 },
        { 10, 25, 61 }, { 10, 25, 86400 },
    };

    [Theory]
    [MemberData(nameof(BlockersBeyondTheBudget))]
    public async Task More_unfit_messages_than_a_request_can_try_never_keep_what_is_behind_them_from_a_recipient_that_polls_at_or_beyond_the_lease(int batch, int blockers, int seconds)
    {
        blockers.Should().BeGreaterThan(2, "more blockers than the two a request can set aside when its batch trims to the head each time");
        await using var s = await Scenario.CreateAsync(batch, blockers, behind: 2);
        var budget = 2 * batch;

        // One request per poll, the polls `seconds` apart — never inside a lease. Each request does
        // at most its bounded work; the deliverable messages arrive after a bounded number of polls.
        var delivered = new List<string>();
        var polls = 0;
        for (; polls < 400 && delivered.Count < s.Fit.Count; polls++)
        {
            if (polls > 0)
                s.Fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
            var sendsBefore = s.Sends;
            var roundsBefore = s.Fixture.FakeStore!.CountOf(nameof(IMessageStore.LeaseAsync));
            delivered.AddRange(await s.PollAsync(s.Session()));
            (s.Sends - sendsBefore).Should().BeLessThanOrEqualTo(budget, "one request never packs more than twice the batch maximum");
            (s.Fixture.FakeStore.CountOf(nameof(IMessageStore.LeaseAsync)) - roundsBefore).Should().BeLessThanOrEqualTo(budget, "nor asks the store for more lease rounds than that");
        }

        delivered.Should().Equal(s.Fit, "the messages behind the blockers are delivered, in order, once each");
        polls.Should().BeLessThan(400, "progress is made in a bounded number of polls: the periods the blockers are passed over for double, so they outgrow any polling interval");
        s.Fixture.Memory.MessageCount.Should().Be(blockers, "the role removed nothing: every message that does not fit is still stored");
        s.Fixture.FakeStore!.CountOf(nameof(IMessageStore.AcknowledgeAsync)).Should().Be(0);
        delivered.Should().NotIntersectWith(s.Unfit);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 9)]
    [InlineData(10, 30)]
    public async Task A_recipient_that_reconnects_for_every_request_gets_past_the_unfit_messages_without_any_time_passing(int batch, int blockers)
    {
        // A WebSocket recipient that opens a connection, asks once and closes it. Leases taken over
        // a connection end when it closes; if "did not fit" were only such a lease, every new
        // connection would meet the same records first. The clock never moves here.
        await using var s = await Scenario.CreateAsync(batch, blockers, behind: 2);

        var delivered = new List<string>();
        var connections = 0;
        for (; connections < 100 && delivered.Count < s.Fit.Count; connections++)
        {
            var socket = s.Session(live: true);
            delivered.AddRange(await s.PollAsync(socket));
            socket.Close();
            s.Fixture.Sessions.Unregister(socket);
        }

        delivered.Should().Equal(s.Fit);
        // Each connection sets at least two blockers aside for good (for this instant): the walk is linear.
        connections.Should().BeLessThanOrEqualTo(blockers / 2 + 1 + s.Fit.Count);
        s.Fixture.Memory.MessageCount.Should().Be(blockers);
    }

    [Fact]
    public async Task The_period_a_message_is_passed_over_for_doubles_each_time_and_belongs_to_the_record_not_to_a_lease_or_a_connection()
    {
        await using var s = await Scenario.CreateAsync(batch: 1, blockers: 1, behind: 0);
        var store = s.Fixture.FakeStore!;
        var id = s.Unfit.Single();
        var t0 = s.Fixture.Clock.Now;

        // First time: one lease timeout. The record carries no lease afterwards.
        var socket = s.Session(live: true);
        (await s.PollAsync(socket)).Should().BeEmpty();
        var first = store.PassedOver.Should().ContainSingle().Subject;
        (first.GrantPeerDid, first.Until).Should().Be((s.Peer, t0 + Lease));
        first.Ids.Should().Equal(id);
        first.LeaseId.Should().Be(store.LeaseRequests.First().Lease.Id, "the request gives the record up under the lease it took it with");

        // Closing the connection, or giving its leases back, does not bring the record up again.
        socket.Close();
        s.Fixture.Sessions.Unregister(socket);
        (await store.Inner.ReleaseLeasesAsync(store.LeaseRequests.First().Lease.Owner!, CancellationToken.None)).Should().Be(0, "a passed-over record is under no lease");
        var sendsBefore = s.Sends;
        (await s.PollAsync(s.Session(live: true))).Should().BeEmpty();
        s.Fixture.Clock.Advance(Lease - TimeSpan.FromTicks(1));
        (await s.PollAsync(s.Session())).Should().BeEmpty();
        s.Sends.Should().Be(sendsBefore, "inside its period the record is not even tried");

        // At the end of each period it is tried once more, and the next period is twice as long.
        var expected = new List<DateTimeOffset> { t0 + Lease };
        var period = Lease;
        for (var attempt = 2; attempt <= 6; attempt++)
        {
            s.Fixture.Clock.Now = expected[^1];
            (await s.PollAsync(s.Session())).Should().BeEmpty();
            period *= 2;
            expected.Add(s.Fixture.Clock.Now + period);
            store.PassedOver.Select(p => p.Until).Should().Equal(expected);
        }
        s.Sends.Should().Be(6, "one send per period");

        // It is still there, still counted, and the store knows how often it was passed over.
        s.Fixture.Clock.Now = expected[^1];
        var leased = await store.Inner.LeaseAsync(new MessageLeaseRequest(s.Peer, null, 10, long.MaxValue, s.Fixture.Clock.Now, new MessageLease(null, s.Fixture.Clock.Now.AddSeconds(1))), CancellationToken.None);
        leased.Should().ContainSingle().Which.PassOvers.Should().Be(6);
        (await s.Fixture.StatusOfAsync(MessagePickup.CreateStatusRequest(s.Peer, s.Mediator), s.Mediator, s.Session())).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_pass_over_that_fails_ends_the_request_with_no_reply_and_gives_back_what_it_holds()
    {
        await using var s = await Scenario.CreateAsync(batch: 2, blockers: 1, behind: 1);
        var store = s.Fixture.FakeStore!;
        store.OnPassOver = (_, _, _, _, _) => throw new InvalidOperationException("the store is down");

        var session = s.Session();
        var outcome = await s.Fixture.SendAsync(MessagePickup.CreateDeliveryRequest(s.Peer, s.Mediator, 10), s.Mediator, session);

        outcome.Reply.Should().BeNull("a store fault is answered with nothing");
        session.Sent.Should().BeEmpty();
        store.Released.SelectMany(r => r.Ids).Should().Contain(s.Unfit.Single(), "the record the request still held is given back under its lease");
        s.Fixture.Memory.MessageCount.Should().Be(2, "nothing was removed or delivered");

        // The store recovers: the next request passes the unfit one over and delivers what is behind it.
        store.OnPassOver = null;
        (await s.PollAsync(s.Session())).Should().Equal(s.Fit);
        s.Fixture.Memory.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task The_period_saturates_instead_of_overflowing()
    {
        await using var s = await Scenario.CreateAsync(batch: 1, blockers: 1, behind: 0);
        var store = s.Fixture.FakeStore!;
        // A record the store says was passed over an absurd number of times already.
        var record = (await store.Inner.LeaseAsync(new MessageLeaseRequest(s.Peer, null, 1, long.MaxValue, s.Fixture.Clock.Now, new MessageLease(null, s.Fixture.Clock.Now) { Id = "probe" }), CancellationToken.None)).Single();
        await store.Inner.ReleaseAsync(s.Peer, new[] { record.Id }, "probe", CancellationToken.None);
        store.OnLease = async (request, ct) => (await store.Inner.LeaseAsync(request, ct)).Select(m => m with { PassOvers = int.MaxValue }).ToArray();

        (await s.PollAsync(s.Session())).Should().BeEmpty();

        store.PassedOver.Should().ContainSingle().Which.Until.Should().Be(DateTimeOffset.MaxValue);
    }
}
