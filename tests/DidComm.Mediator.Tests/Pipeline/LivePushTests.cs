using AwesomeAssertions;
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
        public async Task<PushJob> StoredJobAsync(string forwardId = "fwd-live")
        {
            var result = await Fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(64), new PickupDeliveryOptions(AllowLive: false));
            result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
            return new PushJob(result.GrantPeerDid!, Recipient, result.MessageId!, forwardId);
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
    public async Task A_live_push_takes_exactly_its_record_by_id_under_the_sessions_own_lease()
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        var other = await live.StoredJobAsync("fwd-other");
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        Deliveries(session).Should().ContainSingle().Which.Id.Should().Be(job.MessageId, "the push sends the record it was handed, not the oldest one");
        live.Fixture.Store.MessageCount.Should().Be(2, "a pushed record is held until it is acknowledged");
        (await live.PickupAsync()).Should().Equal(new[] { other.MessageId }, "the pushed record is under the session's lease");

        // The lease is the session's: its close gives the record back to pickup.
        session.Close();
        await MessagePickupLive.UntilAsync(async () => (await live.PickupAsync()).Contains(job.MessageId), "the session's close ends its lease");
    }

    public static TheoryData<string> NotTaken => new() { "NotSent", "TooLarge", "Throws" };

    [Theory]
    [MemberData(nameof(NotTaken))]
    public async Task A_push_the_session_does_not_take_gives_the_lease_back_at_once(string how)
    {
        await using var live = await Live.StartAsync();
        var session = await live.SessionAsync();
        session.OnPush = (_, _) => how switch
        {
            "NotSent" => Task.FromResult(ReturnRouteSendResult.NotSent),
            "TooLarge" => Task.FromResult(ReturnRouteSendResult.TooLarge),
            _ => Task.FromException<ReturnRouteSendResult>(new HandlerBoomException(HandlerBoomException.Marker)),
        };
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        (await live.PickupAsync()).Should().Equal(new[] { job.MessageId }, "a delivery-request gets it now, not when a lease runs out");
        PipelineLogs.Leaking(live.Fixture.Logs, live.Peer.Did, live.Mediator.Did, Recipient, job.MessageId, HandlerBoomException.Marker).Should().BeEmpty();
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNotHeld);
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        Deliveries(session).Should().ContainSingle();
        live.Fixture.Router.Requests.Should().BeEmpty();
        live.Fixture.Directory!.Calls.Should().Be(0, "the directory is asked only when no live session took the message");
    }

    [Fact]
    public async Task A_live_push_the_session_does_not_take_falls_back_to_the_direct_push_when_it_is_on()
    {
        await using var live = await Live.StartAsync(directPush: true);
        var session = await live.SessionAsync();
        session.OnPush = (_, _) => Task.FromResult(ReturnRouteSendResult.NotSent);
        var job = await live.StoredJobAsync();

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await live.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        live.Fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Pushed }, "one outcome per job: the direct push's");
        live.Fixture.Router.Requests.Should().ContainSingle().Which.Endpoint.Should().Be(Inbox);
        live.Fixture.Store.MessageCount.Should().Be(0);
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

        live.Fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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
}
