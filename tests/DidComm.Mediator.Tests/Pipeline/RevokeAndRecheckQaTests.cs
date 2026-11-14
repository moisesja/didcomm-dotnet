using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA-1 (#95, L-057): <see cref="MessagePickupQueue.DeliverAsync"/>'s post-enqueue re-check and
/// <see cref="MessagePickupQueue.RevokeAndPurgeAsync"/>, with the delivery parked (L-044) between
/// each pair of its calls — enqueue | re-check, re-check | live push — and the host's work run in
/// that gap; a recipient removed instead of a grant revoked; cancellation between revoke and purge;
/// two concurrent revokes.
/// </summary>
public sealed class RevokeAndRecheckQaTests
{
    private const string Peer = "did:example:qa-rr-peer";
    private const string Recipient = "did:example:qa-rr-recipient";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // ── parked between the enqueue and the re-check ───────────────────────────────────────

    [Fact]
    public async Task Revoke_and_purge_between_the_enqueue_and_the_recheck_leave_nothing_and_report_NotRegistered()
    {
        var (fixture, mediation, gate) = await ParkedOnRecheckAsync();
        await using var _ = fixture;

        var delivery = fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30)).AsTask();
        await gate.Arrived.WaitAsync(Wait);
        fixture.Store.MessageCount.Should().Be(1, "stored before the re-check");
        (await fixture.Queue.RevokeAndPurgeAsync(Peer)).Should().Be(new GrantRevocation(true, 1), "the purge finds the stored record");
        gate.Release();
        var result = await delivery.WaitAsync(Wait);

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_revoke_alone_between_the_enqueue_and_the_recheck_removes_the_record_before_any_purge()
    {
        var (fixture, mediation, gate) = await ParkedOnRecheckAsync();
        await using var _ = fixture;

        var delivery = fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30)).AsTask();
        await gate.Arrived.WaitAsync(Wait);
        (await mediation.RevokeAsync(Peer, CancellationToken.None)).Should().BeTrue();
        gate.Release();
        var result = await delivery.WaitAsync(Wait);

        result.Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        fixture.Store.MessageCount.Should().Be(0, "the re-check removed exactly its own record");
        (await fixture.Queue.RevokeAndPurgeAsync(Peer)).Should().Be(new GrantRevocation(false, 0));
    }

    [Fact]
    public async Task A_recipient_removed_between_the_enqueue_and_the_recheck_is_not_kept()
    {
        var (fixture, mediation, gate) = await ParkedOnRecheckAsync(extraRecipient: "did:example:qa-rr-stays");
        await using var _ = fixture;
        (await fixture.Queue.DeliverAsync("did:example:qa-rr-stays", PushFixture.Payload(30))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        gate.Reset();

        var delivery = fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(31)).AsTask();
        await gate.Arrived.WaitAsync(Wait);
        await mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Remove) }, CancellationToken.None);
        gate.Release();
        var result = await delivery.WaitAsync(Wait);

        result.Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        fixture.Store.MessageCount.Should().Be(1, "only the other DID's message stays");
        (await fixture.Store.GetStatusAsync(Peer, Recipient, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_recipient_moved_to_another_peer_and_back_between_the_enqueue_and_the_recheck_keeps_the_message()
    {
        var (fixture, mediation, gate) = await ParkedOnRecheckAsync();
        await using var _ = fixture;

        var delivery = fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30)).AsTask();
        await gate.Arrived.WaitAsync(Wait);
        await mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Remove) }, CancellationToken.None);
        await mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Recipient, RecipientUpdateAction.Add) }, CancellationToken.None);
        gate.Release();
        var result = await delivery.WaitAsync(Wait);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the re-check sees the DID registered to the same grant");
        fixture.Store.MessageCount.Should().Be(1);
    }

    // ── parked between the re-check and the live push ─────────────────────────────────────

    [Fact]
    public async Task Revoke_and_purge_while_the_live_push_is_being_sent_today_report_PushedLive_for_a_purged_record()
    {
        // The documented residual ("a live delivery already being sent completes"). Pinned because
        // DeliverAsync's own doc also says "the message is retained exactly when the outcome is
        // PushedLive or Queued": in this interleaving it is PushedLive and not retained.
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new ParkingLiveSession(peer.Did, mediator.Did);
        await TurnLiveOnAsync(fixture, peer.Did, mediator.Did, session);
        session.ParkNext();

        var delivery = fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30)).AsTask();
        await session.Parked.WaitAsync(Wait);
        (await fixture.Queue.RevokeAndPurgeAsync(peer.Did)).Should().Be(new GrantRevocation(true, 1));
        session.Release();
        var result = await delivery.WaitAsync(Wait);

        result.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        fixture.Store.MessageCount.Should().Be(0, "the purge removed the record the push was carrying");
    }

    // ── RevokeAndPurgeAsync itself ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelled_between_the_revoke_and_the_purge_it_revokes_and_a_retry_purges()
    {
        var mediation = new CancellingOnRevokeStore();
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMediationStore>(mediation));
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30));
        using var cts = new CancellationTokenSource();
        mediation.CancelAfterRevoke = cts;

        await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync(Peer, cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        (await mediation.GetGrantAsync(Peer, CancellationToken.None)).Should().BeNull("the revoke happened");
        fixture.Store.MessageCount.Should().Be(1, "the purge did not run");
        (await fixture.Store.GetStatusAsync(Peer, null, fixture.Time.GetUtcNow(), CancellationToken.None)).MessageCount.Should().Be(1, "the store reports it under the key; the queue never delivers it without a grant");
        mediation.CancelAfterRevoke = null;
        (await fixture.Queue.RevokeAndPurgeAsync(Peer)).Should().Be(new GrantRevocation(false, 1));
    }

    [Fact]
    public async Task Two_concurrent_revokes_revoke_once_and_purge_everything_once()
    {
        await using var fixture = PushFixture.Create();
        await fixture.EnrolAsync(Peer, Recipient);
        for (var i = 0; i < 5; i++)
            await fixture.Queue.DeliverAsync(Recipient, PushFixture.Payload(30, (byte)i));

        var results = await Task.WhenAll(
            Task.Run(async () => await fixture.Queue.RevokeAndPurgeAsync(Peer)),
            Task.Run(async () => await fixture.Queue.RevokeAndPurgeAsync(Peer)));

        results.Count(r => r.Revoked).Should().Be(1);
        results.Sum(r => r.Purged).Should().Be(5);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Theory]
    [InlineData("did:example:qa-secret ")]
    [InlineData(" did:example:qa-secret")]
    [InlineData("did:example:qa-secret?x=1")]
    [InlineData("DID:example:qa-secret")]
    public async Task A_peer_that_is_not_a_bare_DID_is_refused_without_quoting_it(string value)
    {
        await using var fixture = PushFixture.Create();

        var ex = (await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync(value)).Should().ThrowAsync<ArgumentException>()).Which;

        ex.ParamName.Should().Be("peerDid");
        ex.Message.Should().NotContain("qa-secret");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    /// <summary>A fixture whose mediation store parks the second recipient lookup of a delivery (its post-enqueue re-check).</summary>
    private static async Task<(PushFixture Fixture, HookedMediationStore Mediation, Gate Gate)> ParkedOnRecheckAsync(string? extraRecipient = null)
    {
        var mediation = new HookedMediationStore();
        var gate = new Gate();
        var fixture = PushFixture.Create(services: s => s.AddSingleton<IMediationStore>(mediation));
        if (extraRecipient is null)
            await fixture.EnrolAsync(Peer, Recipient);
        else
            await fixture.EnrolAsync(Peer, Recipient, extraRecipient);
        var baseline = mediation.FindCalls;
        mediation.OnFindAsync = async (call, did, ct) =>
        {
            // Calls come in pairs per delivery: the lookup (odd) and the re-check (even).
            if ((call - baseline) % 2 == 0 && did == Recipient)
                await gate.PassAsync(ct);
            return false;
        };
        return (fixture, mediation, gate);
    }

    private static async Task TurnLiveOnAsync(PushFixture fixture, string peerDid, string mediatorDid, IReturnRouteSession session)
    {
        fixture.Services.GetRequiredService<IReturnRouteSessionRegistry>().Register(session);
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(peerDid, mediatorDid, liveDelivery: true);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { mediatorDid }, From: peerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
    }

    /// <summary>One-shot rendezvous: the caller records its arrival and waits for <see cref="Release"/>.</summary>
    private sealed class Gate
    {
        private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public Task Arrived => _arrived.Task;

        public async Task PassAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
                return;
            _arrived.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        public void Release() => _release.TrySetResult();

        /// <summary>Arm again (for a later delivery); also releases anything still waiting.</summary>
        public void Reset()
        {
            _release.TrySetResult();
            _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _armed, 1);
        }
    }

    /// <summary>A live session that parks the next size-bounded send until released.</summary>
    private sealed class ParkingLiveSession(string peerDid, string localDid) : IReturnRouteSession
    {
        private readonly CancellationTokenSource _closed = new();
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _park;

        public Task Parked => _parked.Task;
        public string PeerDid { get; } = peerDid;
        public string LocalDid { get; } = localDid;
        public ReturnRouteMode Mode => ReturnRouteMode.All;
        public string? Thread => null;
        public ReturnRouteTransport Transport => ReturnRouteTransport.WebSocket;
        public bool IsLive => true;
        public CancellationToken Closed => _closed.Token;

        public void ParkNext() => Volatile.Write(ref _park, 1);
        public void Release() => _release.TrySetResult();

        public ValueTask<bool> TrySendAsync(Message message, CancellationToken ct) => ValueTask.FromResult(true);

        public async ValueTask<ReturnRouteSendResult> TrySendAsync(Message message, int maxPackedBytes, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _park, 0) == 1)
            {
                _parked.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return ReturnRouteSendResult.Sent;
        }
    }

    /// <summary>The in-memory mediation store; cancels a token right after a revoke succeeds.</summary>
    private sealed class CancellingOnRevokeStore : IMediationStore
    {
        private readonly InMemoryMediationStore _inner = new(new CoordinateMediationOptions());
        public CancellationTokenSource? CancelAfterRevoke { get; set; }
        public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct) => _inner.GetGrantAsync(peerDid, ct);
        public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => _inner.GrantAsync(grant, ct);
        public async ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct)
        {
            var revoked = await _inner.RevokeAsync(peerDid, ct);
            CancelAfterRevoke?.Cancel();
            return revoked;
        }
        public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct) => _inner.UpdateRecipientsAsync(peerDid, updates, ct);
        public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct) => _inner.ListRecipientsAsync(peerDid, offset, limit, ct);
        public ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct) => _inner.FindGrantByRecipientDidAsync(recipientDid, ct);
    }
}
