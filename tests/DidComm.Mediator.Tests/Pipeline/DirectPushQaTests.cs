using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Exceptions;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95): <see cref="PushDispatcher"/> at the component level — the status-code and
/// endpoint matrices, the re-check, saturation and recovery, exactly one outcome per job, shutdown
/// with a throwing cancellation callback, fairness between grants, and what the dispatcher does
/// with a stored message whose expiry or age limit passes while its job waits (FR-ROUTE-09,
/// FR-PICK-10: "never delivered, pushed or picked up").
/// </summary>
public sealed class DirectPushQaTests
{
    private const string Peer = "did:example:qa-push-peer";
    private const string Recipient = "did:example:qa-push-recipient";
    private const string OtherPeer = "did:example:qa-other-peer";
    private const string OtherRecipient = "did:example:qa-other-recipient";
    private static readonly Uri Inbox = new("https://qa-recipient-host.example/qa-inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static void NoLeaks(PushFixture fixture, params string[] more)
        => PipelineLogs.Leaking(fixture.Logs, new[] { Peer, Recipient, OtherPeer, OtherRecipient, "qa-recipient-host", "qa-inbox", HandlerBoomException.Marker }.Concat(more).ToArray())
            .Should().BeEmpty("no line may carry a DID, the endpoint, a queue id or an exception message");

    // ── the status-code matrix ────────────────────────────────────────────────────────────

    public static TheoryData<int?, bool, string> StatusMatrix => new()
    {
        { 199, true, PushOutcomes.Failed },
        { 200, true, PushOutcomes.Pushed },
        { 202, true, PushOutcomes.Pushed },
        { 204, true, PushOutcomes.Pushed },
        { 299, true, PushOutcomes.Pushed },
        { 300, true, PushOutcomes.Failed },
        { 302, true, PushOutcomes.Failed },
        { 400, true, PushOutcomes.Failed },
        { 404, true, PushOutcomes.Failed },
        { 500, true, PushOutcomes.Failed },
        { 503, true, PushOutcomes.Failed },
        { 200, false, PushOutcomes.Failed },
        { 204, false, PushOutcomes.Failed },
        { null, true, PushOutcomes.Failed },
        { null, false, PushOutcomes.Failed },
        { 0, true, PushOutcomes.Failed },
        { -1, true, PushOutcomes.Failed },
    };

    [Theory]
    [MemberData(nameof(StatusMatrix))]
    public async Task Only_an_accepted_answer_with_a_2xx_status_removes_the_queued_copy(int? status, bool accepted, string expected)
    {
        await using var fixture = PushFixture.Create((_, _) => Task.FromResult(new TransportResult(accepted, status)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().ContainSingle();
        fixture.PushOutcomeLog.Should().Equal(expected);
        fixture.Store.MessageCount.Should().Be(expected == PushOutcomes.Pushed ? 0 : 1);
        NoLeaks(fixture, job.MessageId);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(301)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_transport_exception_whatever_its_status_is_a_failure_logged_by_type(int status)
    {
        await using var fixture = PushFixture.Create((r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, status)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains(nameof(TransportException), StringComparison.Ordinal));
        NoLeaks(fixture, job.MessageId);
    }

    // ── the endpoint matrix ───────────────────────────────────────────────────────────────

    public static TheoryData<string, string> Endpoints => new()
    {
        { "HTTPS://QA-RECIPIENT-HOST.example/qa-inbox", PushOutcomes.Pushed },     // scheme and host are case-insensitive
        { "http://qa-recipient-host.example:8080/qa-inbox", PushOutcomes.Pushed },
        { "https://[::1]:8443/qa-inbox", PushOutcomes.Pushed },                       // loopback IPv6; the fixture's guard allows private networks
        { "http://127.0.0.1/qa-inbox", PushOutcomes.Pushed },
        { "https://qa-user:qa-secret-pw@qa-recipient-host.example/qa-inbox", PushOutcomes.Pushed },
        { "ws://qa-recipient-host.example/qa-inbox", PushOutcomes.SkippedScheme },
        { "wss://qa-recipient-host.example/qa-inbox", PushOutcomes.SkippedScheme },
        { "WSS://qa-recipient-host.example/qa-inbox", PushOutcomes.SkippedScheme },
        { "ftp://qa-recipient-host.example/qa-inbox", PushOutcomes.SkippedScheme },
        { "file:///qa-inbox", PushOutcomes.SkippedScheme },
        { "didcomm:transport/queue", PushOutcomes.SkippedScheme },
        { "did:example:qa-recipient-host", PushOutcomes.SkippedScheme },
        { "mailto:qa@qa-recipient-host.example", PushOutcomes.SkippedScheme },
        { "qa-inbox/relative", PushOutcomes.SkippedScheme },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Each_endpoint_shape_gets_its_one_outcome_and_no_line_names_it(string endpoint, string expected)
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri(endpoint, UriKind.RelativeOrAbsolute));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(expected);
        fixture.Router.Requests.Should().HaveCount(expected == PushOutcomes.Pushed ? 1 : 0);
        fixture.Store.MessageCount.Should().Be(expected == PushOutcomes.Pushed ? 0 : 1);
        NoLeaks(fixture, job.MessageId, "qa-secret-pw", "qa-user", "[::1]", "127.0.0.1", "8443");
    }

    [Fact]
    public async Task A_failing_push_to_an_endpoint_with_user_information_never_logs_the_password()
    {
        await using var fixture = PushFixture.Create(
            (r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)),
            directory: _ => new Uri("https://qa-user:qa-secret-pw@qa-recipient-host.example/qa-inbox"));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        NoLeaks(fixture, "qa-secret-pw", "qa-user");
    }

    [Fact]
    public async Task With_private_networks_blocked_a_loopback_endpoint_is_refused_by_the_guard_and_the_message_stays()
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri("http://127.0.0.1:9/qa-inbox"), blockPrivateNetworks: true);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty("the guard runs before the transport");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
        NoLeaks(fixture, "127.0.0.1");
    }

    // ── the directory and the re-check, made to throw on each path (L-054) ────────────────

    [Fact]
    public async Task A_directory_that_throws_every_time_skips_every_push_and_is_asked_again_each_time()
    {
        var calls = 0;
        await using var fixture = PushFixture.Create(directory: _ =>
        {
            Interlocked.Increment(ref calls);
            throw new HandlerBoomException(HandlerBoomException.Marker + " " + Recipient + " " + Inbox);
        });
        await fixture.EnrolAsync(Peer, Recipient);

        // L-020 (PR #109 review round 2, D-2): wake-ups coalesce, so each is served on its own here;
        // the directory is asked for each message a push takes, again every time.
        for (var i = 0; i < 3; i++)
        {
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        calls.Should().Be(3);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint, PushOutcomes.SkippedNoEndpoint, PushOutcomes.SkippedNoEndpoint);
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(3);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_re_check_that_throws_is_a_failure_and_never_a_push()
    {
        var mediation = new ScriptedMediationStore();
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        mediation.OnFind = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker + " " + Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty("a registration that cannot be confirmed is never pushed to");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
        NoLeaks(fixture, job.MessageId);
    }

    [Fact]
    public async Task A_re_check_that_hangs_is_cut_by_the_deadline_and_never_a_push()
    {
        var mediation = new ScriptedMediationStore();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        mediation.OnFind = async (_, ct) =>
        {
            parked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await parked.Task.WaitAsync(Wait);
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains("Timeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_re_check_that_returns_a_grant_with_another_peer_is_unregistered()
    {
        var mediation = new ScriptedMediationStore();
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        mediation.OnFind = (_, _) => ValueTask.FromResult<MediationGrant?>(new MediationGrant(Peer + "x", new[] { PushFixture.MediatorDid }));

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    // ── saturation and recovery ──────────────────────────────────────────────────────────

    [Fact]
    public async Task With_every_operation_unsettled_a_wake_up_starts_nothing_and_the_next_one_after_they_settle_is_served()
    {
        // L-020 (PR #109 review round 2, D-4): the per-grant and total job bounds are gone with the
        // jobs; what is bounded now is the work that has not ended — at most 2 x PushConcurrency
        // operations between start and settlement, abandoned ones included. Beyond it a wake-up
        // starts nothing (Push-Skipped-Saturated) and its message stays queued.
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, _) => { await gate.PassAsync(CancellationToken.None); return new TransportResult(true, 202); }, // ignores the deadline
            directory: _ => Inbox,
            configure: o => o.PushConcurrency = 1);
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.EnrolAsync(OtherPeer, OtherRecipient);
        await fixture.EnrolAsync("did:example:qa-third-peer", "did:example:qa-third-recipient");
        fixture.Dispatcher.MaxUnsettled.Should().Be(2);

        foreach (var (recipient, n) in new[] { (Recipient, 1), (OtherRecipient, 2) })
        {
            fixture.Dispatcher.Wake((await fixture.StoreAsync(recipient)).GrantPeerDid).Should().BeTrue();
            await gate.WaitForArrivalsAsync(n, Wait);
            fixture.Time.Advance(TimeSpan.FromSeconds(30));
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        fixture.Dispatcher.Unsettled.Should().Be(2, "both abandoned sends still run");

        var third = await fixture.StoreAsync("did:example:qa-third-recipient");
        fixture.Dispatcher.Wake(third.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(1);
        fixture.Router.Requests.Should().HaveCount(2, "the third grant's message was not sent");

        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned sends ended");
        fixture.Dispatcher.Wake(third.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(1, "the third grant's message, at its next wake-up");
        fixture.Store.MessageCount.Should().Be(0, "the abandoned sends' late 2xx removed their copies too");
    }

    [Fact]
    public async Task A_payload_of_any_admissible_size_is_taken_when_idle()
    {
        // L-020: this test pinned the refusal of a payload larger than the pinned-bytes budget. A push
        // pins no payload (it takes what the store holds), so any message the queue admitted can be pushed.
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient, PushFixture.Payload(fixture.Queue.MaxAdmissiblePayloadBytes))).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        fixture.Store.MessageCount.Should().Be(0);
    }

    // ── exactly one outcome per push attempt ──────────────────────────────────────────────

    [Fact]
    public async Task A_failed_push_ends_its_operation_and_the_message_is_tried_again_at_the_next_wake_up_with_one_outcome_per_attempt()
    {
        // L-020 (PR #109 review round 2, D-1/D-2): this test counted one outcome per job. Wake-ups
        // now coalesce and an operation stops at its first failure, so the property is one outcome
        // per push attempt (and one per wake-up that pushed nothing), and a failed message's retry
        // at the grant's next wake-up.
        var n = 0;
        await using var fixture = PushFixture.Create(
            (r, _) => Interlocked.Increment(ref n) % 2 == 0
                ? Task.FromResult(new TransportResult(true, 202))
                : Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)),
            directory: peer => peer == OtherPeer ? new Uri("wss://qa-recipient-host.example/qa-inbox") : Inbox,
            configure: o => o.Pickup.MaxQueueMessages = 50);
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.EnrolAsync(OtherPeer, OtherRecipient);
        for (var i = 0; i < 12; i++)
            await fixture.StoreAsync(i % 3 == 0 ? OtherRecipient : Recipient);

        fixture.Dispatcher.Wake(OtherPeer).Should().BeTrue();
        for (var wake = 0; wake < 3; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Pushes(PushOutcomes.SkippedScheme).Should().Be(1, "one wake-up of the grant whose endpoint is not http(s)");
        fixture.Pushes(PushOutcomes.Failed).Should().Be(3, "each of the three operations stopped at its failure");
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(2, "the failed message went out at the next wake-up");
        fixture.Router.Requests.Should().HaveCount(5, "one request per counted attempt");
        fixture.Store.MessageCount.Should().Be(10);
        fixture.Dispatcher.Unsettled.Should().Be(0);
    }

    // ── shutdown ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disposing_while_a_push_holds_a_throwing_cancellation_callback_neither_throws_nor_loses_the_message()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) =>
            {
                ct.Register(() => throw new HandlerBoomException(HandlerBoomException.Marker));
                await gate.PassAsync(ct);
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox,
            configure: o => o.PushConcurrency = 1);
        await fixture.EnrolAsync(Peer, Recipient);
        var dispatcher = fixture.Dispatcher;
        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();

        dispatcher.Invoking(d => d.Dispose()).Should().NotThrow("the shutdown cancels inside a try");
        await dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => dispatcher.Unsettled == 0, "the cancelled push settled");

        // The push in flight, and the wake-up that arrived during it: each skipped once.
        fixture.PushOutcomeLog.Should().HaveCount(2).And.OnlyContain(o => o == PushOutcomes.SkippedShutdown);
        fixture.Store.MessageCount.Should().Be(2, "both messages stay queued for pickup");
        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeFalse();
        fixture.Pushes(PushOutcomes.SkippedShutdown).Should().Be(3);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_DisposeAsync_after_Dispose_returns()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        var dispatcher = fixture.Dispatcher;

        dispatcher.Dispose();
        dispatcher.Dispose();
        await dispatcher.DisposeAsync().AsTask().WaitAsync(Wait);
    }

    // ── fairness between grants ───────────────────────────────────────────────────────────

    // QA-1 DEFECT 2 (P3), fixed by Q-4: grants are served in turn with at most one push in flight
    // per grant, so a recipient whose endpoint hangs holds one worker, never all of them. L-020: the
    // draft waited for four slow arrivals (four workers inside the slow endpoint); with the fix only
    // one of the grant's pushes runs at a time, so it waits for one. The "today" pin that asserted the
    // other grant waits for the slow timeouts is removed with the fix (it encoded the defect).
    [Fact]
    public async Task A_slow_grant_with_as_many_jobs_as_workers_does_not_hold_up_another_grants_push()
    {
        var slow = new Uri("https://qa-slow-host.example/qa-inbox");
        var gate = new PushGate();
        var fastPushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(
            async (request, ct) =>
            {
                if (request.Endpoint == slow)
                    await gate.PassAsync(ct);
                else
                    fastPushed.TrySetResult();
                return new TransportResult(true, 202);
            },
            directory: peer => peer == Peer ? slow : Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.EnrolAsync(OtherPeer, OtherRecipient);

        for (var i = 0; i < 4; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(OtherRecipient)).GrantPeerDid).Should().BeTrue();

        await fastPushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Arrived.Should().Be(1, "the slow grant has one push in flight; its other three wait their turn");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(5);
    }

    [Fact]
    public async Task Grants_are_served_in_turn_and_each_grant_in_arrival_order()
    {
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var gate = new PushGate();
        var first = 0;
        await using var fixture = PushFixture.Create(
            async (request, ct) =>
            {
                if (Interlocked.Increment(ref first) == 1)
                    await gate.PassAsync(ct); // parks the only worker while the backlog fills
                order.Enqueue(request.Endpoint.AbsolutePath);
                return new TransportResult(true, 202);
            },
            directory: peer => new Uri(peer == Peer ? "https://qa-recipient-host.example/a" : "https://qa-recipient-host.example/b"),
            configure: o =>
            {
                o.PushConcurrency = 1;
                // L-020 (PR #109 review round 2, D-4): grants now take turns per operation, of at
                // most MaxDeliveryBatch pushes; with one push per operation the turns are per push.
                o.Pickup.MaxDeliveryBatch = 1;
            });
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.EnrolAsync(OtherPeer, OtherRecipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        for (var i = 1; i <= 3; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        for (var i = 0; i < 3; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync(OtherRecipient)).GrantPeerDid).Should().BeTrue();

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        order.Should().Equal("/a", "/b", "/a", "/b", "/a", "/b", "/a");
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(7);
    }

    // ── expiry and the age limit while a job waits (FR-ROUTE-09, FR-PICK-10) ──────────────

    // QA-1 DEFECT 1 (P2), fixed by Q-1: the push takes the record it sends by id
    // (now through IMessageStore.LeaseAsync at the moment of the push), so a message whose forward expires_time — or whose mailbox
    // age limit — passed while its job waited is never pushed (FR-ROUTE-09, FR-PICK-10). The
    // "today" pins that asserted the push are removed with the fix (L-020: they encoded the defect).
    [Fact]
    public async Task A_message_that_expires_while_its_job_waits_is_never_pushed()
    {
        var (fixture, gate) = await BlockedSingleWorkerAsync();
        await using var _ = fixture;
        var now = fixture.Time.GetUtcNow();
        var expiring = await QueueAsync(fixture, Recipient, new PickupDeliveryOptions(ExpiresTime: now.ToUnixTimeSeconds() + 10), "expiring");
        fixture.Dispatcher.Wake(expiring.Job.GrantPeerDid).Should().BeTrue();

        fixture.Time.Advance(TimeSpan.FromSeconds(60)); // the forward has expired; no push deadline (5 min) has passed
        // L-020: the QA draft expected 0 here, but the blocker (no expiry, being pushed) is still held
        // and counted; what matters is that the expired message no longer is.
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1, "only the blocker is still reported");
        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Select(r => r.Payload.ToArray()).Should().NotContain(p => p.SequenceEqual(expiring.Payload), "an expired message is never pushed");
        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
    }

    [Fact]
    public async Task A_message_that_ages_out_while_its_job_waits_is_never_pushed()
    {
        var (fixture, gate) = await BlockedSingleWorkerAsync(o =>
        {
            o.MaxForwardDelay = TimeSpan.FromSeconds(10);
            o.Pickup.MaxMessageAge = TimeSpan.FromSeconds(30);
        });
        await using var _ = fixture;
        var aging = await QueueAsync(fixture, Recipient, null, "aging");
        fixture.Dispatcher.Wake(aging.Job.GrantPeerDid).Should().BeTrue();

        fixture.Time.Advance(TimeSpan.FromSeconds(31)); // aged out; the blocker's 5-minute deadline has not passed
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(0, "the queue no longer reports it");
        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Select(r => r.Payload.ToArray()).Should().NotContain(p => p.SequenceEqual(aging.Payload), "an aged-out message is never pushed");
        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
    }

    [Fact]
    public async Task A_job_whose_message_was_acknowledged_meanwhile_sends_nothing()
    {
        // L-020: this was pinned as an observation ("still pushes a second copy", at-least-once). The
        // push now takes the record it sends (Q-1, L-057), so an acknowledged message is not sent again.
        var (fixture, gate) = await BlockedSingleWorkerAsync();
        await using var _ = fixture;
        var acked = await QueueAsync(fixture, Recipient, null, "acked");
        fixture.Dispatcher.Wake(acked.Job.GrantPeerDid).Should().BeTrue();
        (await fixture.Store.AcknowledgeAsync(acked.Job.GrantPeerDid, new[] { acked.Job.MessageId }, CancellationToken.None)).Should().Be(1);

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().ContainSingle("only the blocker is sent");
        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>One worker, a 5-minute deadline, the first push parked on a gate; expiry judged without skew.</summary>
    private static async Task<(PushFixture Fixture, PushGate Gate)> BlockedSingleWorkerAsync(Action<MediatorOptions>? configure = null)
    {
        var gate = new PushGate();
        var first = 0;
        var fixture = PushFixture.Create(
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref first) == 1)
                    await gate.PassAsync(ct);
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox,
            configure: o =>
            {
                o.PushConcurrency = 1;
                o.PushTimeout = TimeSpan.FromMinutes(5);
                configure?.Invoke(o);
            },
            didComm: o => o.ExpiresClockSkew = TimeSpan.Zero);
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        return (fixture, gate);
    }

    private static async Task<(StoredMessage Job, byte[] Payload)> QueueAsync(PushFixture fixture, string recipientDid, PickupDeliveryOptions? options, string forwardId)
    {
        var payload = PushFixture.Payload(70, seed: (byte)forwardId.Length);
        var result = await fixture.Queue.DeliverAsync(recipientDid, payload, options);
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        return (new StoredMessage(result.GrantPeerDid!, recipientDid, result.MessageId!), payload);
    }
}
