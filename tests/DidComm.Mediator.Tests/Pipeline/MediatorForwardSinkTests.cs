using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Threading;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="MediatorForwardSink"/> (design §3): each queue outcome maps to exactly one forward
/// outcome and one log level; a DID URL <c>next</c> reaches its DID; direct push gets only queued,
/// due messages; faults and cancellation; no DID or queue id in any line.
/// </summary>
public sealed class MediatorForwardSinkTests
{
    private const string Peer = "did:example:sink-peer";
    private const string Recipient = "did:example:sink-recipient";
    private static readonly Uri Inbox = new("https://sink-recipient-host.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ProtocolContext Context(PushFixture fixture, string forwardId = "fwd-sink-1")
        => new(
            new UnpackResult(
                new Message { Id = forwardId, Type = ForwardConstants.ForwardTypeUri },
                new[] { EnvelopeKind.Encrypted, EnvelopeKind.Plaintext },
                Encrypted: true,
                Authenticated: false,
                NonRepudiation: false,
                AnonymousSender: true,
                ContentEncryption: null,
                KeyWrap: null,
                SignatureAlgorithm: null,
                SignerKid: null,
                SenderKid: null,
                RecipientKid: "did:example:push-mediator#key-1",
                AllRecipientKids: new[] { "did:example:push-mediator#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forwardId),
            Client: null,
            fixture.Options,
            new InMemoryThreadStateStore());

    private static ForwardProcessingResult Forward(string next, byte[]? payload = null, long? expiresTime = null, DateTimeOffset? notBefore = null)
        => new(next, payload ?? PushFixture.Payload(80), expiresTime, Delay: null) { NotBefore = notBefore };

    private static IReadOnlyList<HandlerLogEntry> SinkLines(PushFixture fixture) => PipelineLogs.Of<MediatorForwardSink>(fixture.Logs);

    private static void NoLeaks(PushFixture fixture)
        => PipelineLogs.Leaking(fixture.Logs, Peer, Recipient, "sink-recipient-host")
            .Should().BeEmpty("no line may carry a DID or an endpoint");

    // ---- the outcome table -----------------------------------------------------------------------

    [Fact]
    public async Task A_registered_recipient_without_a_live_connection_is_queued_and_counted_once()
    {
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(1);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        SinkLines(fixture).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        fixture.PushOutcomeLog.Should().BeEmpty("direct push is off");
        NoLeaks(fixture);

        var now = fixture.Time.GetUtcNow();
        var record = (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 10, int.MaxValue, now, new MessageLease("test-owner", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().ContainSingle().Which;
        PipelineLogs.Leaking(fixture.Logs, record.Id).Should().BeEmpty("the queue's record id is never logged");
    }

    [Fact]
    public async Task A_next_that_names_a_key_of_the_recipient_reaches_the_recipient_did()
    {
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient + "#key-x25519-1"), Context(fixture), CancellationToken.None);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        (await fixture.Store.GetStatusAsync(Peer, Recipient, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task The_payload_and_the_forward_schedule_are_stored_as_given()
    {
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);
        var payload = PushFixture.Payload(123, seed: 3);
        var now = fixture.Time.GetUtcNow();
        var notBefore = now + TimeSpan.FromSeconds(5);

        await fixture.Sink.AcceptAsync(Forward(Recipient, payload, expiresTime: now.ToUnixTimeSeconds() + 60, notBefore: notBefore), Context(fixture), CancellationToken.None);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.Store.TotalBytes.Should().Be(payload.Length);
        (await fixture.Store.GetStatusAsync(Peer, null, now, CancellationToken.None)).MessageCount.Should().Be(0, "not due yet");
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1, "due at its not-before");
        fixture.Time.Advance(TimeSpan.FromSeconds(56));
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(0, "expired by its expires_time");
    }

    [Fact]
    public async Task An_unregistered_recipient_is_dropped_as_unknown_with_a_rate_limited_warning()
    {
        await using var fixture = PushFixture.Create();

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-a"), CancellationToken.None);
        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-b"), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(0);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedUnknown, ForwardOutcomes.DroppedUnknown);
        SinkLines(fixture).Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
        SinkLines(fixture)[0].Everything.Should().Contain("fwd-a");
        fixture.PushOutcomeLog.Should().BeEmpty();
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_next_that_is_not_a_did_is_dropped_as_unknown()
    {
        await using var fixture = PushFixture.Create();

        await fixture.Sink.AcceptAsync(Forward("https://sink-recipient-host.example/x"), Context(fixture), CancellationToken.None);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedUnknown);
        NoLeaks(fixture);
    }

    [Fact]
    public async Task An_expired_forward_is_dropped_as_expired_and_not_stored()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient, expiresTime: fixture.Time.GetUtcNow().ToUnixTimeSeconds() - 1), Context(fixture), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(0);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedExpired);
        SinkLines(fixture).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        fixture.PushOutcomeLog.Should().BeEmpty();
    }

    [Fact]
    public async Task A_forward_larger_than_the_queue_accepts_is_dropped_as_too_large()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);
        var tooLarge = PushFixture.Payload(fixture.Queue.MaxAdmissiblePayloadBytes + 1);

        await fixture.Sink.AcceptAsync(Forward(Recipient, tooLarge), Context(fixture), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(0);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.DroppedTooLarge);
        SinkLines(fixture).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task A_forward_beyond_the_recipient_quota_is_dropped_as_quota()
    {
        await using var fixture = PushFixture.Create(configure: o =>
        {
            o.DirectPush = false;
            o.Pickup.MaxQueueMessages = 1;
        });
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-1"), CancellationToken.None);
        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-2"), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(1);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.DroppedQuota);
        SinkLines(fixture).Select(e => e.Level).Should().Equal(LogLevel.Debug, LogLevel.Warning);
    }

    [Fact]
    public async Task A_queue_fault_is_counted_as_faulted_and_logged_at_error_by_type_only()
    {
        var mediation = new ScriptedMediationStore();
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        mediation.OnFind = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker + " " + Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-1"), CancellationToken.None);
        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-2"), CancellationToken.None);

        fixture.Store.MessageCount.Should().Be(0);
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Faulted, ForwardOutcomes.Faulted);
        SinkLines(fixture).Select(e => e.Level).Should().Equal(LogLevel.Error, LogLevel.Debug);
        PipelineLogs.Leaking(fixture.Logs, HandlerBoomException.Marker, Recipient, Peer).Should().BeEmpty();
    }

    [Fact]
    public async Task An_exception_from_the_queue_is_counted_as_faulted_and_never_escapes()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);

        var act = () => fixture.Sink.AcceptAsync(Forward(Recipient, Array.Empty<byte>()), Context(fixture), CancellationToken.None);

        await act.Should().NotThrowAsync();
        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Faulted);
        SinkLines(fixture).Should().ContainSingle().Which.Everything.Should().Contain(nameof(ArgumentException));
    }

    [Fact]
    public async Task A_cancelled_token_leaves_as_cancellation_and_counts_nothing()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.ForwardOutcomeLog.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_handlers_token_is_a_fault()
    {
        var mediation = new ScriptedMediationStore();
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        mediation.OnFind = (_, _) => throw new OperationCanceledException("a driver's own timeout");

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture), CancellationToken.None);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Faulted);
    }

    [Fact]
    public async Task A_long_forward_id_is_shortened_in_the_log()
    {
        await using var fixture = PushFixture.Create();
        var longId = new string('i', 300);

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, longId), CancellationToken.None);

        SinkLines(fixture).Should().ContainSingle().Which.Everything.Should().NotContain(longId).And.Contain("(id of 300 characters)");
    }

    [Fact]
    public async Task A_recipient_with_live_delivery_on_gets_the_message_stored_then_pushed_live_by_the_push_workers()
    {
        // L-020 (PR #109 review R1-2): the sink used to push live itself, inside DeliverAsync on the
        // forward pump, and count the forward Delivered-Live. It now only stores (Queued) and hands
        // the message to the push workers, which push it live (Pushed-Live on the push counter).
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        var before = session.Sent.Count;

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture), CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        session.Sent.Count.Should().Be(before + 1);
        MessagePickup.TryParseDelivery(session.Sent.Last(), out var delivered, out _).Should().BeTrue();
        delivered.Should().ContainSingle();
        fixture.Store.MessageCount.Should().Be(1, "the pushed copy is held, leased to the session, until it is acknowledged");
        SinkLines(fixture).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        PipelineLogs.Leaking(fixture.Logs, peer.Did, mediator.Did, Recipient).Should().BeEmpty();
    }

    [Fact]
    public async Task The_sink_stores_and_returns_while_a_live_push_is_stuck_and_other_grants_go_on()
    {
        // PR #109 review R1-2 at the unit level: the forward pump never awaits a live session.
        // Grant A's session never finishes taking its push; the sink still returns at once for A,
        // stores B's message, and B's own live session gets its push from another worker.
        const string RecipientB = "did:example:sink-recipient-b";
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peerA = await peers.NewAsync();
        var peerB = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(peerA.Did, Recipient);
        await fixture.EnrolAsync(peerB.Did, RecipientB);
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionA = new LiveTestSession(peerA.Did, mediator.Did);
        var sessionB = new LiveTestSession(peerB.Did, mediator.Did);
        await fixture.GoLiveAsync(sessionA);
        await fixture.GoLiveAsync(sessionB);
        sessionA.OnPush = async (_, ct) =>
        {
            entered.TrySetResult();
            await stuck.Task.WaitAsync(ct);
            return ReturnRouteSendResult.Sent;
        };
        var pushedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionB.OnPush = (_, _) =>
        {
            pushedB.TrySetResult();
            return Task.FromResult(ReturnRouteSendResult.Sent);
        };

        await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, "fwd-a"), CancellationToken.None).WaitAsync(Wait);
        await entered.Task.WaitAsync(Wait);
        await fixture.Sink.AcceptAsync(Forward(RecipientB), Context(fixture, "fwd-b"), CancellationToken.None).WaitAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        fixture.Store.MessageCount.Should().Be(2, "both messages are stored while A's push is stuck");
        await pushedB.Task.WaitAsync(Wait);
        stuck.Task.IsCompleted.Should().BeFalse("B's live push went out on another worker while A's was still stuck");

        stuck.TrySetResult();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive, PushOutcomes.PushedLive);
    }

    [Fact]
    public async Task A_message_that_is_not_due_yet_is_never_pushed_live()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        var before = session.Sent.Count;

        await fixture.Sink.AcceptAsync(Forward(Recipient, notBefore: fixture.Time.GetUtcNow() + TimeSpan.FromTicks(1)), Context(fixture), CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.PushOutcomeLog.Should().BeEmpty("a message that is not due gets no push job");
        session.Sent.Count.Should().Be(before);
    }

    // ---- the hand-over to direct push -----------------------------------------------------------

    [Fact]
    public async Task With_direct_push_on_a_queued_due_message_is_pushed_and_its_copy_removed()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var payload = PushFixture.Payload(90, seed: 9);

        await fixture.Sink.AcceptAsync(Forward(Recipient, payload, notBefore: fixture.Time.GetUtcNow()), Context(fixture, "fwd-push"), CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
        fixture.Router.Requests.Should().ContainSingle().Which.Payload.ToArray().Should().Equal(payload);
        fixture.Store.MessageCount.Should().Be(0);
        // L-020 (PR #109 review round 2, D-1): the push takes what the store holds and knows no forward;
        // its lines carry no forward id (the sink's line does).
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().NotContain(e => e.Everything.Contains("fwd-push", StringComparison.Ordinal));
        NoLeaks(fixture);
    }

    [Fact]
    public async Task A_message_that_is_not_due_yet_is_never_pushed_directly()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);

        await fixture.Sink.AcceptAsync(Forward(Recipient, notBefore: fixture.Time.GetUtcNow() + TimeSpan.FromTicks(1)), Context(fixture), CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.ForwardOutcomeLog.Should().Equal(ForwardOutcomes.Queued);
        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1, "it waits in the queue and is picked up once due");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("quota")]
    public async Task A_dropped_forward_is_never_handed_to_direct_push(string kind)
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox, configure: o => o.Pickup.MaxQueueMessages = 1);
        if (kind != "unknown")
            await fixture.EnrolAsync(Peer, Recipient);
        if (kind == "quota")
            await fixture.StoreAsync(Recipient);
        var expires = kind == "expired" ? fixture.Time.GetUtcNow().ToUnixTimeSeconds() - 1 : (long?)null;

        await fixture.Sink.AcceptAsync(Forward(Recipient, expiresTime: expires), Context(fixture), CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Behind_a_stuck_push_every_message_the_queue_stores_is_handed_over_and_the_queue_quota_is_the_only_limit()
    {
        // L-020 (ADV-4 D1): this test asserted that the 16-job per-grant share refused the 17th and
        // 18th messages (Push-Skipped-Saturated) while the first push hung. The share is now the
        // queue's own per-grant quota, so every stored message gets its push, and the message beyond
        // the quota is the queue's to refuse (Dropped-Quota), never the dispatcher's.
        const int Quota = 18;
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox,
            configure: o => o.Pickup.MaxQueueMessages = Quota);
        await fixture.EnrolAsync(Peer, Recipient);

        for (var i = 0; i < Quota + 1; i++)
            await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, $"fwd-{i}"), CancellationToken.None);

        fixture.ForwardOutcomeLog.Should().HaveCount(Quota + 1);
        fixture.ForwardOutcomeLog.Take(Quota).Should().OnlyContain(o => o == ForwardOutcomes.Queued);
        fixture.ForwardOutcomeLog.Last().Should().Be(ForwardOutcomes.DroppedQuota);
        fixture.Pushes(PushOutcomes.SkippedSaturated).Should().Be(0);
        await gate.WaitForArrivalsAsync(1, Wait);
        fixture.Dispatcher.Unsettled.Should().Be(1, "one operation for the grant, its first push stuck; the other wake-ups coalesced");
        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Pushed) == Quota, "every stored message is pushed");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Store.MessageCount.Should().Be(0, "every stored message was pushed and its copy removed");
    }

    [Fact]
    public async Task Push_jobs_waiting_behind_a_blocked_push_retain_only_bounded_values_whatever_the_forward_ids_length()
    {
        // PR #109 review R1-1: what the dispatcher keeps outlives the forward handler's byte
        // reservation, so it must not keep the forward's wire id alive (the reviewer's 16 ids of
        // 524 288 characters kept 15 MiB in 15 waiting jobs while the store counted 1 280 bytes).
        // L-020 (PR #109 review round 2, D-1): there are no jobs any more; per grant the dispatcher
        // keeps the grant's DID and nothing of any message.
        const int IdLength = 524_288;
        const int Forwards = 16;
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);

        for (var i = 0; i < Forwards; i++)
            await fixture.Sink.AcceptAsync(Forward(Recipient), Context(fixture, new string((char)('a' + i), IdLength)), CancellationToken.None);
        await gate.WaitForArrivalsAsync(1, Wait);

        // Every forward has returned (its reservation is given back); one push hangs, fifteen wait in the store.
        fixture.Dispatcher.Unsettled.Should().Be(1);
        fixture.Dispatcher.RetainedCharacters.Should().Be(Peer.Length, "one entry for the grant: its DID, never a wire id");

        gate.Open();
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Pushed) == Forwards, "every stored message is pushed");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the last operation settled");
        fixture.Dispatcher.RetainedCharacters.Should().Be(0);
    }

    [Fact]
    public async Task The_direct_push_setting_is_read_once_when_the_sink_is_built()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(Peer, Recipient);
        var sink = fixture.Sink;
        fixture.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediatorOptions>>().Value.DirectPush = true;

        await sink.AcceptAsync(Forward(Recipient), Context(fixture), CancellationToken.None);

        fixture.PushOutcomeLog.Should().BeEmpty();
        fixture.Store.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task The_registered_forward_sink_is_the_mediator_sink()
    {
        await using var fixture = PushFixture.Create();

        fixture.Services.GetRequiredService<IForwardSink>().Should().BeSameAs(fixture.Sink);
    }
}
