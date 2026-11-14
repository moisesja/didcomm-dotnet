using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="PushDispatcher"/> (FR-PICK-09, JC-4, JC-5): a 2xx removes, anything else keeps;
/// only http(s) from the host's directory; the registration is re-checked; bounded backlog, byte
/// budget and per-grant share; a slow endpoint holds only its workers; shutdown loses nothing; lazy
/// resolution; no execution-context flow; log lines name no DID, endpoint or exception text.
/// </summary>
public sealed class DirectPushDispatcherTests
{
    private const string Peer = "did:example:push-peer";
    private const string Recipient = "did:example:push-recipient";
    private static readonly Uri Inbox = new("https://recipient-host.example/inbox-path");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static void NoLeaks(PushFixture fixture, params string[] more)
        => PipelineLogs.Leaking(fixture.Logs, new[] { Peer, Recipient, "recipient-host", "inbox-path", HandlerBoomException.Marker }.Concat(more).ToArray())
            .Should().BeEmpty("no line may carry a DID, the endpoint or an exception message");

    [Fact]
    public async Task A_2xx_push_sends_the_stored_payload_to_the_directory_endpoint_and_removes_the_queued_copy()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var payload = PushFixture.Payload(64, seed: 11);
        var job = await fixture.StoreAsync(Recipient, payload);
        fixture.Store.MessageCount.Should().Be(1);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var request = fixture.Router.Requests.Should().ContainSingle().Which;
        request.Endpoint.Should().Be(Inbox);
        request.MediaType.Should().Be(MediaTypes.Encrypted);
        request.Payload.ToArray().Should().Equal(payload, "the payload sent is the one the store holds");
        request.SingleAttempt.Should().BeTrue("a push is one request to exactly the directory's endpoint: no retry, no redirect, no circuit breaker");
        fixture.Store.MessageCount.Should().Be(0, "a delivered push removes the queued copy");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        fixture.Directory!.Calls.Should().Be(1);
        NoLeaks(fixture, job.MessageId);
    }

    [Fact]
    public async Task A_removal_that_hangs_after_a_2xx_is_cut_by_the_deadline_and_the_copy_stays_queued()
    {
        // L-020 (PR #109 review round 2, D-3/D-5): the removal is part of the push now, so a removal
        // that hangs holds the push past its deadline: the push counts as failed (the worker cannot
        // tell it was delivered), and the grant stays busy until the removal ends.
        var store = new ScriptedMessageStore();
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnAcknowledge = async (_, _, _) =>
        {
            removing.TrySetResult();
            await hang.Task; // the removal is never cancelled: it ends a lease (D-5)
            return 0;
        };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await removing.Task.WaitAsync(Wait);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the removal holds the push until the deadline");
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        store.Inner.MessageCount.Should().Be(1, "the delivered copy stays queued; delivery is at-least-once");
        fixture.Dispatcher.Unsettled.Should().Be(1, "the grant waits for its removal");
        hang.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the removal ended and the push settled");
    }

    [Fact]
    public async Task A_job_whose_message_was_already_acknowledged_sends_nothing()
    {
        // L-020: this test pinned "a 2xx push whose copy was already acknowledged still counts as
        // pushed", i.e. the message went out a second time. The push now takes what the store holds
        // (D-1), so nothing is sent for a record the queue no longer holds: the wake-up finds nothing.
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        (await fixture.Queue.RemoveAsync(job.GrantPeerDid, job.MessageId)).Should().BeTrue();

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_leased_to_a_pickup_is_not_pushed_and_stays_with_the_pickup()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        var now = fixture.Time.GetUtcNow();
        var pickup = new MessageLease("pickup-session", now + TimeSpan.FromMinutes(1));
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, pickup), CancellationToken.None)).Should().ContainSingle();

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Router.Requests.Should().BeEmpty("a recipient fetching the message does not get it twice");
        (await fixture.Store.ReleaseAsync(Peer, new[] { job.MessageId }, pickup.Id, CancellationToken.None)).Should().Be(1, "the pickup's lease is untouched");
    }

    [Fact]
    public async Task A_message_not_due_yet_is_not_pushed()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var result = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20), new PickupDeliveryOptions(NotBefore: fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(5)));

        fixture.Dispatcher.Wake(result.GrantPeerDid!).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // L-020 (PR #109 review round 2): a wake-up that finds nothing to push is no outcome any more (Push-Skipped-NotHeld is gone).
        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_push_gives_its_lease_back_at_once_so_a_pickup_gets_the_message()
    {
        await using var fixture = PushFixture.Create((r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        var now = fixture.Time.GetUtcNow();
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, new MessageLease(null, now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().ContainSingle("the push's lease was given back, not left to run out");
    }

    [Fact]
    public async Task A_push_whose_worker_never_finishes_leaves_the_message_to_pickup_after_its_lease()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(CancellationToken.None); return new TransportResult(true, 202); }, // ignores the deadline
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        var start = fixture.Time.GetUtcNow();
        async Task<int> PickupSeesAsync(TimeSpan after)
        {
            var at = start + after;
            return (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, at, new MessageLease(null, at + TimeSpan.FromSeconds(1))), CancellationToken.None)).Count;
        }

        (await PickupSeesAsync(TimeSpan.FromSeconds(30) + PushDispatcher.LeaseMargin - TimeSpan.FromTicks(1))).Should().Be(0, "the push holds the record under its lease");
        (await PickupSeesAsync(TimeSpan.FromSeconds(30) + PushDispatcher.LeaseMargin)).Should().Be(1, "the lease ends PushTimeout + LeaseMargin after the take");
        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }

    [Fact]
    public async Task A_store_that_fails_to_take_the_record_is_a_failure_and_nothing_is_sent()
    {
        var store = new ScriptedMessageStore();
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var stored = await fixture.StoreAsync(Recipient);
        store.OnLease = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Router.Requests.Should().BeEmpty();
        store.Inner.MessageCount.Should().Be(1);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains(nameof(HandlerBoomException), StringComparison.Ordinal));
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_store_that_fails_to_give_the_lease_back_leaves_one_outcome_and_the_message_returns_when_the_lease_ends()
    {
        var store = new ScriptedMessageStore { OnRelease = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) };
        await using var fixture = PushFixture.Create((r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)), directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        store.Inner.MessageCount.Should().Be(1);
        var after = fixture.Time.GetUtcNow() + TimeSpan.FromSeconds(30) + PushDispatcher.LeaseMargin;
        (await store.Inner.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, after, new MessageLease(null, after + TimeSpan.FromSeconds(1))), CancellationToken.None))
            .Should().ContainSingle();
        NoLeaks(fixture);
    }

    public static TheoryData<string, Func<TransportRequest, CancellationToken, Task<TransportResult>>> Failures => new()
    {
        { "500 thrown by the transport", (r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)) },
        { "accepted without a status", (_, _) => Task.FromResult(new TransportResult(true, null)) },
        { "not accepted", (_, _) => Task.FromResult(new TransportResult(false, 202)) },
        { "a 3xx reported as accepted", (_, _) => Task.FromResult(new TransportResult(true, 302)) },
        { "an unexpected exception", (_, _) => Task.FromException<TransportResult>(new HandlerBoomException(HandlerBoomException.Marker)) },
        { "a timeout the transport reports itself", (_, _) => Task.FromException<TransportResult>(new TaskCanceledException()) },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Anything_but_an_accepted_2xx_leaves_the_message_queued(string because, Func<TransportRequest, CancellationToken, Task<TransportResult>> send)
    {
        await using var fixture = PushFixture.Create(send, directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Store.MessageCount.Should().Be(1, because);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning);
        NoLeaks(fixture, job.MessageId);
    }

    [Fact]
    public async Task A_push_that_outlasts_its_timeout_is_abandoned_and_the_message_stays_queued()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox,
            configure: o => o.PushTimeout = TimeSpan.FromSeconds(30));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        fixture.Dispatcher.Unsettled.Should().Be(1, "the deadline has not passed yet");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains("Timeout", StringComparison.Ordinal));
    }

    // The deadline must end the push even when the callee ignores its token. A transport that only
    // returns once the far end gives up (the ASP.NET Core TestServer client does exactly that while
    // the server-side handler ignores RequestAborted) otherwise holds the worker, and the grant's
    // turn, until the far end returns: the push "outlasts" PushTimeout by however long that is.
    [Fact]
    public async Task A_push_whose_transport_ignores_the_cancellation_frees_the_worker_at_its_deadline_and_settles_its_record_when_the_send_ends()
    {
        // L-020 (PR #109 review round 2, D-3/D-5): the lease used to be given back at the deadline
        // while the abandoned send ran on (so a late 2xx went unrecorded and the copy stayed). The
        // push now ends its lease exactly once, when the send ends: until then the record stays
        // under the push's lease and the grant is not served again; the late 2xx removes it.
        var gate = new PushGate();
        var lateSendReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(
            async (_, _) =>
            {
                await gate.PassAsync(CancellationToken.None); // ignores the push's token
                lateSendReturned.TrySetResult();
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox,
            configure: o => o.PushTimeout = TimeSpan.FromSeconds(7));
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        fixture.Dispatcher.Unsettled.Should().Be(1, "one second before the deadline the push still runs");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.WaitForIdleAsync(Wait); // without opening the gate

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains("Timeout", StringComparison.Ordinal));
        fixture.Dispatcher.Unsettled.Should().Be(1, "the abandoned send still runs, and its grant waits for it");
        var now = fixture.Time.GetUtcNow();
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, new MessageLease("pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().BeEmpty("the record stays under the push's lease until its send ends");

        gate.Open();
        await lateSendReturned.Task.WaitAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned push settled");
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "counted once, at the deadline");
        fixture.Store.MessageCount.Should().Be(0, "the late 2xx was a delivery: the copy is removed");
    }

    [Fact]
    public async Task A_store_take_that_ignores_the_cancellation_ends_at_the_deadline_and_nothing_is_sent()
    {
        var parked = new TaskCompletionSource<IReadOnlyList<QueuedMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedMessageStore();
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var stored = await fixture.StoreAsync(Recipient);
        store.OnLease = (_, _) =>
        {
            entered.TrySetResult();
            return new ValueTask<IReadOnlyList<QueuedMessage>>(parked.Task); // ignores the token
        };
        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Dispatcher.Unsettled.Should().Be(1, "the take still runs; its grant waits for it (D-3)");
        parked.SetException(new HandlerBoomException(HandlerBoomException.Marker)); // observed, never unobserved
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the take ended and the push settled");
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_give_back_that_ignores_the_cancellation_frees_the_worker_after_its_bound()
    {
        var parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedMessageStore
        {
            OnRelease = (_, _, _, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<int>(parked.Task); // ignores the token
            },
        };
        await using var fixture = PushFixture.Create(
            (r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)),
            directory: _ => Inbox,
            services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        fixture.Dispatcher.Unsettled.Should().Be(1, "the give-back holds the push");
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Dispatcher.Unsettled.Should().Be(1, "the give-back still runs: the grant is not served again before it ends");
        parked.SetResult(1);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the give-back ended and the push settled");
    }

    [Fact]
    public async Task A_cancellation_callback_that_throws_cannot_escape_the_push_deadline()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) =>
            {
                ct.Register(() => throw new HandlerBoomException(HandlerBoomException.Marker));
                await gate.PassAsync(ct);
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        fixture.Invoking(f => f.Time.Advance(TimeSpan.FromSeconds(30))).Should().NotThrow("the deadline's callback cancels inside a try");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Store.MessageCount.Should().Be(1);
    }

    public static TheoryData<string> NotHttp => new() { "ws://recipient-host.example/inbox-path", "wss://recipient-host.example/inbox-path", "ftp://recipient-host.example/inbox-path", "/inbox-path" };

    [Theory]
    [MemberData(nameof(NotHttp))]
    public async Task An_endpoint_that_is_not_absolute_http_or_https_is_never_pushed_to(string endpoint)
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri(endpoint, UriKind.RelativeOrAbsolute));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedScheme);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task No_endpoint_for_the_grant_skips_the_push()
    {
        await using var fixture = PushFixture.Create(directory: _ => null);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint);
    }

    [Fact]
    public async Task No_registered_directory_skips_the_push()
    {
        await using var fixture = PushFixture.Create(directory: null);
        await fixture.EnrolAsync(Peer, Recipient);

        // L-020 (PR #109 review round 2, D-2): two wake-ups in a row may coalesce, so each is served
        // and counted on its own here.
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint, PushOutcomes.SkippedNoEndpoint);
    }

    [Fact]
    public async Task A_throwing_directory_skips_the_push_and_is_logged_by_type_only()
    {
        await using var fixture = PushFixture.Create(directory: _ => throw new HandlerBoomException(HandlerBoomException.Marker));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint);
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Everything.Should().Contain(nameof(HandlerBoomException));
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_grant_revoked_between_queueing_and_the_push_is_not_pushed_to()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        (await fixture.Mediation.RevokeAsync(Peer, CancellationToken.None)).Should().BeTrue();

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    [Fact]
    public async Task A_recipient_now_registered_under_another_grant_is_not_pushed_to()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);
        await fixture.Mediation.UpdateRecipientsAsync(Peer, new[] { new DidComm.Mediator.Coordination.RecipientUpdate(Recipient, DidComm.Mediator.Coordination.RecipientUpdateAction.Remove) }, CancellationToken.None);
        await fixture.EnrolAsync("did:example:other-peer", Recipient);

        fixture.Dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    [Fact]
    public async Task The_outbound_endpoint_guard_applies_to_the_directory_endpoint()
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri("http://127.0.0.1:9/inbox-path"), blockPrivateNetworks: true);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty("SendPackedAsync refuses a private address before any transport runs");
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        NoLeaks(fixture, "127.0.0.1");
    }

    // ---- bounds and saturation -------------------------------------------------------------------

    [Fact]
    public async Task A_grant_with_its_whole_quota_waiting_behind_a_stuck_push_has_every_message_pushed_in_order()
    {
        // ADV-4 D1, kept under D-1: nothing caps what a grant may have waiting but the store, and a
        // burst behind a stuck first push is pushed in full once it moves, in the order it was stored.
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox,
            configure: o => o.Pickup.MaxQueueMessages = 40);
        await fixture.EnrolAsync(Peer, Recipient);

        var payloads = new List<byte[]>();
        for (var i = 0; i < 40; i++)
        {
            payloads.Add(PushFixture.Payload(16, seed: (byte)i));
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient, payloads[^1])).GrantPeerDid).Should().BeTrue();
        }

        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Pushed) == 40, "every stored message is pushed");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(0);
        fixture.Router.Requests.Select(r => r.Payload.ToArray()).Should().BeEquivalentTo(payloads, o => o.WithStrictOrdering());
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_grant_does_at_most_one_delivery_batch_of_pushes_per_turn_so_a_busy_grant_cannot_hold_a_worker()
    {
        // D-4: an operation pushes at most MaxDeliveryBatch messages and then yields its worker; the
        // grant takes its turn again behind the others.
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var gate = new PushGate();
        var first = 0;
        await using var fixture = PushFixture.Create(
            async (request, ct) =>
            {
                order.Enqueue(request.Endpoint.AbsolutePath);
                if (Interlocked.Exchange(ref first, 1) == 0)
                    await gate.PassAsync(ct); // the busy grant's first push waits until the other grant is woken
                return new TransportResult(true, 202);
            },
            directory: peer => new Uri($"https://recipient-host.example/{peer.Split(':').Last()}"),
            configure: o =>
            {
                o.PushConcurrency = 1;
                o.Pickup.MaxDeliveryBatch = 3;
            });
        await fixture.EnrolAsync("did:example:busy", "did:example:busy-r");
        await fixture.EnrolAsync("did:example:other", "did:example:other-r");
        fixture.Dispatcher.PerRun.Should().Be(3);
        for (var i = 0; i < 7; i++)
            await fixture.StoreAsync("did:example:busy-r");
        await fixture.StoreAsync("did:example:other-r");

        fixture.Dispatcher.Wake("did:example:busy").Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Dispatcher.Wake("did:example:other").Should().BeTrue();
        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Pushed) == 8, "everything is pushed");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        order.Should().Equal("/busy", "/busy", "/busy", "/other", "/busy", "/busy", "/busy", "/busy");
    }

    [Fact]
    public async Task Wake_takes_a_bounded_grant_did_and_the_dispatcher_keeps_nothing_else_of_a_message()
    {
        // R1-1 under D-1: the dispatcher holds no per-message state at all; per grant it keeps the
        // grant's DID, bounded by construction.
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        FluentActions.Invoking(() => fixture.Dispatcher.Wake("did:example:" + new string('d', 2048))).Should().Throw<ArgumentException>();
        fixture.Dispatcher.GrantEntries.Should().Be(0);
    }

    [Fact]
    public async Task A_slow_endpoint_holds_one_worker_and_its_grant_pushes_one_at_a_time()
    {
        // L-020: this test pinned "a slow grant holds as many workers as it has jobs" (3 here). Since
        // Q-4 a grant has at most one push in flight, so its endpoint holds a single worker.
        var slow = new Uri("https://slow-host.example/inbox-path");
        var gate = new PushGate();
        var fastDone = 0;
        var allFast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(
            async (request, ct) =>
            {
                if (request.Endpoint == slow)
                {
                    await gate.PassAsync(ct);
                }
                else if (Interlocked.Increment(ref fastDone) == 5)
                {
                    allFast.TrySetResult();
                }
                return new TransportResult(true, 202);
            },
            directory: peer => peer == "did:example:slow-peer" ? slow : Inbox,
            configure: o => o.PushConcurrency = 4);
        await fixture.EnrolAsync("did:example:slow-peer", "did:example:slow-recipient");
        await fixture.EnrolAsync(Peer, Recipient);

        for (var i = 0; i < 3; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync("did:example:slow-recipient")).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        for (var i = 0; i < 5; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();

        await allFast.Task.WaitAsync(Wait);
        gate.Arrived.Should().Be(1, "the slow grant has one push in flight; its other two wait their turn");
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 1, "only the slow grant's operation is left running");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(8);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task One_grant_never_has_two_pushes_in_flight_while_grants_together_use_every_worker()
    {
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var maxPerEndpoint = 0;
        var maxTotal = 0;
        var total = 0;
        await using var fixture = PushFixture.Create(
            async (request, ct) =>
            {
                var key = request.Endpoint.AbsolutePath;
                var mine = inFlight.AddOrUpdate(key, 1, (_, n) => n + 1);
                var all = Interlocked.Increment(ref total);
                InterlockedMax(ref maxPerEndpoint, mine);
                InterlockedMax(ref maxTotal, all);
                await Task.Yield();
                await Task.Delay(1, ct);
                inFlight.AddOrUpdate(key, 0, (_, n) => n - 1);
                Interlocked.Decrement(ref total);
                return new TransportResult(true, 202);
            },
            directory: peer => new Uri($"https://recipient-host.example/{peer.Split(':').Last()}"),
            configure: o =>
            {
                o.PushConcurrency = 4;
                o.Pickup.MaxQueueMessages = 40;
            });
        var jobs = new List<StoredMessage>();
        for (var g = 0; g < 6; g++)
        {
            await fixture.EnrolAsync($"did:example:fair-peer-{g}", $"did:example:fair-recipient-{g}");
            for (var j = 0; j < 10; j++)
                jobs.Add(await fixture.StoreAsync($"did:example:fair-recipient-{g}"));
        }

        jobs.Should().OnlyContain(job => fixture.Dispatcher.Wake(job.GrantPeerDid));
        await fixture.Dispatcher.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        maxPerEndpoint.Should().Be(1, "a grant's pushes go out one after another");
        maxTotal.Should().BeGreaterThan(1, "different grants are pushed in parallel");
        maxTotal.Should().BeLessThanOrEqualTo(4);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(60);
        fixture.Store.MessageCount.Should().Be(0);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    // ---- shutdown ------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_with_a_push_in_flight_and_jobs_waiting_loses_nothing()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox,
            configure: o => o.PushConcurrency = 1);
        await fixture.EnrolAsync(Peer, Recipient);
        var dispatcher = fixture.Dispatcher;
        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        for (var i = 0; i < 2; i++)
            dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();

        await dispatcher.DisposeAsync().AsTask().WaitAsync(Wait);
        await MessagePickupLive.UntilAsync(() => dispatcher.Unsettled == 0, "the cancelled push settled");

        // L-020 (PR #109 review round 2, D-2): the two wake-ups during the push coalesced into one held
        // wake-up; the push in flight and that wake-up each end once (Push-Skipped-Shutdown), not
        // one outcome per message; every message stays queued.
        fixture.Store.MessageCount.Should().Be(3, "the push in flight was cancelled; all stay queued");
        fixture.Pushes(PushOutcomes.SkippedShutdown).Should().Be(2);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(0);
        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeFalse();
        dispatcher.Invoking(d => d.Dispose()).Should().NotThrow("disposing twice is harmless");
    }

    // ---- off, lazy, context ------------------------------------------------------------------

    [Fact]
    public async Task With_direct_push_off_a_job_without_a_live_session_never_reaches_the_directory_and_its_message_stays_queued()
    {
        // L-020 (PR #109 review R1-2): the workers now run whenever AddMediator() is used, because
        // they also do the live pushes; with direct push off the dispatcher used to refuse every
        // job. A wake-up whose peer has no live session any more (the sink wakes only when it saw
        // one) now ends with its own outcome, and still never consults the directory.
        await using var fixture = PushFixture.Create(directory: _ => Inbox, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.DirectPushEnabled.Should().BeFalse();
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoLiveSession);
        fixture.Directory!.Calls.Should().Be(0);
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1, "nothing took a lease on it");
    }

    [Fact]
    public async Task A_directory_that_takes_the_protocol_dispatcher_in_its_constructor_is_resolved_on_the_first_push()
    {
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IRecipientEndpointDirectory, DispatcherHungryDirectory>());
        await fixture.EnrolAsync(Peer, Recipient);

        await Task.Run(() => fixture.Services.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Wait);
        DispatcherHungryDirectory.Built.Should().BeFalse("nothing resolves the directory before a push");
        fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        fixture.Router.Requests.Should().ContainSingle().Which.Endpoint.Should().Be(DispatcherHungryDirectory.Endpoint);
    }

    [Fact]
    public async Task Workers_do_not_inherit_the_execution_context_of_whoever_built_the_dispatcher_or_enqueued()
    {
        var marker = new AsyncLocal<string?>();
        string? seen = "not called";
        await using var fixture = PushFixture.Create(directory: _ =>
        {
            seen = marker.Value;
            return Inbox;
        });
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.StoreAsync(Recipient);

        marker.Value = "request-scoped";
        var dispatcher = fixture.Dispatcher;
        dispatcher.Wake(job.GrantPeerDid).Should().BeTrue();
        await dispatcher.WaitForIdleAsync(Wait);

        seen.Should().BeNull();
    }

    [Fact]
    public async Task Building_the_dispatcher_inside_a_suppressed_flow_still_starts_its_workers()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        PushDispatcher dispatcher;
        using (ExecutionContext.SuppressFlow())
            dispatcher = fixture.Dispatcher;

        dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        await dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
    }

    private sealed class DispatcherHungryDirectory : IRecipientEndpointDirectory
    {
        public static readonly Uri Endpoint = new("https://hungry-host.example/inbox");
        private static int s_built;

        public DispatcherHungryDirectory(ProtocolDispatcher dispatcher)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            Interlocked.Exchange(ref s_built, 1);
        }

        public static bool Built => Volatile.Read(ref s_built) == 1;

        public Uri? Find(string grantPeerDid) => Endpoint;
    }
}
