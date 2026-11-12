using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The clean-up a closed session leaves behind is bounded (PR #107 review, finding 2). Ending the
/// leases of a closed connection is a store call the role makes in the background; with a slow
/// store and connections that come and go, those calls must not pile up without limit — neither
/// running at once nor waiting. One worker makes them, one at a time, from a backlog of at most
/// <see cref="PickupSessionTracker.MaxPendingReleases"/> owner tokens; a close that finds the
/// backlog full is not queued, and its leases end by their timeout. A session that never took a
/// lease has nothing to clean up and queues nothing. Closing never waits.
/// </summary>
public sealed class MessagePickupCleanupBoundTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>A store whose <c>ReleaseLeasesAsync</c> does not complete until the test opens the gate, and that counts how many are in flight.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _peak;
        private int _started;

        public int Active => Volatile.Read(ref _active);
        public int Peak => Volatile.Read(ref _peak);
        public int Started => Volatile.Read(ref _started);

        public void Attach(PickupFakeStore store)
            => store.OnReleaseLeases = async (owner, ct) =>
            {
                Interlocked.Increment(ref _started);
                var now = Interlocked.Increment(ref _active);
                int seen;
                while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
                {
                }
                try
                {
                    await _open.Task;
                    return await store.Inner.ReleaseLeasesAsync(owner, ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            };

        public void Open() => _open.TrySetResult();
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + what + ".");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_peer_without_a_grant_that_turns_live_mode_on_and_disconnects_a_thousand_times_leaves_no_clean_up_behind()
    {
        // The reviewer's reproduction: an empty mediation store (the peer never enrols), a delayed
        // asynchronous message store, and 1 000 connections that each enable live delivery and
        // close. Before the fix every close queued its own store call: 1 000 active at once.
        // (A thousand real dispatches: about a second. The count is the point of the test.)
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var gate = new Gate();
        gate.Attach(fixture.FakeStore!);
        var mediator = (await fixture.NewPeerAsync()).Did;
        var peer = (await fixture.NewPeerAsync()).Did;
        var tracker = fixture.Queue.Coordinator.Tracker;

        for (var connection = 0; connection < 1000; connection++)
        {
            var socket = fixture.Session(peer, mediator, live: true);
            var status = await fixture.StatusOfAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator, socket);
            status.LiveDelivery.Should().BeTrue();
            socket.Close();
            fixture.Sessions.Unregister(socket);
        }

        fixture.Sessions.Count.Should().Be(0);
        tracker.PendingReleases.Should().Be(0, "a session that never took a lease has nothing to clean up");
        gate.Started.Should().Be(0);
        fixture.FakeStore!.CountOf(nameof(IMessageStore.ReleaseLeasesAsync)).Should().Be(0);
        fixture.FakeStore.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(0);
    }

    [Fact]
    public async Task Connections_that_each_leave_a_lease_behind_are_cleaned_up_one_at_a_time_from_a_bounded_backlog_and_the_overflow_falls_back_to_lease_expiry()
    {
        const int connections = PickupSessionTracker.MaxPendingReleases + 76;
        await using var fixture = PickupFixture.Create(
            pickup: o =>
            {
                o.MaxQueueMessages = connections;
                o.MaxStoreMessages = connections;
            },
            fakeStore: true);
        var gate = new Gate();
        gate.Attach(fixture.FakeStore!);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var tracker = fixture.Queue.Coordinator.Tracker;
        var coordinator = fixture.Queue.Coordinator;

        // Every connection turns live mode on, is pushed one message (a lease owned by the
        // connection) and closes without acknowledging it. The store's release never completes.
        var ids = new List<string>();
        for (var connection = 0; connection < connections; connection++)
        {
            var socket = fixture.Session(peer, mediator, live: true);
            (await coordinator.SetLiveDeliveryAsync(peer, true, socket, static (from, to, m) => MessagePickup.CreateDelivery(from, to, new[] { m }), "churn", CancellationToken.None))
                .Status!.LiveDelivery.Should().BeTrue();
            var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(8, (byte)connection));
            pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
            ids.Add(pushed.MessageId!);

            var before = DateTime.UtcNow;
            socket.Close();
            (DateTime.UtcNow - before).Should().BeLessThan(TimeSpan.FromSeconds(2), "closing never waits for the store");
            fixture.Sessions.Unregister(socket);

            tracker.PendingReleases.Should().BeLessThanOrEqualTo(PickupSessionTracker.MaxPendingReleases);
            gate.Active.Should().BeLessThanOrEqualTo(1);
        }

        // One release is in flight and stuck; the rest wait, up to the bound; the overflow was dropped.
        await UntilAsync(() => gate.Started == 1, "the worker made its first release call");
        gate.Peak.Should().Be(1, "one store call at a time");
        tracker.PendingReleases.Should().Be(PickupSessionTracker.MaxPendingReleases);
        tracker.DroppedReleases.Should().Be(connections - PickupSessionTracker.MaxPendingReleases);
        fixture.FakeStore!.CountOf(nameof(IMessageStore.ReleaseLeasesAsync)).Should().Be(1);
        fixture.Sessions.Count.Should().Be(0);

        // Saturation is logged without naming anyone, rate-limited: the first at Warning.
        var saturated = fixture.PickupLogs.Where(e => e.Formatted.Contains("backlog is full", StringComparison.Ordinal)).ToArray();
        saturated.Should().HaveCount((int)tracker.DroppedReleases);
        saturated[0].Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        saturated.Skip(1).Should().OnlyContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug);
        saturated.Should().OnlyContain(e => !e.Everything.Contains("did:", StringComparison.Ordinal));

        // The store comes back: the backlog drains, still one call at a time, in close order.
        gate.Open();
        await UntilAsync(() => tracker.PendingReleases == 0, "the backlog drained");
        gate.Peak.Should().Be(1);
        gate.Started.Should().Be(PickupSessionTracker.MaxPendingReleases);
        fixture.FakeStore.ReleasedOwners.Should().OnlyHaveUniqueItems();

        // What the drained releases freed is deliverable at once; the dropped sessions' leases are
        // not — until their timeout, the fallback. Nothing was lost either way.
        var http = fixture.Session(peer, mediator);
        var now = await FetchAllAsync(fixture, peer, mediator);
        now.Should().Equal(ids.Take(PickupSessionTracker.MaxPendingReleases));
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));
        var later = await FetchAllAsync(fixture, peer, mediator);
        later.Should().Equal(ids, "after the lease timeout everything is deliverable again, in arrival order");
        _ = http;
    }

    [Fact]
    public async Task A_release_that_fails_does_not_stop_the_clean_up_of_the_sessions_behind_it()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var store = fixture.FakeStore!;
        var tracker = fixture.Queue.Coordinator.Tracker;
        var calls = 0;
        store.OnReleaseLeases = (owner, ct) => Interlocked.Increment(ref calls) == 2
            ? throw new InvalidOperationException("the store hiccups")
            : store.Inner.ReleaseLeasesAsync(owner, ct);

        for (var i = 0; i < 5; i++)
            tracker.ReleaseLeasesInBackground("owner-" + i);

        await UntilAsync(() => tracker.PendingReleases == 0, "the backlog drained");
        store.ReleasedOwners.Should().Equal("owner-0", "owner-1", "owner-2", "owner-3", "owner-4");
        tracker.DroppedReleases.Should().Be(0);
        fixture.PickupLogs.Should().ContainSingle(e => e.Formatted.Contains("could not be released", StringComparison.Ordinal))
            .Which.Everything.Should().NotContain("hiccups").And.NotContain("owner-");
    }

    [Fact]
    public async Task A_backlog_that_was_full_takes_new_closes_again_once_it_has_room()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var gate = new Gate();
        gate.Attach(fixture.FakeStore!);
        var tracker = fixture.Queue.Coordinator.Tracker;

        for (var i = 0; i < PickupSessionTracker.MaxPendingReleases + 10; i++)
            tracker.ReleaseLeasesInBackground("first-" + i);
        tracker.PendingReleases.Should().Be(PickupSessionTracker.MaxPendingReleases);
        tracker.DroppedReleases.Should().Be(10);

        gate.Open();
        await UntilAsync(() => tracker.PendingReleases == 0, "the backlog drained");

        tracker.ReleaseLeasesInBackground("second");
        await UntilAsync(() => fixture.FakeStore!.ReleasedOwners.Contains("second"), "the later close was cleaned up");
        tracker.DroppedReleases.Should().Be(10);
        gate.Peak.Should().Be(1);
    }

    private static async Task<IReadOnlyList<string>> FetchAllAsync(PickupFixture fixture, string peer, string mediator)
    {
        // Straight from the real store, in arrival order, without leaving a lease behind.
        var now = fixture.Clock.Now;
        var probe = new MessageLeaseRequest(peer, null, int.MaxValue, long.MaxValue, now, new MessageLease(null, now.AddTicks(1)));
        var taken = await fixture.Memory.LeaseAsync(probe, CancellationToken.None);
        var ids = taken.Select(m => m.Id).ToArray();
        await fixture.Memory.ReleaseAsync(peer, ids, probe.Lease.Id, CancellationToken.None);
        _ = mediator;
        return ids;
    }
}
