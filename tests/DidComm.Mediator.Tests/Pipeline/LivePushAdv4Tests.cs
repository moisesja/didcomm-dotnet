using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-4 (#95, PR #109 review R1-2): a break-it pass on the revised live push — the forward sink
/// only stores, and the push workers push a stored record over the peer's live session. Every test
/// drives the real sink (as the forward pump calls it) and the real dispatcher in a container built
/// by <c>AddMediator()</c>, with a manual clock for the push deadline, and forces each interleaving
/// with a rendezvous (L-044). Tests marked <c>Skip</c> are confirmed defects: each fails when the
/// skip is removed.
/// </summary>
public sealed class LivePushAdv4Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan LeaseTimeout = new MessagePickupOptions().LeaseTimeout;

    // ---- 1. isolation ------------------------------------------------------------------------------

    [Fact]
    public async Task Isolation_a_grant_whose_live_push_never_returns_holds_up_neither_the_sink_nor_another_grants_live_push()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var b = await world.GrantAsync("b");
        var sessionA = await world.LiveAsync(a);
        var sessionB = await world.LiveAsync(b);
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionA.OnPush = (_, _) =>
        {
            parked.TrySetResult();
            return never.Task; // A controls this: it never returns and never looks at its token
        };

        // The pump hands A twenty forwards; each call returns although A's first push never ends.
        for (var i = 0; i < 20; i++)
            await world.ForwardAsync(a.Recipient, (byte)i).WaitAsync(Wait);
        await parked.Task.WaitAsync(Wait);

        // B: stored, handed over and pushed live by a free worker while A's push is still parked.
        await world.ForwardAsync(b.Recipient, 100).WaitAsync(Wait);
        await MessagePickupLive.UntilAsync(() => Task.FromResult(LivePushWorld.Deliveries(sessionB).Count == 1), "B is pushed live while A's push is parked");

        world.Fixture.ForwardOutcomeLog.Should().HaveCount(21).And.OnlyContain(o => o == ForwardOutcomes.Queued);
        // L-020 (ADV-4 D1): A's share of the backlog is now its queue quota, so none of its twenty is refused.
        world.Fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(0, "every stored message of A keeps its job");
        world.Fixture.Pushes(PushOutcomes.PushedLive).Should().Be(1);
        (await world.StatusAsync(a.Peer)).Should().Be(20, "every forward for A is stored");
        LivePushWorld.Deliveries(sessionA).Should().BeEmpty();
        never.Task.IsCompleted.Should().BeFalse("all of this happened with A's push still parked");
    }

    [Fact]
    public async Task With_every_worker_held_by_a_stalled_grant_another_live_recipient_is_pushed_within_one_push_timeout()
    {
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 2);
        var a1 = await world.GrantAsync("a1");
        var a2 = await world.GrantAsync("a2");
        var b = await world.GrantAsync("b");
        var arrived = new CountdownEvent(2);
        foreach (var stalled in new[] { await world.LiveAsync(a1), await world.LiveAsync(a2) })
        {
            stalled.OnPush = async (_, ct) =>
            {
                arrived.Signal();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ReturnRouteSendResult.Sent;
            };
        }
        var sessionB = await world.LiveAsync(b);

        await world.ForwardAsync(a1.Recipient, 1);
        await world.ForwardAsync(a2.Recipient, 2);
        arrived.Wait(Wait).Should().BeTrue("both workers are parked in A1's and A2's pushes");
        await world.ForwardAsync(b.Recipient, 3);

        world.Fixture.Time.Advance(PushTimeout);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(sessionB).Should().ContainSingle("B waits for a worker at most one push timeout");
        world.Fixture.PushOutcomeLog.Should().BeEquivalentTo(new[] { PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedLive });
    }

    // ---- 2. leases and races ------------------------------------------------------------------------

    [Fact]
    public async Task A_lease_the_live_push_records_after_the_sessions_close_time_release_ran_is_given_back_at_once()
    {
        var store = new ScriptedMessageStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var inTake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // L-020 (PR #109 review round 2, D-1): the push takes the next record with LeaseAsync, no longer by id.
        store.OnLease = (request, _) =>
        {
            inTake.TrySetResult();
            return new ValueTask<IReadOnlyList<QueuedMessage>>(TakeAsync());

            async Task<IReadOnlyList<QueuedMessage>> TakeAsync()
            {
                await proceed.Task;
                return await store.Inner.LeaseAsync(request, CancellationToken.None);
            }
        };

        await world.ForwardAsync(a.Recipient, 1);
        await inTake.Task.WaitAsync(Wait); // MarkLeasing has run; the lease does not exist yet
        session.Close();
        var tracker = world.Fixture.Queue.Coordinator.Tracker;
        await MessagePickupLive.UntilAsync(() => Task.FromResult(tracker.PendingReleases == 0), "the close-time release ran before the lease exists");
        proceed.SetResult();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        world.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        LivePushWorld.Deliveries(session).Should().BeEmpty("a closed session is sent nothing");
        (await world.PickupFromAsync(store.Inner, a.Peer)).Should().ContainSingle("the lease recorded after the close-time release is given back by the push itself, not left to its timeout");
    }

    [Fact]
    public async Task A_pickup_that_takes_the_record_while_the_live_push_is_inside_its_take_wins_and_nothing_is_sent_twice()
    {
        var store = new ScriptedMessageStore();
        await using var world = await LivePushWorld.StartAsync(services: s => s.AddSingleton<IMessageStore>(store));
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var inTake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // L-020 (PR #109 review round 2, D-1): the push takes the next record with LeaseAsync, no longer by id.
        store.OnLease = (request, _) =>
        {
            inTake.TrySetResult();
            return new ValueTask<IReadOnlyList<QueuedMessage>>(TakeAsync());

            async Task<IReadOnlyList<QueuedMessage>> TakeAsync()
            {
                await proceed.Task;
                return await store.Inner.LeaseAsync(request, CancellationToken.None);
            }
        };

        await world.ForwardAsync(a.Recipient, 1);
        await inTake.Task.WaitAsync(Wait);
        (await world.PickupFromAsync(store.Inner, a.Peer)).Should().ContainSingle("a delivery-request on another connection takes it first");
        proceed.SetResult();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        world.Fixture.PushOutcomeLog.Should().BeEmpty();
        LivePushWorld.Deliveries(session).Should().BeEmpty();
        (await world.PickupFromAsync(store.Inner, a.Peer)).Should().BeEmpty("it stays with the pickup that took it");
    }

    [Fact]
    public async Task An_acknowledgement_that_lands_while_the_live_push_is_in_flight_leaves_nothing_behind_and_one_outcome()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var stored = await world.Fixture.Queue.DeliverAsync(a.Recipient, PushFixture.Payload(64), new PickupDeliveryOptions(AllowLive: false));
        world.Fixture.Dispatcher.Wake(stored.GrantPeerDid!).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        (await world.Fixture.Queue.RemoveAsync(a.Peer, stored.MessageId!)).Should().BeTrue();
        release.SetResult(ReturnRouteSendResult.NotSent);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        world.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        world.Fixture.Store.MessageCount.Should().Be(0, "the give-back of a removed record restores nothing");
    }

    [Fact]
    public async Task An_abandoned_live_send_that_still_goes_out_is_not_delivered_again_within_its_lease_and_returns_to_pickup_when_the_lease_ends()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task; // ignores the deadline
        };

        await world.ForwardAsync(a.Recipient, 1);
        await entered.Task.WaitAsync(Wait);
        world.Fixture.Time.Advance(PushTimeout);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        world.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        release.SetResult(ReturnRouteSendResult.Sent);
        await MessagePickupLive.UntilAsync(() => Task.FromResult(LivePushWorld.Deliveries(session).Count == 1), "the abandoned send went out");
        (await world.PickupAsync(a.Peer)).Should().BeEmpty("the session that was sent it holds it: no second copy within the lease");
        world.Fixture.Time.Advance(LeaseTimeout);
        (await world.PickupAsync(a.Peer)).Should().ContainSingle("unacknowledged, it returns to pickup when the lease ends (at-least-once)");
    }

    [Fact]
    public async Task An_abandoned_live_send_that_reports_sent_after_its_session_closed_gives_the_lease_back()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        await world.ForwardAsync(a.Recipient, 1);
        await entered.Task.WaitAsync(Wait);
        world.Fixture.Time.Advance(PushTimeout);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        session.Close();
        var tracker = world.Fixture.Queue.Coordinator.Tracker;
        await MessagePickupLive.UntilAsync(() => Task.FromResult(tracker.PendingReleases == 0), "the close-time release ran");
        release.SetResult(ReturnRouteSendResult.Sent);

        await MessagePickupLive.UntilAsync(async () => (await world.PickupAsync(a.Peer)).Count == 1, "a send that ends after its session closed holds no lease");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_job_waiting_while_its_grant_is_revoked_and_purged_sends_nothing_even_when_the_peer_is_granted_again(bool regrant)
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        session.OnPush = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(ReturnRouteSendResult.Sent);
            entered.TrySetResult();
            return release.Task;
        };

        await world.ForwardAsync(a.Recipient, 1);
        await entered.Task.WaitAsync(Wait);
        await world.ForwardAsync(a.Recipient, 2); // its wake-up waits behind the parked push

        (await world.Fixture.Queue.RevokeAndPurgeAsync(a.Peer)).Purged.Should().Be(2);
        if (regrant)
            await world.Fixture.EnrolAsync(a.Peer, a.Recipient);
        release.SetResult(ReturnRouteSendResult.NotSent);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        world.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        LivePushWorld.Deliveries(session).Should().BeEmpty("a purged record is never pushed, not even to the same peer granted again");
        world.Fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task Review_round_2_live_sends_that_ignore_cancellation_never_pile_up_across_deadlines_session_churn_and_cooldowns()
    {
        // PR #109 review round 2, finding 2, through the real sink and dispatcher: a live send that
        // ignores cancellation, its deadline, the session closed and reopened, the cooldown run out,
        // a pickup and an acknowledgement — cycle after cycle. On 755c497 every cycle left one more
        // send pending (6 after six cycles) while the dispatcher counted none. Now a grant has one
        // operation until its work has actually ended (D-3), so at most one send is ever pending,
        // while a healthy recipient keeps getting its pushes and the stalled one its pickups.
        const int Cycles = 8;
        await using var world = await LivePushWorld.StartAsync(o => o.PushConcurrency = 1);
        var a = await world.GrantAsync("a");
        var b = await world.GrantAsync("b");
        var healthy = await world.LiveAsync(b);
        var pending = 0;
        var maxPending = 0;
        var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            var stalled = await world.LiveAsync(a);
            stalled.OnPush = (_, _) =>
            {
                var now = Interlocked.Increment(ref pending);
                int seen;
                while ((seen = Volatile.Read(ref maxPending)) < now && Interlocked.CompareExchange(ref maxPending, now, seen) != seen)
                {
                }
                firstSend.TrySetResult();
                return new TaskCompletionSource<ReturnRouteSendResult>().Task; // never ends, never looks at its token
            };

            await world.ForwardAsync(a.Recipient, (byte)cycle);
            if (cycle == 0)
                await firstSend.Task.WaitAsync(Wait);
            world.Fixture.Time.Advance(PushTimeout);
            await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            stalled.Close();
            world.Fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);

            // A healthy recipient still gets its live push, and acknowledges it.
            await world.ForwardAsync(b.Recipient, (byte)(100 + cycle));
            await MessagePickupLive.UntilAsync(() => Task.FromResult(LivePushWorld.Deliveries(healthy).Count == cycle + 1), "the healthy recipient's push goes out");
            await world.Fixture.Queue.RemoveAsync(b.Peer, LivePushWorld.Deliveries(healthy)[^1].Id);

            // The stalled recipient still picks up and acknowledges what it was sent.
            var picked = new List<string>();
            await MessagePickupLive.UntilAsync(async () =>
            {
                picked.AddRange(await world.PickupAsync(a.Peer));
                return picked.Count > 0;
            }, "the stalled recipient picks up its message");
            foreach (var id in picked)
                await world.Fixture.Queue.RemoveAsync(a.Peer, id);
        }

        Volatile.Read(ref maxPending).Should().Be(1, "one operation per grant until its work has ended: the abandoned send is never joined by another");
        world.Fixture.Dispatcher.Unsettled.Should().Be(1, "and the dispatcher still counts it");
        LivePushWorld.Deliveries(healthy).Should().HaveCount(Cycles);
        (await world.StatusAsync(a.Peer)).Should().Be(0, "every message of the stalled recipient was picked up and acknowledged");
    }

    // ---- 3. loss and starvation; 4. order; 5. accounting ----------------------------------------------

    [Fact]
    public async Task A_recipient_whose_live_session_never_acknowledges_and_that_polls_elsewhere_once_per_lease_period_gets_every_message()
    {
        // L-056/L-058: the slowest honest client. The live session takes every push and never
        // acknowledges or closes (a backgrounded app); the recipient polls on another connection
        // once per lease period plus a second, while a message arrives before every poll.
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var fetched = new List<string>();

        for (var round = 0; round < 5; round++)
        {
            await world.ForwardAsync(a.Recipient, (byte)round);
            await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            world.Fixture.Time.Advance(LeaseTimeout + TimeSpan.FromSeconds(1));
            var ids = await world.PickupAsync(a.Peer);
            foreach (var id in ids)
                await world.Fixture.Queue.RemoveAsync(a.Peer, id); // the poller acknowledges what it got
            fetched.AddRange(ids);
        }

        LivePushWorld.Deliveries(session).Should().HaveCount(5, "each message was pushed once");
        fetched.Should().BeEquivalentTo(LivePushWorld.Deliveries(session).Select(d => d.Id), "each lease ended before the next poll, so every poll got what was pushed before it");
        world.Fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_burst_while_the_first_push_is_slow_loses_nothing_keeps_order_counts_one_outcome_per_message_and_later_messages_are_pushed_again()
    {
        // L-020 (ADV-4 D1): this test asserted the defect — sixteen pushed, four refused by the
        // 16-job share and left for pickup. The share is now the queue quota: all twenty are pushed.
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await open.Task.WaitAsync(ct);
            return ReturnRouteSendResult.Sent;
        };

        for (var i = 0; i < 20; i++)
            await world.ForwardAsync(a.Recipient, (byte)i);
        await entered.Task.WaitAsync(Wait);
        open.SetResult();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        // Order: the pushes go out in the order the forwards arrived, all twenty.
        LivePushWorld.Deliveries(session).Select(d => d.Payload.Span[0]).Should().Equal(Enumerable.Range(0, 20).Select(i => LivePushWorld.Seed(i)));
        // Accounting: one forward outcome and one push outcome per message.
        world.Fixture.ForwardOutcomeLog.Should().HaveCount(20).And.OnlyContain(o => o == ForwardOutcomes.Queued);
        world.Fixture.Pushes(PushOutcomes.PushedLive).Should().Be(20);
        world.Fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(0);
        world.Fixture.PushOutcomeLog.Should().HaveCount(20);
        world.Fixture.Store.MessageCount.Should().Be(20, "the jobs take nothing from the quota; pushed records wait for their acknowledgement");
        // No loss and no double hand-out: every record is leased to the session.
        (await world.PickupAsync(a.Peer)).Should().BeEmpty();

        // A later message is pushed live again once the line drained.
        await world.ForwardAsync(a.Recipient, 50);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        LivePushWorld.Deliveries(session).Should().HaveCount(21);
        LivePushWorld.Deliveries(session)[^1].Payload.Span[0].Should().Be(LivePushWorld.Seed(50));
    }

    [Fact]
    public async Task Every_message_that_arrives_while_live_delivery_is_on_is_pushed_over_the_live_session()
    {
        // Pickup 3.0: live mode is "delivering newly arriving messages directly to a connected
        // recipient"; a recipient in its mode 2 retrieves the queue once, then turns live mode on and
        // never polls again. Before the redesign (bbb1fee) the sink pushed every due message inline.
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await open.Task.WaitAsync(ct); // the first push is slow (a pack, a resolution)
            return ReturnRouteSendResult.Sent;
        };

        for (var i = 0; i < 20; i++)
            await world.ForwardAsync(a.Recipient, (byte)i);
        await entered.Task.WaitAsync(Wait);
        open.SetResult();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Should().HaveCount(20, "all twenty arrived while the session had live delivery on");
    }

    [Fact]
    public async Task The_push_backlog_retains_bounded_values_for_a_forward_with_a_hostile_id()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await open.Task.WaitAsync(ct);
            return ReturnRouteSendResult.Sent;
        };

        var hostile = new string('x', 512 * 1024);
        for (var i = 0; i < 16; i++)
            await world.ForwardAsync(a.Recipient, (byte)i, forwardId: hostile + i);
        await entered.Task.WaitAsync(Wait);

        var perJob = 2 * Consistency.IdentifierLimits.MaxDidLength + PickupSizing.MaxMintedIdLength + DidComm.Diagnostics.LogValues.MaxIdLength;
        world.Fixture.Dispatcher.RetainedCharacters.Should().BeLessThanOrEqualTo(16L * perJob);
        world.Fixture.Dispatcher.RetainedCharacters.Should().BeLessThan(hostile.Length, "no job keeps the wire id");
        open.SetResult();
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        world.Fixture.Dispatcher.RetainedCharacters.Should().Be(0);
    }

    // ---- 6. hosts -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_live_delivery_concurrent_with_the_workers_live_push_to_the_same_peer_delivers_each_message_once()
    {
        await using var world = await LivePushWorld.StartAsync();
        var a = await world.GrantAsync("a");
        var session = await world.LiveAsync(a);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        session.OnPush = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(ReturnRouteSendResult.Sent);
            entered.TrySetResult();
            return release.Task;
        };

        await world.ForwardAsync(a.Recipient, 1);
        await entered.Task.WaitAsync(Wait);
        var host = await world.Fixture.Queue.DeliverAsync(a.Recipient, PushFixture.Payload(64, LivePushWorld.Seed(2)));
        host.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "the host's own live delivery is not held up by the worker's push");
        release.SetResult(ReturnRouteSendResult.Sent);
        await world.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        world.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        (await world.PickupAsync(a.Peer)).Should().BeEmpty("both are leased to the session that was sent them");
        world.Fixture.Store.MessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Disposing_the_container_while_a_live_push_ignores_its_cancellation_returns_promptly_and_loses_nothing()
    {
        var world = await LivePushWorld.StartAsync();
        try
        {
            var a = await world.GrantAsync("a");
            var session = await world.LiveAsync(a);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnPush = (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            };
            var store = world.Fixture.Store;
            var outcomes = world.Fixture.Recorder;
            var dispatcher = world.Fixture.Dispatcher;

            await world.ForwardAsync(a.Recipient, 1);
            await entered.Task.WaitAsync(Wait);
            await world.ForwardAsync(a.Recipient, 2);
            await world.Fixture.Services.DisposeAsync().AsTask().WaitAsync(Wait);

            // L-020 (PR #109 review round 2, D-2/D-3): the second forward's wake-up waits behind the
            // push in flight (its grant is busy until that push settles), so it is counted when the
            // abandoned push ends, not at the shutdown.
            outcomes.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag)
                .Should().Equal(PushOutcomes.SkippedShutdown);
            store.MessageCount.Should().Be(2, "nothing is lost by the shutdown");
            release.SetResult(ReturnRouteSendResult.NotSent);
            await MessagePickupLive.UntilAsync(() => Task.FromResult(dispatcher.Unsettled == 0), "the abandoned push settled");
            outcomes.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag)
                .Should().Equal(PushOutcomes.SkippedShutdown, PushOutcomes.SkippedShutdown);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await MessagePickupLive.UntilAsync(async () =>
            {
                // Each poll leases what it finds, so what it found is remembered across polls.
                seen.UnionWith(await world.PickupFromAsync(store, a.Peer));
                return seen.Count == 2;
            }, "the abandoned send gave its lease back after the container was gone");
        }
        finally
        {
            await world.DisposeAsync();
        }
    }
}
