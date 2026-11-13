using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// #95 JC-6 (design §9): <see cref="MessagePickupQueue.RevokeAndPurgeAsync"/> revokes a peer's grant
/// and drops what is held under it in one host call, and <see cref="MessagePickupQueue.DeliverAsync"/>
/// checks the registration again after it stored a message, so a delivery that looked the recipient
/// up before the revoke cannot leave a record behind after the purge (L-057). The races are driven
/// with a rendezvous (L-044): the delivery is parked inside the store's enqueue — after its lookup,
/// before its record exists — while the revoke and purge run, then released.
/// </summary>
public sealed class PickupRevokeAndPurgeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    // ── The call ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_and_purge_removes_the_grant_and_everything_held_under_it()
    {
        await using var fixture = PickupFixture.Create();
        var mediator = (await fixture.NewPeerAsync()).Did;
        var peer = (await fixture.NewPeerAsync()).Did;
        await fixture.EnrolAsync(peer, mediator, peer, "did:example:second");
        await fixture.QueueAsync(peer, PickupFixture.Payload(20, 1));
        await fixture.QueueAsync(peer, PickupFixture.Payload(20, 2));
        await fixture.QueueAsync("did:example:second", PickupFixture.Payload(20, 3));

        var outcome = await fixture.Queue.RevokeAndPurgeAsync(peer);

        outcome.Should().Be(new GrantRevocation(Revoked: true, Purged: 3));
        fixture.Memory.MessageCount.Should().Be(0);
        (await fixture.Host.Store.GetGrantAsync(peer, CancellationToken.None)).Should().BeNull();
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator)).MessageCount.Should().Be(0);
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 4))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        (await fixture.Queue.DeliverAsync("did:example:second", PickupFixture.Payload(20, 5))).Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task It_is_idempotent_and_leaves_other_grants_alone()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, alice) = await fixture.EnrolledAsync();
        var bob = await fixture.NewPeerAsync();
        await fixture.EnrolAsync(bob.Did, mediator, bob.Did);
        await fixture.QueueAsync(alice, PickupFixture.Payload(20, 1));
        var bobs = await fixture.QueueAsync(bob.Did, PickupFixture.Payload(20, 2));

        (await fixture.Queue.RevokeAndPurgeAsync(alice)).Should().Be(new GrantRevocation(true, 1));
        (await fixture.Queue.RevokeAndPurgeAsync(alice)).Should().Be(new GrantRevocation(false, 0));

        (await MessagePickupLive.FetchNowAsync(fixture, bob.Did, mediator)).Should().ContainSingle().Which.Id.Should().Be(bobs);
    }

    [Fact]
    public async Task A_peer_with_no_grant_revokes_nothing_and_purges_what_was_left_under_its_key()
    {
        await using var fixture = PickupFixture.Create();
        var (_, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        // A host that revoked through the mediation store alone: the record stays until purged.
        (await fixture.Host.Store.RevokeAsync(peer, CancellationToken.None)).Should().BeTrue();
        fixture.Memory.MessageCount.Should().Be(1);

        (await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().Be(new GrantRevocation(false, 1));
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("secret-not-a-did")]
    [InlineData("did:example:secret#key-1")]
    [InlineData("did:example:secret/path")]
    public async Task A_peer_that_is_not_a_bare_DID_is_refused_before_any_store_is_called_and_not_quoted(string value)
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));

        var act = async () => await fixture.Queue.RevokeAndPurgeAsync(value);

        (await act.Should().ThrowAsync<ArgumentException>()).WithParameterName("peerDid").Which.Message.Should().NotContain("secret");
        mediation.Calls.Should().BeEmpty();
        fixture.FakeStore!.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_and_an_over_long_DID_are_refused()
    {
        await using var fixture = PickupFixture.Create();

        await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync("did:example:" + new string('a', 2048)))
            .Should().ThrowAsync<ArgumentException>().WithParameterName("peerDid");
    }

    [Fact]
    public async Task A_mediation_store_fault_propagates_and_nothing_is_purged()
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (_, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        mediation.OnRevoke = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);

        await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().ThrowAsync<HandlerBoomException>();

        fixture.FakeStore!.CountOf(nameof(IMessageStore.PurgeGrantAsync)).Should().Be(0, "revoke first: a purge with the grant still in force would be refilled");
        fixture.Memory.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task A_message_store_fault_propagates_and_a_retry_purges_what_is_left()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        fixture.FakeStore!.OnPurgeGrant = (_, _) => throw new HandlerBoomException(HandlerBoomException.Marker);

        await FluentActions.Awaiting(async () => await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().ThrowAsync<HandlerBoomException>();
        (await fixture.Host.Store.GetGrantAsync(peer, CancellationToken.None)).Should().BeNull("the revoke happened before the fault");

        fixture.FakeStore.OnPurgeGrant = null;
        (await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().Be(new GrantRevocation(false, 1), "safe to retry");
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task The_outcome_prints_no_DID_and_the_log_line_names_none()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();

        var outcome = await fixture.Queue.RevokeAndPurgeAsync(peer);

        outcome.ToString().Should().Be("GrantRevocation { Revoked = True, Purged = 0 }");
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator);
    }

    // ── The race with a delivery in flight (parked between its lookup and its enqueue) ──

    [Fact]
    public async Task A_delivery_parked_across_revoke_and_purge_removes_its_own_record_and_reports_NotRegistered()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (_, peer) = await fixture.EnrolledAsync();
        using var park = new Park(fixture.FakeStore!);

        var delivery = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20)).AsTask();
        await park.Arrived.WaitAsync(Deadline);
        (await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().Be(new GrantRevocation(true, 0), "the parked record does not exist yet");
        park.Release();
        var result = await delivery.WaitAsync(Deadline);

        park.Overlapped.Should().BeTrue("the revoke ran while the delivery was inside the store");
        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.FakeStore!.Acknowledged.Should().ContainSingle().Which.Ids.Should().Equal(new[] { park.StoredId }, "exactly the record this delivery stored is removed");
        fixture.Memory.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_parked_across_revoke_and_a_new_grant_for_the_same_peer_keeps_its_message()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        using var park = new Park(fixture.FakeStore!);

        var delivery = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20)).AsTask();
        await park.Arrived.WaitAsync(Deadline);
        await fixture.Queue.RevokeAndPurgeAsync(peer);
        await fixture.EnrolAsync(peer, mediator, peer);
        park.Release();
        var result = await delivery.WaitAsync(Deadline);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the recipient DID belongs to the same peer again");
        fixture.FakeStore!.Acknowledged.Should().BeEmpty();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
    }

    [Fact]
    public async Task A_delivery_parked_while_its_DID_moves_to_another_grant_does_not_keep_it_under_the_old_one()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        const string recipient = "did:example:moving";
        var (mediator, alice) = await fixture.EnrolledAsync(recipient);
        var bob = await fixture.NewPeerAsync();
        using var park = new Park(fixture.FakeStore!);

        var delivery = fixture.Queue.DeliverAsync(recipient, PickupFixture.Payload(20)).AsTask();
        await park.Arrived.WaitAsync(Deadline);
        await fixture.Queue.RevokeAndPurgeAsync(alice);
        await fixture.EnrolAsync(bob.Did, mediator, recipient);
        park.Release();
        var result = await delivery.WaitAsync(Deadline);

        result.Should().Be(new PickupDeliveryResult(PickupDeliveryOutcome.NotRegistered, null, null));
        fixture.Memory.MessageCount.Should().Be(0);
        (await MessagePickupLive.StatusAsync(fixture, bob.Did, mediator)).MessageCount.Should().Be(0, "nothing was moved to the new owner");
    }

    [Fact]
    public async Task A_live_session_gets_no_push_for_a_delivery_parked_across_the_revoke()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        using var park = new Park(fixture.FakeStore!);

        var delivery = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20)).AsTask();
        await park.Arrived.WaitAsync(Deadline);
        park.Lease.Should().NotBeNull("the record is admitted leased to the live session");
        await fixture.Queue.RevokeAndPurgeAsync(peer);
        park.Release();
        var result = await delivery.WaitAsync(Deadline);

        result.Outcome.Should().Be(PickupDeliveryOutcome.NotRegistered);
        socket.Attempts.Should().BeEmpty("nothing is pushed for a recipient that is no longer registered");
        fixture.Memory.MessageCount.Should().Be(0);
    }

    // ── The re-check itself ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_delivery_looks_the_recipient_up_again_after_storing()
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (_, peer) = await fixture.EnrolledAsync();
        mediation.Calls.Clear();
        mediation.Lookups.Clear();

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);

        mediation.Lookups.Should().Equal(peer, peer);
        fixture.FakeStore!.Calls.Should().Equal(nameof(IMessageStore.EnqueueAsync));
    }

    [Fact]
    public async Task A_recheck_that_fails_keeps_the_record_reports_Queued_and_pushes_nothing_live()
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var lookups = 0;
        mediation.OnFind = (did, ct) => Interlocked.Increment(ref lookups) == 2
            ? throw new HandlerBoomException(HandlerBoomException.Marker)
            : mediation.Inner.FindGrantByRecipientDidAsync(did, ct);

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "never lose an accepted message on a failed check");
        result.MessageId.Should().NotBeNull();
        socket.Attempts.Should().BeEmpty("no live push on an unconfirmed registration");
        fixture.Memory.MessageCount.Should().Be(1);
        fixture.FakeStore!.Released.Should().ContainSingle("the live lease it was admitted under is given back").Which.Ids.Should().Equal(result.MessageId);
        mediation.OnFind = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("deliverable at once by delivery-request").Which.Id.Should().Be(result.MessageId);
        var line = MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle().Subject;
        line.State["ExceptionType"].Should().Be(nameof(HandlerBoomException));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, peer, mediator, result.MessageId);
    }

    [Fact]
    public async Task A_recheck_cancelled_by_the_caller_keeps_the_record_and_reports_it_Queued()
    {
        // #95 Q-6: once the record is stored, a cancellation ends in Queued, not in an exception —
        // the caller's outcome must say the message is held.
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (mediator, peer) = await fixture.EnrolledAsync();
        using var cts = new CancellationTokenSource();
        var lookups = 0;
        mediation.OnFind = (did, ct) =>
        {
            if (Interlocked.Increment(ref lookups) == 2)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return mediation.Inner.FindGrantByRecipientDidAsync(did, ct);
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20), ct: cts.Token);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        result.MessageId.Should().NotBeNull();
        fixture.Memory.MessageCount.Should().Be(1, "cancelling after the message was stored leaves it stored");
        mediation.OnFind = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
    }

    [Fact]
    public async Task A_live_push_cancelled_by_the_caller_keeps_the_record_reports_it_Queued_and_gives_back_the_lease()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        using var cts = new CancellationTokenSource();
        socket.OnSend = (_, _) =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return null;
        };

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20), ct: cts.Token);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        fixture.FakeStore!.Released.Should().ContainSingle().Which.Ids.Should().Equal(result.MessageId);
        socket.OnSend = null;
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("deliverable at once by delivery-request").Which.Id.Should().Be(result.MessageId);
    }

    [Fact]
    public async Task A_cancellation_before_anything_is_stored_still_throws_and_stores_nothing()
    {
        var mediation = new MessagePickupLiveMediationStore();
        await using var fixture = PickupFixture.Create(fakeStore: true, services: s => s.AddSingleton<IMediationStore>(mediation));
        var (_, peer) = await fixture.EnrolledAsync();
        using var cts = new CancellationTokenSource();
        mediation.OnFind = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return default;
        };

        await FluentActions.Awaiting(async () => await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20), ct: cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        fixture.FakeStore!.CountOf(nameof(IMessageStore.EnqueueAsync)).Should().Be(0);
    }

    [Fact]
    public async Task A_record_that_cannot_be_removed_after_the_recheck_is_reported_retained_and_not_leased()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        using var park = new Park(fixture.FakeStore!);

        var delivery = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20)).AsTask();
        await park.Arrived.WaitAsync(Deadline);
        await fixture.Queue.RevokeAndPurgeAsync(peer);
        fixture.FakeStore!.OnAcknowledge = (_, _, _) => throw new HandlerBoomException(HandlerBoomException.Marker);
        park.Release();
        var result = await delivery.WaitAsync(Deadline);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the outcome tells the truth: the record is still held");
        socket.Attempts.Should().BeEmpty();
        fixture.FakeStore.Released.Should().ContainSingle().Which.Ids.Should().Equal(result.MessageId);
        fixture.Memory.MessageCount.Should().Be(1);
        MessagePickupLive.QueueLogs(fixture, LogLevel.Error).Should().ContainSingle().Which.State["ExceptionType"].Should().Be(nameof(HandlerBoomException));
        fixture.FakeStore.OnAcknowledge = null;
        (await fixture.Queue.RevokeAndPurgeAsync(peer)).Should().Be(new GrantRevocation(false, 1), "the host's purge reclaims it");
    }

    /// <summary>
    /// Parks the first <see cref="IMessageStore.EnqueueAsync"/> until released: the delivery has done
    /// its lookup and has not stored its record yet. Records what was stored once released.
    /// </summary>
    private sealed class Park : IDisposable
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PickupFakeStore _store;
        private int _parked;
        private int _released;

        public Park(PickupFakeStore store)
        {
            _store = store;
            store.OnEnqueue = async (message, lease, now, ct) =>
            {
                if (Interlocked.Exchange(ref _parked, 1) == 0)
                {
                    Lease = lease;
                    _arrived.TrySetResult();
                    await _release.Task.WaitAsync(ct);
                    Overlapped = Volatile.Read(ref _released) == 1;
                    StoredId = message.Id;
                }
                return await store.Inner.EnqueueAsync(message, lease, now, ct);
            };
        }

        public Task Arrived => _arrived.Task;
        public MessageLease? Lease { get; private set; }
        public string? StoredId { get; private set; }

        /// <summary>The delivery was still parked when the test released it, after the work in between.</summary>
        public bool Overlapped { get; private set; }

        public void Release()
        {
            Volatile.Write(ref _released, 1);
            _release.TrySetResult();
        }

        public void Dispose()
        {
            _release.TrySetResult();
            _store.OnEnqueue = null;
        }
    }
}
