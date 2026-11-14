using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// The push workers' live push (PR #109 review R1-2, FR-PICK-08/09, D6 step 1): the forward pump only
/// stores, and a worker pushes the stored record over the peer's live session — taken by id under
/// the session's own lease, given back at once when the session does not take it, at the deadline,
/// when the session closes or at shutdown; never sent when a pickup took it first; the newest live
/// session gets it; with direct push on, the live session is tried first and a live push that does
/// not go out falls back to the direct push.
/// </summary>
public sealed class LivePushTests
{
    private const string Recipient = "did:example:live-push-recipient";
    private static readonly Uri Inbox = new("https://live-push-host.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // A container whose mediator and peer hold real keys, the peer enrolled with Recipient, and one
    // live session with live delivery on (unless told otherwise).
    private sealed class Live : IAsyncDisposable
    {
        private Live(PipelinePeers peers, PeerIdentity mediator, PeerIdentity peer, PushFixture fixture)
        {
            Peers = peers;
            Mediator = mediator;
            Peer = peer;
            Fixture = fixture;
        }

        public PipelinePeers Peers { get; }
        public PeerIdentity Mediator { get; }
        public PeerIdentity Peer { get; }
        public PushFixture Fixture { get; }

        public static async Task<Live> StartAsync(bool directPush = false, Func<TransportRequest, CancellationToken, Task<TransportResult>>? send = null)
        {
            var peers = new PipelinePeers();
            var mediator = await peers.NewAsync();
            var peer = await peers.NewAsync();
            var fixture = PushFixture.Create(
                send,
                directory: _ => Inbox,
                configure: o => o.DirectPush = directPush,
                secrets: peers.Secrets,
                mediatorDid: mediator.Did);
            await fixture.EnrolAsync(peer.Did, Recipient);
            return new Live(peers, mediator, peer, fixture);
        }

        public async Task<LiveTestSession> SessionAsync()
        {
            var session = new LiveTestSession(Peer.Did, Mediator.Did);
            await Fixture.GoLiveAsync(session);
            return session;
        }

        // Stored without a live push of the queue's own (the mediator sink's way), and the job the
        // sink would hand over for it.
        public async Task<StoredMessage> StoredJobAsync(string forwardId = "fwd-live")
        {
            var result = await Fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(64), new PickupDeliveryOptions(AllowLive: false));
            result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            return new StoredMessage(result.GrantPeerDid!, Recipient, result.MessageId!);
        }

        // What a delivery-request would be handed now (under a lease of its own).
        public async Task<IReadOnlyList<string>> PickupAsync()
        {
            var now = Fixture.Time.GetUtcNow();
            var leased = await Fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer.Did, null, 10, int.MaxValue, now, new MessageLease("a-pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None);
            return leased.Select(m => m.Id).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await Fixture.DisposeAsync();
            await Peers.DisposeAsync();
        }
    }

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();

    [Fact]
    public async Task A_live_push_takes_what_was_stored_since_live_delivery_went_on_oldest_first_under_the_sessions_own_lease()
    {
        // L-020 (PR #109 review round 2, D-1): this test pinned "the push sends the record it was
        // handed by id". The push now takes what the store holds: every message stored since the
        // session turned live delivery on, oldest first; what was queued before stays for pickup
        // (FR-PICK-06).
        await using var live = await Live.StartAsync();
        var before = await live.StoredJobAsync("fwd-before");
        live.Fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var session = await live.SessionAsync();
        var first = await live.StoredJobAsync("fwd-first");
        var second = await live.StoredJobAsync("fwd-second");

        live.Fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        Deliveries(session).Select(d => d.Id).Should().Equal(first.MessageId, second.MessageId);
        live.Fixture.Store.MessageCount.Should().Be(3, "pushed records are held until they are acknowledged");
        (await live.PickupAsync()).Should().Equal(new[] { before.MessageId }, "the pushed records are under the session's lease; the earlier one was never pushed");

        // The leases are the session's: its close gives the records back to pickup.
        session.Close();
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Count == 2, "the session's close ends its leases");
    }

    public static TheoryData<string> NotTaken => new() { "NotSent", "Throws" };

    [Theory]
    [MemberData(nameof(NotTaken))]
    public async Task A_push_the_session_does_not_take_gives_the_lease_back_at_once_and_ends_the_operation(string how)
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var calls = 0;
        session.OnPush = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return how == "NotSent"
                ? Task.FromResult(ReturnRouteSendResult.NotSent)
                : Task.FromException<ReturnRouteSendResult>(new HandlerBoomException(HandlerBoomException.Marker));
        };
        var job = await live.StoredJobAsync();
        await live.StoredJobAsync("fwd-next");

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        calls.Should().Be(1, "the operation stops at the first push the session does not take");
        (await live.PickupAsync()).Should().HaveCount(2).And.Contain(job.MessageId, "a delivery-request gets it now, not when a lease runs out");
        PipelineLogs.Leaking(live.Fixture.Logs, live.Peer.Did, live.Mediator.Did, Recipient, job.MessageId, HandlerBoomException.Marker).Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_too_large_for_the_sessions_envelope_is_passed_over_as_a_pickup_does_and_the_next_one_is_pushed()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var calls = 0;
        session.OnPush = (_, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? ReturnRouteSendResult.TooLarge : ReturnRouteSendResult.Sent);
        var tooLarge = await live.StoredJobAsync("fwd-large");
        var next = await live.StoredJobAsync("fwd-next");

        live.Fixture.Dispatcher.Wake(tooLarge.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.PushedLive);
        Deliveries(session).Select(d => d.Id).Should().Equal(next.MessageId);
        (await live.PickupAsync()).Should().BeEmpty("the large one is passed over for now, the next one is leased to the session");
        live.Fixture.Time.Advance(new MessagePickupOptions().LeaseTimeout);
        (await live.PickupAsync()).Should().Contain(tooLarge.MessageId, "a pass-over ends at its instant, as a pickup's does");
    }

    [Fact]
    public async Task A_live_push_that_outlasts_the_deadline_is_abandoned_and_its_lease_given_back()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ReturnRouteSendResult.Sent;
        };
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        live.Fixture.Time.Advance(new MediatorOptions().PushTimeout);
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        // The worker stops waiting at the deadline; the send sees the cancellation and gives the
        // lease back on its own, in parallel with the worker.
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Contains(job.MessageId), "the cancelled live push gives its lease back");
    }

    [Fact]
    public async Task A_session_that_ignores_the_deadline_frees_the_worker_and_gets_no_lease_once_its_send_ends()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task; // never looks at the token
        };
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        live.Fixture.Time.Advance(new MediatorOptions().PushTimeout);
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        (await live.PickupAsync()).Should().BeEmpty("the abandoned send still holds the session's lease");

        release.SetResult(ReturnRouteSendResult.NotSent);
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Contains(job.MessageId), "the send that ended without going out gives the lease back");
    }

    [Fact]
    public async Task A_record_a_pickup_took_first_is_pushed_neither_live_nor_directly()
    {
        await using var live = await Live.StartAsync(directPush: true);
        var session = await live.SessionAsync();
        var job = await live.StoredJobAsync();
        (await live.PickupAsync()).Should().Equal(job.MessageId);

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        live.Fixture.PushOutcomeLog.Should().BeEmpty();
        Deliveries(session).Should().BeEmpty();
        live.Fixture.Router.Requests.Should().BeEmpty();
        live.Fixture.Store.MessageCount.Should().Be(1, "it stays with the pickup that took it");
    }

    [Fact]
    public async Task A_session_that_closes_during_the_push_leaves_the_message_for_pickup()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        session.OnPush = (_, _) =>
        {
            session.Close();
            return Task.FromResult(ReturnRouteSendResult.Sent);
        };
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Contains(job.MessageId), "a push into a closing session is not counted delivered");
    }

    [Fact]
    public async Task With_two_live_sessions_of_the_peer_the_newest_gets_the_push()
    {
        await using var live = await Live.StartAsync();
        var older = await live.SessionAsync();
        var newer = await live.SessionAsync();
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        Deliveries(newer).Should().ContainSingle().Which.Id.Should().Be(job.MessageId);
        Deliveries(older).Should().BeEmpty();
    }

    [Fact]
    public async Task With_direct_push_on_the_live_session_is_tried_first()
    {
        await using var live = await Live.StartAsync(directPush: true);
        var session = await live.SessionAsync();
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        Deliveries(session).Should().ContainSingle();
        live.Fixture.Router.Requests.Should().BeEmpty();
        live.Fixture.Directory!.Calls.Should().Be(0, "the directory is asked only when no live session took the message");
    }

    [Fact]
    public async Task With_direct_push_on_the_messages_queued_before_live_delivery_went_on_go_to_the_endpoint()
    {
        // L-020 (PR #109 review round 2, D-1): this test pinned "a live push the session does not take
        // falls back to the direct push". A live push the session does not take now ends the
        // operation (the message is tried again at the next wake-up); the direct push takes what the
        // live push may not — the messages stored before live delivery went on.
        await using var live = await Live.StartAsync(directPush: true);
        var before = await live.StoredJobAsync("fwd-before");
        live.Fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var session = await live.SessionAsync();
        var after = await live.StoredJobAsync("fwd-after");

        live.Fixture.Dispatcher.Wake(after.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (round 8): turning live delivery on wakes the grant, and that operation's direct step
        // sends the message queued before; the later wake-up pushes the new one live.
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed, PushOutcomes.PushedLive);
        Deliveries(session).Select(d => d.Id).Should().Equal(after.MessageId);
        live.Fixture.Router.Requests.Should().ContainSingle().Which.Endpoint.Should().Be(Inbox);
        live.Fixture.Store.MessageCount.Should().Be(1, "the live one is held for its acknowledgement; the direct one is removed");
        (await live.PickupAsync()).Should().BeEmpty();
        _ = before;
    }

    [Fact]
    public async Task A_live_push_the_session_does_not_take_ends_the_operation_and_is_tried_again_at_the_next_wake_up()
    {
        await using var live = await Live.StartAsync(directPush: true);
        var session = await live.SessionAsync();
        var refuse = 1;
        session.OnPush = (_, _) => Task.FromResult(Interlocked.Exchange(ref refuse, 0) == 1 ? ReturnRouteSendResult.NotSent : ReturnRouteSendResult.Sent);
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        live.Fixture.Router.Requests.Should().BeEmpty("a refused live push does not turn into a direct one");

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.PushedLive);
        Deliveries(session).Select(d => d.Id).Should().Equal(job.MessageId);
    }

    [Fact]
    public async Task Shutting_down_with_a_live_push_in_flight_gives_its_lease_back()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ReturnRouteSendResult.Sent;
        };
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        await live.Fixture.Dispatcher.DisposeAsync();

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedShutdown);
        // The worker stops waiting at once; the send it abandoned sees the cancellation and gives
        // the lease back on its own.
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Contains(job.MessageId), "the abandoned live push gives its lease back");
    }

    [Fact]
    public async Task The_sink_hands_over_nothing_when_direct_push_is_off_and_the_peer_is_not_live()
    {
        await using var live = await Live.StartAsync();

        var stored = await live.Fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(32), new PickupDeliveryOptions(AllowLive: false));
        live.Fixture.Queue.Coordinator.HasLiveTarget(stored.GrantPeerDid!).Should().BeFalse();
        var session = await live.SessionAsync();
        live.Fixture.Queue.Coordinator.HasLiveTarget(stored.GrantPeerDid!).Should().BeTrue();
        session.Close();
        live.Fixture.Queue.Coordinator.HasLiveTarget(stored.GrantPeerDid!).Should().BeFalse("a closed session is no live target");
    }

    // ---- the cooldown after a deadline (ADV-4 D3) ----------------------------------------------------

    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;

    [Fact]
    public async Task A_grant_whose_live_push_reaches_the_deadline_is_paused_and_its_wake_ups_never_take_a_worker()
    {
        // L-020 (PR #109 review round 2, D-2): there are no waiting jobs to skip any more; the
        // wake-ups during the cooldown are what is skipped.
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnPush = async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // the peer stalls its own push
            return ReturnRouteSendResult.Sent;
        };
        var first = await live.StoredJobAsync("fwd-0");

        live.Fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        live.Fixture.Time.Advance(PushTimeout);
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => live.Fixture.Dispatcher.Unsettled == 0, "the cancelled push settled");
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        // Woken during the cooldown: skipped at once, never on a worker.
        var during = await live.StoredJobAsync("fwd-during");
        live.Fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts - TimeSpan.FromTicks(1));
        live.Fixture.Dispatcher.Wake(during.GrantPeerDid).Should().BeFalse();
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown);
        Volatile.Read(ref calls).Should().Be(1);
        live.Fixture.Store.MessageCount.Should().Be(2, "nothing is lost: both wait for pickup");
        PipelineLogs.Leaking(live.Fixture.Logs, live.Peer.Did, live.Mediator.Did, Recipient).Should().BeEmpty();

        // The cooldown over, the grant's next wake-up pushes what is due, oldest first.
        live.Fixture.Time.Advance(TimeSpan.FromTicks(1));
        session.OnPush = null;
        live.Fixture.Dispatcher.Wake(during.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.SkippedCooldown, PushOutcomes.PushedLive, PushOutcomes.PushedLive);
        Deliveries(session).Select(d => d.Id).Should().Equal(first.MessageId, during.MessageId);
    }

    [Fact]
    public async Task A_direct_push_that_reaches_the_deadline_pauses_the_grant_too_and_its_grant_waits_for_the_abandoned_send()
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<TransportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var live = await Live.StartAsync(directPush: true, send: (_, _) =>
        {
            parked.TrySetResult();
            return never.Task; // the recipient's endpoint never answers and ignores the token
        });
        var first = await live.StoredJobAsync("fwd-first");

        live.Fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeTrue();
        await parked.Task.WaitAsync(Wait);
        live.Fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeTrue("woken again during its operation: served after it settles");
        live.Fixture.Time.Advance(PushTimeout);
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        live.Fixture.Dispatcher.Unsettled.Should().Be(1, "the abandoned send never ended");
        live.Fixture.Router.Requests.Should().ContainSingle("the grant is not served again while its push is unsettled");
        live.Fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeFalse("and it is paused");
        never.TrySetResult(new TransportResult(true, 500));
        await MessagePickupLive.UntilAsync(() => live.Fixture.Dispatcher.Unsettled == 0, "the abandoned send ended");
        live.Fixture.Router.Requests.Should().ContainSingle("the wake-up held during the operation fell in the cooldown");
        (await live.PickupAsync()).Should().Equal(first.MessageId);
    }

    [Fact]
    public async Task A_push_that_fails_before_the_deadline_pauses_nothing()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var calls = 0;
        session.OnPush = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ReturnRouteSendResult.NotSent);
        };
        var job = await live.StoredJobAsync("fwd-1");

        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);
        live.Fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed);
        Volatile.Read(ref calls).Should().Be(2);
        live.Fixture.Dispatcher.GrantEntries.Should().Be(0, "a grant that is neither woken, busy nor paused holds no entry");
    }

    [Fact]
    public async Task A_deadline_while_checking_the_registration_pauses_the_grant_like_any_push_deadline()
    {
        // L-020 (PR #109 review round 2, D-1): the registration is checked as part of taking the
        // record (exactly as a pickup does), inside the push and under its deadline; the dispatcher
        // no longer tells that step apart, so a deadline there pauses the grant like any other.
        var mediation = new ScriptedMediationStore();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync("did:example:check-peer", Recipient);
        var first = await fixture.StoreAsync(Recipient);
        mediation.OnFind = async (_, ct) =>
        {
            parked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeTrue();
        await parked.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Dispatcher.Wake(first.GrantPeerDid).Should().BeFalse("paused");
        await MessagePickupLive.UntilAsync(async () => (await fixture.Store.GetStatusAsync(first.GrantPeerDid, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount == 1, "the record went back to the queue");
    }

    [Fact]
    public async Task The_grant_entries_drop_the_cooldowns_that_ran_out()
    {
        var arrivals = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await arrivals.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        await fixture.EnrolAsync("did:example:cool-1", "did:example:cool-1-r");
        await fixture.EnrolAsync("did:example:cool-2", "did:example:cool-2-r");

        fixture.Dispatcher.Wake((await fixture.StoreAsync("did:example:cool-1-r")).GrantPeerDid).Should().BeTrue();
        await arrivals.WaitForArrivalsAsync(1, Wait);
        fixture.Time.Advance(PushTimeout);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the first push settled");
        fixture.Dispatcher.GrantEntries.Should().Be(1, "the first grant is paused");

        // The first grant's cooldown runs out; the second grant's deadline drops it from the entries.
        fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);
        fixture.Dispatcher.Wake((await fixture.StoreAsync("did:example:cool-2-r")).GrantPeerDid).Should().BeTrue();
        await arrivals.WaitForArrivalsAsync(2, Wait);
        fixture.Time.Advance(PushTimeout);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the second push settled");

        fixture.Dispatcher.GrantEntries.Should().Be(1, "only the second grant is paused now");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed);
    }
}
