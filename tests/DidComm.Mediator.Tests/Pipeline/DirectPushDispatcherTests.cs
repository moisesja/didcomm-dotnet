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
        var job = await fixture.QueuedJobAsync(Recipient);
        fixture.Store.MessageCount.Should().Be(1);

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        var request = fixture.Router.Requests.Should().ContainSingle().Which;
        request.Endpoint.Should().Be(Inbox);
        request.MediaType.Should().Be(MediaTypes.Encrypted);
        request.Payload.ToArray().Should().Equal(job.Payload);
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
    public async Task A_2xx_push_whose_copy_was_already_acknowledged_still_counts_as_pushed()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var job = await fixture.QueuedJobAsync(Recipient);
        (await fixture.Queue.RemoveAsync(job.GrantPeerDid, job.MessageId)).Should().BeTrue();

        fixture.Dispatcher.TryEnqueue(job).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
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
        for (var g = 0; g < grants; g++)
            await fixture.EnrolAsync($"did:example:grant-{g}", $"did:example:recipient-{g}");

        var accepted = 0;
        for (var g = 0; g < grants; g++)
        {
            for (var j = 0; j < DirectPushDispatcher.MaxPerGrant; j++)
            {
                if (fixture.Dispatcher.TryEnqueue(new PushJob($"did:example:grant-{g}", $"did:example:recipient-{g}", $"id-{g}-{j}", PushFixture.Payload(16), $"fwd-{g}-{j}")))
                    accepted++;
            }
        }

        accepted.Should().Be(DirectPushDispatcher.MaxBacklog);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(DirectPushDispatcher.MaxPerGrant);
        fixture.Dispatcher.TryEnqueue(new PushJob("did:example:grant-new", "did:example:recipient-new", "id-new", PushFixture.Payload(16), "fwd-new"))
            .Should().BeFalse("the backlog is full whatever the grant");

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(DirectPushDispatcher.MaxBacklog);
        fixture.Dispatcher.TryEnqueue(new PushJob("did:example:grant-0", "did:example:recipient-0", "id-again", PushFixture.Payload(16), "fwd-again"))
            .Should().BeTrue("released slots are reusable");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }

    [Fact]
    public async Task The_byte_budget_refuses_a_job_that_would_exceed_it()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        await using var dispatcher = new DirectPushDispatcher(
            fixture.Services,
            fixture.Services.GetRequiredService<IOptions<MediatorOptions>>(),
            fixture.Queue,
            fixture.Mediation,
            fixture.Services.GetRequiredService<MediatorMetrics>(),
            logger: null,
            fixture.Time,
            capacity: 1024,
            byteBudget: 100,
            perGrant: 16);

        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(60))).Should().BeTrue();
        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(41))).Should().BeFalse();
        dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, PushFixture.Payload(40))).Should().BeTrue();

        gate.Open();
        await dispatcher.WaitForIdleAsync(Wait);
        fixture.Store.MessageCount.Should().Be(1, "the refused message stays queued");
    }

    [Fact]
    public async Task A_slow_endpoint_holds_only_the_workers_it_occupies()
    {
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
        await gate.WaitForArrivalsAsync(3, Wait);
        for (var i = 0; i < 5; i++)
            fixture.Dispatcher.TryEnqueue(await fixture.QueuedJobAsync(Recipient, forwardId: $"fast-{i}")).Should().BeTrue();

        await allFast.Task.WaitAsync(Wait);
        gate.Arrived.Should().Be(3, "the slow pushes are still held");
        fixture.Dispatcher.Outstanding.Should().BeGreaterThanOrEqualTo(3);

        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(8);
        fixture.Store.MessageCount.Should().Be(0);
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
