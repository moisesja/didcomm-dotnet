using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="DirectPushDispatcher"/> (FR-PICK-09, JC-4, JC-5): a 2xx removes, anything else keeps;
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
        var job = await fixture.QueuedJobAsync(Recipient, payload);
        fixture.Store.MessageCount.Should().Be(1);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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
        var store = new ScriptedMessageStore();
        var removing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnAcknowledge = async (_, _, ct) =>
        {
            removing.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            return 0;
        };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await removing.Task.WaitAsync(Wait);
        fixture.Dispatcher.Outstanding.Should().Be(1, "the removal holds the worker until the deadline");
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        store.Inner.MessageCount.Should().Be(1, "the delivered copy stays queued; delivery is at-least-once");
    }

    [Fact]
    public async Task A_job_whose_message_was_already_acknowledged_sends_nothing()
    {
        // L-020: this test pinned "a 2xx push whose copy was already acknowledged still counts as
        // pushed", i.e. the message went out a second time. The push now takes the record it sends
        // (Q-1), so nothing is sent for a record the queue no longer holds.
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        (await fixture.Queue.RemoveAsync(job.GrantPeerDid, job.MessageId)).Should().BeTrue();

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNotHeld);
        fixture.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_leased_to_a_pickup_is_not_pushed_and_stays_with_the_pickup()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        var now = fixture.Time.GetUtcNow();
        var pickup = new MessageLease("pickup-session", now + TimeSpan.FromMinutes(1));
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, pickup), CancellationToken.None)).Should().ContainSingle();

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNotHeld);
        fixture.Router.Requests.Should().BeEmpty("a recipient fetching the message does not get it twice");
        (await fixture.Store.ReleaseAsync(Peer, new[] { job.MessageId }, pickup.Id, CancellationToken.None)).Should().Be(1, "the pickup's lease is untouched");
    }

    [Fact]
    public async Task A_message_not_due_yet_is_not_pushed()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var result = await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(20), new PickupDeliveryOptions(NotBefore: fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(5)));

        fixture.Dispatcher.TryEnqueue(new PushJob(result.GrantPeerDid!, Recipient, result.MessageId!, "fwd-later")).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNotHeld);
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_push_gives_its_lease_back_at_once_so_a_pickup_gets_the_message()
    {
        await using var fixture = PushFixture.Create((r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
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
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        var start = fixture.Time.GetUtcNow();
        async Task<int> PickupSeesAsync(TimeSpan after)
        {
            var at = start + after;
            return (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, at, new MessageLease(null, at + TimeSpan.FromSeconds(1))), CancellationToken.None)).Count;
        }

        (await PickupSeesAsync(TimeSpan.FromSeconds(30) + DirectPushDispatcher.LeaseMargin - TimeSpan.FromTicks(1))).Should().Be(0, "the push holds the record under its lease");
        (await PickupSeesAsync(TimeSpan.FromSeconds(30) + DirectPushDispatcher.LeaseMargin)).Should().Be(1, "the lease ends DirectPushTimeout + LeaseMargin after the take");
        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }

    [Fact]
    public async Task A_store_that_fails_to_take_the_record_is_a_failure_and_nothing_is_sent()
    {
        var store = new ScriptedMessageStore { OnLeaseById = (_, _, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Router.Requests.Should().BeEmpty();
        store.Inner.MessageCount.Should().Be(1);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains(nameof(HandlerBoomException), StringComparison.Ordinal));
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_store_that_fails_to_give_the_lease_back_leaves_one_outcome_and_the_message_returns_when_the_lease_ends()
    {
        var store = new ScriptedMessageStore { OnRelease = (_, _, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker) };
        await using var fixture = PushFixture.Create((r, _) => Task.FromException<TransportResult>(PushFixture.TransportFailure(r, 500)), directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        store.Inner.MessageCount.Should().Be(1);
        var after = fixture.Time.GetUtcNow() + TimeSpan.FromSeconds(30) + DirectPushDispatcher.LeaseMargin;
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
        var job = await fixture.QueuedJobAsync(Recipient);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Store.MessageCount.Should().Be(1, because);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning);
        NoLeaks(fixture, job.MessageId);
    }

    [Fact]
    public async Task A_push_that_outlasts_its_timeout_is_abandoned_and_the_message_stays_queued()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox,
            configure: o => o.DirectPushTimeout = TimeSpan.FromSeconds(30));
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        fixture.Dispatcher.Outstanding.Should().Be(1, "the deadline has not passed yet");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains("Timeout", StringComparison.Ordinal));
    }

    // The deadline must end the push even when the callee ignores its token. A transport that only
    // returns once the far end gives up (the ASP.NET Core TestServer client does exactly that while
    // the server-side handler ignores RequestAborted) otherwise holds the worker, and the grant's
    // turn, until the far end returns: the push "outlasts" DirectPushTimeout by however long that is.
    [Fact]
    public async Task A_push_whose_transport_ignores_the_cancellation_still_ends_at_its_deadline_and_gives_the_message_back()
    {
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
            configure: o => o.DirectPushTimeout = TimeSpan.FromSeconds(7));
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        fixture.Dispatcher.Outstanding.Should().Be(1, "one second before the deadline the push still runs");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.WaitForIdleAsync(Wait); // without opening the gate

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().Contain(e => e.Everything.Contains("Timeout", StringComparison.Ordinal));
        var now = fixture.Time.GetUtcNow();
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, new MessageLease("pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().ContainSingle("the push gave its lease back at the deadline, so a pickup gets the message now");

        gate.Open();
        await lateSendReturned.Task.WaitAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.Failed }, "the abandoned send's late answer changes nothing");
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_store_take_that_ignores_the_cancellation_ends_at_the_deadline_and_nothing_is_sent()
    {
        var parked = new TaskCompletionSource<QueuedMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedMessageStore
        {
            OnLeaseById = (_, _, _, _, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<QueuedMessage?>(parked.Task); // ignores the token
            },
        };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        fixture.Router.Requests.Should().BeEmpty();
        parked.SetException(new HandlerBoomException(HandlerBoomException.Marker)); // observed, never unobserved
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
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);

        fixture.Dispatcher.Outstanding.Should().Be(1, "the give-back holds the worker");
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        parked.SetResult(1);
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
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
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

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedScheme);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task No_endpoint_for_the_grant_skips_the_push()
    {
        await using var fixture = PushFixture.Create(directory: _ => null);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
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

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint, PushOutcomes.SkippedNoEndpoint);
    }

    [Fact]
    public async Task A_throwing_directory_skips_the_push_and_is_logged_by_type_only()
    {
        await using var fixture = PushFixture.Create(directory: _ => throw new HandlerBoomException(HandlerBoomException.Marker));
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint);
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Everything.Should().Contain(nameof(HandlerBoomException));
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_grant_revoked_between_queueing_and_the_push_is_not_pushed_to()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        (await fixture.Mediation.RevokeAsync(Peer, CancellationToken.None)).Should().BeTrue();

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    [Fact]
    public async Task A_recipient_now_registered_under_another_grant_is_not_pushed_to()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        await fixture.Mediation.UpdateRecipientsAsync(Peer, new[] { new DidComm.Mediator.Coordination.RecipientUpdate(Recipient, DidComm.Mediator.Coordination.RecipientUpdateAction.Remove) }, CancellationToken.None);
        await fixture.EnrolAsync("did:example:other-peer", Recipient);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered);
    }

    [Fact]
    public async Task The_outbound_endpoint_guard_applies_to_the_directory_endpoint()
    {
        await using var fixture = PushFixture.Create(directory: _ => new Uri("http://127.0.0.1:9/inbox-path"), blockPrivateNetworks: true);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty("SendPackedAsync refuses a private address before any transport runs");
        fixture.Store.MessageCount.Should().Be(1);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        NoLeaks(fixture, "127.0.0.1");
    }

    // ---- bounds and saturation -------------------------------------------------------------------

    [Fact]
    public async Task One_grant_holds_at_most_16_jobs_and_a_refused_job_keeps_its_message_queued()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var jobs = new List<PushJob>();
        for (var i = 0; i < 20; i++)
            jobs.Add(await fixture.QueuedJobAsync(Recipient, forwardId: $"fwd-{i}"));

        var accepted = jobs.Select(j => fixture.Dispatcher.TryEnqueue(j)).ToArray();

        accepted.Count(a => a).Should().Be(DirectPushDispatcher.MaxPerGrant);
        accepted.Skip(DirectPushDispatcher.MaxPerGrant).Should().OnlyContain(a => !a);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(4);

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Pushes(PushOutcomes.Pushed).Should().Be(DirectPushDispatcher.MaxPerGrant);
        fixture.Store.MessageCount.Should().Be(4, "the refused jobs' messages were never pushed and stay queued");
        PipelineLogs.Of<DirectPushDispatcher>(fixture.Logs).Count(e => e.Level == LogLevel.Warning).Should().Be(1, "saturation is logged at Warning once, then at Debug");
    }

    [Fact]
    public async Task The_backlog_holds_at_most_1024_jobs_across_grants()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        const int grants = DirectPushDispatcher.MaxBacklog / DirectPushDispatcher.MaxPerGrant + 1; // 65
        var jobs = new List<PushJob>();
        for (var g = 0; g < grants; g++)
        {
            await fixture.EnrolAsync($"did:example:grant-{g}", $"did:example:recipient-{g}");
            for (var j = 0; j < DirectPushDispatcher.MaxPerGrant; j++)
                jobs.Add(await fixture.QueuedJobAsync($"did:example:recipient-{g}", PushFixture.Payload(16), $"fwd-{g}-{j}"));
        }

        var accepted = jobs.Count(job => fixture.Dispatcher.TryEnqueue(job));

        accepted.Should().Be(DirectPushDispatcher.MaxBacklog);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(DirectPushDispatcher.MaxPerGrant);
        await fixture.EnrolAsync("did:example:grant-new", "did:example:recipient-new");
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync("did:example:recipient-new", forwardId: "fwd-new"))
            .Should().BeFalse("the backlog is full whatever the grant");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(DirectPushDispatcher.MaxBacklog);
        fixture.Store.MessageCount.Should().Be(DirectPushDispatcher.MaxPerGrant + 1, "the refused jobs' messages were never pushed and stay queued");
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync("did:example:recipient-0", forwardId: "fwd-again"))
            .Should().BeTrue("released slots are reusable");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }

    [Fact]
    public async Task A_job_pins_no_payload_so_only_the_count_bounds_apply()
    {
        // L-020: the 64 MiB pinned-bytes budget is gone with the payload in the job (Q-1): a job now
        // names a stored record, whose bytes the store already holds under its own quota.
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        await using var dispatcher = fixture.NewDispatcher(capacity: 2, perGrant: 16);
        var large = fixture.Queue.MaxAdmissiblePayloadBytes;

        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(large))).Should().BeTrue();
        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(large))).Should().BeTrue();
        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(1))).Should().BeFalse("the count bound, whatever the size");

        gate.Open();
        await dispatcher.WaitForIdleAsync(Wait);
        fixture.Store.MessageCount.Should().Be(1, "the refused message stays queued");
    }

    [Fact]
    public void A_job_keeps_the_forward_id_only_in_its_bounded_log_form_and_refuses_unbounded_values()
    {
        // PR #109 review R1-1: a job outlives the forward's byte reservation, so whatever it holds
        // must be bounded by construction, not by every caller remembering to shorten the wire id.
        var wireId = new string('w', 524_288);
        var job = new PushJob(Peer, Recipient, new string('a', 32), wireId);

        job.ForwardId.Should().Be("(id of 524288 characters)");
        job.RetainedCharacters.Should().Be(Peer.Length + Recipient.Length + 32 + job.ForwardId.Length);
        new PushJob(Peer, Recipient, "id", "fwd-short").ForwardId.Should().Be("fwd-short");
        new PushJob(Peer, Recipient, "id", null).ForwardId.Should().Be("(none)");

        var longDid = "did:example:" + new string('d', 2048);
        FluentActions.Invoking(() => new PushJob(longDid, Recipient, "id", "f")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PushJob(Peer, longDid, "id", "f")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new PushJob(Peer, Recipient, new string('a', PickupSizing.MaxMintedIdLength + 1), "f")).Should().Throw<ArgumentException>();
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
            configure: o => o.DirectPushConcurrency = 4);
        await fixture.EnrolAsync("did:example:slow-peer", "did:example:slow-recipient");
        await fixture.EnrolAsync(Peer, Recipient);

        for (var i = 0; i < 3; i++)
            fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync("did:example:slow-recipient", forwardId: $"slow-{i}")).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);
        for (var i = 0; i < 5; i++)
            fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, forwardId: $"fast-{i}")).Should().BeTrue();

        await allFast.Task.WaitAsync(Wait);
        gate.Arrived.Should().Be(1, "the slow grant has one push in flight; its other two wait their turn");
        fixture.Dispatcher.Outstanding.Should().BeGreaterThanOrEqualTo(3, "the slow grant's three jobs are still waiting or running");

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
                o.DirectPushConcurrency = 4;
                o.Pickup.MaxQueueMessages = 40;
            });
        var jobs = new List<PushJob>();
        for (var g = 0; g < 6; g++)
        {
            await fixture.EnrolAsync($"did:example:fair-peer-{g}", $"did:example:fair-recipient-{g}");
            for (var j = 0; j < 10; j++)
                jobs.Add(await fixture.QueuedJobAsync($"did:example:fair-recipient-{g}", forwardId: $"f{g}-{j}"));
        }

        jobs.Should().OnlyContain(job => fixture.Dispatcher.TryEnqueue(job));
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
            configure: o => o.DirectPushConcurrency = 1);
        await fixture.EnrolAsync(Peer, Recipient);
        var dispatcher = fixture.Dispatcher;
        for (var i = 0; i < 3; i++)
            dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, forwardId: $"fwd-{i}")).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait);

        await dispatcher.DisposeAsync().AsTask().WaitAsync(Wait);

        fixture.Store.MessageCount.Should().Be(3, "the push in flight was cancelled and the waiting ones discarded; all stay queued");
        fixture.Pushes(PushOutcomes.SkippedShutdown).Should().Be(3);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(0);
        dispatcher.Outstanding.Should().Be(0);
        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, forwardId: "late")).Should().BeFalse();
        dispatcher.Invoking(d => d.Dispose()).Should().NotThrow("disposing twice is harmless");
    }

    // ---- off, lazy, context ------------------------------------------------------------------

    [Fact]
    public async Task With_direct_push_off_nothing_is_taken_or_counted()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        fixture.Dispatcher.Enabled.Should().BeFalse();
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeFalse();

        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Directory!.Calls.Should().Be(0);
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_directory_that_takes_the_protocol_dispatcher_in_its_constructor_is_resolved_on_the_first_push()
    {
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IRecipientEndpointDirectory, DispatcherHungryDirectory>());
        await fixture.EnrolAsync(Peer, Recipient);

        await Task.Run(() => fixture.Services.GetRequiredService<ProtocolDispatcher>()).WaitAsync(Wait);
        DispatcherHungryDirectory.Built.Should().BeFalse("nothing resolves the directory before a push");
        fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
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
        var job = await fixture.QueuedJobAsync(Recipient);

        marker.Value = "request-scoped";
        var dispatcher = fixture.Dispatcher;
        dispatcher.TryEnqueue(job).Should().BeTrue();
        await dispatcher.WaitForIdleAsync(Wait);

        seen.Should().BeNull();
    }

    [Fact]
    public async Task Building_the_dispatcher_inside_a_suppressed_flow_still_starts_its_workers()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        DirectPushDispatcher dispatcher;
        using (ExecutionContext.SuppressFlow())
            dispatcher = fixture.Dispatcher;

        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient)).Should().BeTrue();
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
