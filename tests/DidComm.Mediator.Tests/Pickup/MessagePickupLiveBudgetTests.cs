using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The retained-storage budget under live delivery (FR-PICK-08, -09, -10; JC-5, -6): a live push is
/// the store's own record under a lease — admitted first, pushed second, never copied — so a
/// session that reads pushes without acknowledging can never make the mediator hold more than the
/// grant's quota, and what it did not acknowledge is deliverable again, as the same records, once
/// it closes.
/// </summary>
public sealed class MessagePickupLiveBudgetTests
{
    private const int Size = 300;
    private const int Fits = 5;

    [Theory]
    [InlineData("max_queue_messages")]
    [InlineData("max_queue_bytes")]
    public async Task A_live_session_that_never_acknowledges_never_makes_the_mediator_hold_more_than_the_quota(string bound)
    {
        await using var fixture = PickupFixture.Create(
            pickup: o =>
            {
                if (bound == "max_queue_messages")
                    o.MaxQueueMessages = Fits;
                else
                    o.MaxQueueBytes = Fits * Size + Size - 1; // five fit; a sixth is one byte too many
            },
            fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue();
        fixture.Memory.MessageCount.Should().Be(0, "before the pushes");

        // A stream of pushes, none acknowledged.
        var retained = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(Size, (byte)i));

            if (i < Fits)
            {
                result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "push {0} is within the quota", i);
                retained.Add(result.MessageId!);
            }
            else
            {
                result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.QueueFull, null, peer), "push {0} is beyond the quota: not retained", i);
            }
            var held = Math.Min(i + 1, Fits);
            fixture.Memory.MessageCount.Should().Be(held, "during the pushes, after push {0}", i);
            fixture.Memory.TotalBytes.Should().Be((long)held * Size);
            socket.Sent.Should().HaveCount(held, "what is not admitted is not pushed");
        }
        socket.Sent.Select(Attachments).Should().OnlyContain(count => count == 1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("every retained message is leased to the socket");

        // The session closes (the registry is not told): counters unchanged, no copy re-queued.
        socket.Close();
        fixture.Memory.MessageCount.Should().Be(Fits, "after the disconnect");
        fixture.Memory.TotalBytes.Should().Be((long)Fits * Size);

        // Its leases are released: a new connection gets every message, the same ids, in arrival order.
        IReadOnlyList<DeliveredMessage> delivered = Array.Empty<DeliveredMessage>();
        await MessagePickupLive.UntilAsync(
            async () => (delivered = await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Count > 0,
            "the leases of the closed session are released without waiting for the lease timeout");
        delivered.Select(m => m.Id).Should().Equal(retained, "the original records, in arrival order");
        for (var i = 0; i < Fits; i++)
            delivered[i].Payload.ToArray().Should().Equal(PickupFixture.Payload(Size, (byte)i));
        fixture.Memory.MessageCount.Should().Be(Fits, "releasing a lease copies nothing");
        fixture.Memory.TotalBytes.Should().Be((long)Fits * Size);
        fixture.FakeStore!.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(12, "one admission attempt per arrival and none afterwards: nothing is re-enqueued");
        fixture.FakeStore.Enqueued.Select(e => e.Message.Id).Take(Fits).Should().Equal(retained);

        // Still at the quota until the acknowledgement.
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(Size))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);
        (await MessagePickupLive.AckAsync(fixture, peer, mediator, retained.ToArray())).MessageCount.Should().Be(0);
        fixture.Memory.MessageCount.Should().Be(0);
        fixture.Memory.TotalBytes.Should().Be(0);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(Size))).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "room again, and the closed session is no live target");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_flood_for_one_grant_stays_within_max_queue_bytes_and_leaves_another_grant_its_room(bool live)
    {
        const int quota = 10 * 1024;
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueBytes = quota);
        var mediator = (await fixture.NewPeerAsync()).Did;
        var flooded = (await fixture.NewPeerAsync()).Did;
        var bystander = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(flooded, mediator, flooded);
        await fixture.EnrolAsync(bystander, mediator, bystander);
        var socket = fixture.Session(flooded, mediator, live: true);
        if (live)
            await MessagePickupLive.SetLiveAsync(fixture, flooded, mediator, socket);

        // 300 KiB offered against a 10 KiB quota, in sizes that do not divide it.
        var outcomes = new List<PickupDeliveryOutcome>();
        long accepted = 0;
        for (var i = 0; i < 400; i++)
        {
            var size = 700 + i % 100;
            var result = await fixture.Queue.DeliverAsync(flooded, PickupFixture.Payload(size, (byte)i));
            outcomes.Add(result.Outcome);
            if (result.MessageId is not null)
                accepted += size;
            fixture.Memory.TotalBytes.Should().BeLessThanOrEqualTo(quota, "after arrival {0}", i);
            fixture.Memory.TotalBytes.Should().Be(accepted, "exactly what was reported as retained is retained");
        }

        outcomes.Should().OnlyContain(o => o == (live ? PickupDeliveryOutcome.PushedLive : PickupDeliveryOutcome.Queued) || o == PickupDeliveryOutcome.QueueFull, "a flood is never an exception");
        outcomes.Count(o => o == PickupDeliveryOutcome.QueueFull).Should().BeGreaterThan(380);
        socket.Sent.Should().HaveCount(live ? outcomes.Count(o => o == PickupDeliveryOutcome.PushedLive) : 0);
        (await MessagePickupLive.StatusAsync(fixture, flooded, mediator)).TotalBytes.Should().Be(accepted, "the recipient observes the cap through status.total_bytes (FR-PICK-10)");

        (await fixture.Queue.DeliverAsync(bystander, PickupFixture.Payload(quota))).Outcome.Should().Be(PickupDeliveryOutcome.Queued, "one grant's flood does not take another grant's quota");
    }

    [Fact]
    public async Task Pushes_that_arrive_at_the_same_time_are_admitted_up_to_the_quota_and_only_the_admitted_are_pushed()
    {
        const int arrivals = 40;
        MessagePickupLive.RaiseThreadPoolFloor(arrivals);
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = Fits);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        // All arrivals are released together and overlap; the invariant holds for every interleaving.
        using var start = new ManualResetEventSlim(false);
        var arrived = 0;
        var tasks = Enumerable.Range(0, arrivals).Select(i => Task.Run(async () =>
        {
            Interlocked.Increment(ref arrived);
            start.Wait(MessagePickupLive.Deadline).Should().BeTrue();
            return await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(Size, (byte)i));
        })).ToArray();
        await MessagePickupLive.UntilAsync(() => Volatile.Read(ref arrived) == arrivals, "every arrival is parked at the start line");
        start.Set();
        var results = await Task.WhenAll(tasks).WaitAsync(MessagePickupLive.Deadline);

        var pushed = results.Where(r => r.Outcome == PickupDeliveryOutcome.PushedLive).Select(r => r.MessageId!).ToArray();
        pushed.Should().HaveCount(Fits);
        results.Where(r => r.Outcome == PickupDeliveryOutcome.QueueFull).Should().HaveCount(arrivals - Fits);
        fixture.Memory.MessageCount.Should().Be(Fits);
        fixture.Memory.TotalBytes.Should().Be((long)Fits * Size);
        socket.Sent.Select(PushedId).Should().BeEquivalentTo(pushed, "exactly the admitted messages were pushed, once each");
    }

    [Fact]
    public async Task A_live_push_is_one_more_retained_record_until_its_acknowledgement_and_none_after()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(45), fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(123, 4));

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        fixture.Memory.MessageCount.Should().Be(1, "retained count +1 until the acknowledgement (FR-PICK-09)");
        fixture.Memory.TotalBytes.Should().Be(123);
        var (record, lease) = fixture.FakeStore!.Enqueued.Should().ContainSingle("admitted once, already under its lease: one store step (JC-6)").Subject;
        record.Id.Should().Be(result.MessageId);
        lease.Should().NotBeNull();
        lease!.Owner.Should().NotBeNullOrEmpty("a live session's leases carry its owner token");
        lease.ExpiresAt.Should().Be(fixture.Clock.Now + TimeSpan.FromSeconds(45));

        // Counted (FR-PICK-04: the count drops on acknowledgement), and not handed out while leased.
        var status = await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket);
        status.MessageCount.Should().Be(1);
        status.TotalBytes.Should().Be(123);
        status.LiveDelivery.Should().BeTrue();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty();

        // The owner token is the mediator's own business: never on the wire, never in a log.
        var push = socket.Sent.Should().ContainSingle().Subject;
        JsonSerializer.Serialize(push, DidCommJson.Default).Should().NotContain(lease.Owner!);
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, lease.Owner, peer, mediator, result.MessageId);

        var acknowledged = await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { result.MessageId! }), mediator, socket);
        acknowledged.MessageCount.Should().Be(0);
        acknowledged.LiveDelivery.Should().BeTrue("acknowledging does not change the mode");
        fixture.Memory.MessageCount.Should().Be(0, "0 after the acknowledgement");
        fixture.Memory.TotalBytes.Should().Be(0);

        // A late duplicate acknowledgement is a harmless miss; closing afterwards has nothing to release.
        (await fixture.StatusOfAsync(MessagePickup.CreateMessagesReceived(peer, mediator, new[] { result.MessageId! }), mediator, socket)).MessageCount.Should().Be(0);
        socket.Close();
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task An_unacknowledged_live_push_is_deliverable_by_request_once_its_lease_runs_out_and_is_never_pushed_twice()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(45));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(50));

        fixture.Clock.Advance(TimeSpan.FromSeconds(45) - TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("the lease holds to its last millisecond");

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
        socket.Sent.Should().ContainSingle("the session stays open and live; a lease that ran out is not a reason to push again");
        fixture.Memory.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Waiting_leased_live_and_not_yet_due_messages_all_count_against_the_same_quota()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.MaxQueueMessages = 4);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);

        // One delivered by request and not acknowledged, one waiting, one not yet due, one pushed live.
        var leased = await fixture.QueueAsync(peer, PickupFixture.Payload(10, 1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(leased);
        var waiting = await fixture.QueueAsync(peer, PickupFixture.Payload(10, 2));
        var later = await fixture.QueueAsync(peer, PickupFixture.Payload(10, 3), new PickupDeliveryOptions(NotBefore: fixture.Clock.Now + TimeSpan.FromHours(1)));
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var live = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 4));
        live.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        fixture.Memory.MessageCount.Should().Be(4);

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 5))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull, "a live push never bypasses the quota (FR-PICK-10)");
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 5), new PickupDeliveryOptions(AllowLive: false))).Outcome.Should().Be(PickupDeliveryOutcome.QueueFull);
        socket.Sent.Should().ContainSingle();
        fixture.Memory.MessageCount.Should().Be(4);

        // Each kind gives its room back when it is acknowledged or removed.
        foreach (var id in new[] { leased, waiting, later, live.MessageId! })
        {
            (await fixture.Queue.RemoveAsync(peer, id)).Should().BeTrue();
            (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(10, 6), new PickupDeliveryOptions(AllowLive: false))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            fixture.Memory.MessageCount.Should().Be(4);
        }
    }

    private static int Attachments(DidComm.Messages.Message delivery) => delivery.Attachments?.Count ?? 0;

    private static string PushedId(DidComm.Messages.Message delivery)
    {
        MessagePickup.TryParseDelivery(delivery, out var messages, out _).Should().BeTrue();
        return messages!.Should().ContainSingle().Subject.Id;
    }
}
