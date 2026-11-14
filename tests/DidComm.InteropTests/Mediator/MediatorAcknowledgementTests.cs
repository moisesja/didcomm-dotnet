using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// The mediator's HTTP endpoint through <c>MapDidCommMediator</c> keeps the transport acknowledgement
/// (FR-ROUTE-11, #90): nothing of a forward is relayed, queued or counted until its 202 has been
/// committed, and a forward whose request is aborted before that is never relayed
/// (<c>Dropped-Unacknowledged</c>). Replay admission (FR-RR-08) runs on the HTTP endpoint (the
/// WebSocket case is in <see cref="MapDidCommMediatorTests"/>). Revocation (owner decision):
/// <c>RevokeAndPurgeAsync</c> ends the grant and empties its queue, and a forward that was already
/// on its way is removed by the queue's post-enqueue re-check (JC-6, L-057).
/// </summary>
public sealed class MediatorAcknowledgementTests
{
    [Fact]
    public async Task A_forward_is_neither_processed_nor_queued_until_its_202_has_been_committed()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(bob.Did);
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(note, bob.Did));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediator.Controls.ResponseGate = gate;

        var post = Task.Run(() => mediator.PostAsync(packed));
        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 1, "the handler admits the forward while the response is held");

        post.IsCompleted.Should().BeFalse("the response is held at OnStarting");
        (await ReturnRouteTestHost.WaitUntilAsync(() => mediator.Store.MessageCount > 0 || mediator.Outcomes.Count > 0, TimeSpan.FromMilliseconds(200)))
            .Should().BeFalse("nothing is queued or counted before the 202 is committed");
        mediator.Handler.Outstanding.Should().Be(1);

        gate.SetResult();
        var response = await post.WaitAsync(MediatorTestServer.Timeout);
        response.Status.Should().Be(HttpStatusCode.Accepted);
        response.Body.Should().BeEmpty();
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued);
        mediator.Store.MessageCount.Should().Be(1);
        var delivered = (await mediator.FetchAsync(bob)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(bob, delivered.Payload, alice)).Message.Id.Should().Be(note.Id);
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_forward_whose_request_is_aborted_before_the_202_is_committed_is_never_queued()
    {
        // As in MediatorEndpointRelayTests: TestServer cannot drop a connection while a response is
        // pending, so a middleware aborts the request after the endpoint returned and before the 202
        // is committed — the RequestAborted signal Kestrel raises for a dropped connection.
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (packed, forward) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        mediator.Controls.AbortNextPost = true;

        var act = async () => await mediator.PostAsync(packed);
        await act.Should().ThrowAsync<Exception>("the client never receives a response for an aborted request");

        await MediatorTestServer.WaitUntilAsync(() => mediator.Handler.Outstanding == 0, "the abort cancels the acknowledgement and releases the reservation");
        await mediator.FlushAsync();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnacknowledged);
        mediator.Store.MessageCount.Should().Be(0, "a forward whose sender never received the 202 is not relayed");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        mediator.Logs.Entries.Should().Contain(e => e.Contains(forward.Id) && e.Contains("did not acknowledge"));
        mediator.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeated_stamped_pickup_request_over_HTTP_is_answered_once()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var request = mediator.Prepare(MessagePickup.CreateStatusRequest(bob.Did, mediator.Did));
        var packed = await mediator.AuthcryptAsync(bob, request);

        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.OK);
        var repeat = await mediator.PostAsync(packed);
        repeat.Status.Should().Be(HttpStatusCode.Accepted, "a refused message is answered like an accepted one, with nothing in it (FR-RR-08)");
        repeat.Body.Should().BeEmpty();
        mediator.Logs.Entries.Should().Contain(e => e.Contains("dropped: replay admission") && e.Contains(request.Id) && e.Contains("Duplicate"));
    }

    // ---- revoke and purge ---------------------------------------------------------------------

    [Fact]
    public async Task RevokeAndPurge_ends_the_grant_empties_its_queue_and_later_forwards_for_its_recipients_are_dropped()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        var carol = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        await mediator.EnrolAsync(carol);
        var alice = await mediator.NewSenderAsync();
        for (var i = 0; i < 3; i++)
            await PostForwardAsync(mediator, alice, bob);
        await PostForwardAsync(mediator, alice, carol);
        mediator.Store.MessageCount.Should().Be(4);

        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 3));
        mediator.Store.MessageCount.Should().Be(1, "Carol's message is not Bob's");
        (await mediator.Mediation.GetGrantAsync(bob.Did, CancellationToken.None)).Should().BeNull();
        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: false, Purged: 0), "idempotent");

        await PostForwardAsync(mediator, alice, bob);
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.DroppedUnknown);
        mediator.Store.MessageCount.Should().Be(1);
        (await mediator.StatusAsync(carol)).MessageCount.Should().Be(1);
        mediator.LeakingLogLines().Should().BeEmpty();
        new GrantRevocation(true, 3).ToString().Should().NotContain("did:", "the result prints no DID");
    }

    [Fact]
    public async Task A_forward_parked_between_its_lookup_and_its_enqueue_while_the_grant_is_revoked_is_removed_by_the_recheck()
    {
        // L-057 / JC-6, through the real pipeline: the forward's delivery looks the grant up, then
        // parks inside the store's EnqueueAsync; the host revokes and purges (nothing to purge yet);
        // the enqueue completes after the purge, and the queue's re-check removes exactly that record.
        var parking = new EnqueueParkingStore(new InMemoryMessageStore(new MessagePickupOptions()));
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = services => services.AddSingleton<IMessageStore>(parking));
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var alice = await mediator.NewSenderAsync();
        var (packed, _) = await alice.ForwardAsync(mediator.Did, bob.Did, await alice.AuthcryptAsync(alice.Note(bob.Did), bob.Did));
        var parked = parking.ParkNextEnqueue();

        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await parked.Arrived.WaitAsync(MediatorTestServer.Timeout);
        (await mediator.Queue.RevokeAndPurgeAsync(bob.Did)).Should().Be(new GrantRevocation(Revoked: true, Purged: 0));
        parked.Release();
        await mediator.FlushAsync();

        mediator.Outcomes.Should().Equal(ForwardOutcomes.DroppedUnknown);
        parking.Inner.MessageCount.Should().Be(0, "the record stored after the purge was removed by the re-check");
        parking.Enqueued.Should().Be(1);
    }

    private static async Task PostForwardAsync(MediatorTestServer mediator, MediatorSender alice, TestPeer to)
    {
        var (packed, _) = await alice.ForwardAsync(mediator.Did, to.Did, await alice.AuthcryptAsync(alice.Note(to.Did), to.Did));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
    }

    /// <summary>A host <see cref="IMessageStore"/> around the product's in-memory store that can hold the next enqueue at the door.</summary>
    private sealed class EnqueueParkingStore(InMemoryMessageStore inner) : IMessageStore
    {
        private Parked? _next;
        private int _enqueued;

        public InMemoryMessageStore Inner { get; } = inner;

        public int Enqueued => Volatile.Read(ref _enqueued);

        public Parked ParkNextEnqueue()
        {
            var parked = new Parked();
            Interlocked.Exchange(ref _next, parked).Should().BeNull();
            return parked;
        }

        public async ValueTask<EnqueueOutcome> EnqueueAsync(QueuedMessage message, MessageLease? lease, DateTimeOffset now, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _next, null) is { } parked)
                await parked.ArriveAsync();
            Interlocked.Increment(ref _enqueued);
            return await Inner.EnqueueAsync(message, lease, now, ct);
        }

        public ValueTask<IReadOnlyList<QueuedMessage>> LeaseAsync(MessageLeaseRequest request, CancellationToken ct) => Inner.LeaseAsync(request, ct);
        public ValueTask<int> AcknowledgeAsync(string grantPeerDid, IReadOnlyList<string> messageIds, CancellationToken ct) => Inner.AcknowledgeAsync(grantPeerDid, messageIds, ct);
        public ValueTask<int> ReleaseAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, CancellationToken ct) => Inner.ReleaseAsync(grantPeerDid, messageIds, leaseId, ct);
        public ValueTask<int> PassOverAsync(string grantPeerDid, IReadOnlyList<string> messageIds, string leaseId, DateTimeOffset until, CancellationToken ct) => Inner.PassOverAsync(grantPeerDid, messageIds, leaseId, until, ct);
        public ValueTask<int> ReleaseLeasesAsync(string leaseOwner, CancellationToken ct) => Inner.ReleaseLeasesAsync(leaseOwner, ct);
        public ValueTask<QueueStatus> GetStatusAsync(string grantPeerDid, string? recipientDid, DateTimeOffset now, CancellationToken ct) => Inner.GetStatusAsync(grantPeerDid, recipientDid, now, ct);
        public ValueTask<int> PurgeRecipientAsync(string grantPeerDid, string recipientDid, CancellationToken ct) => Inner.PurgeRecipientAsync(grantPeerDid, recipientDid, ct);
        public ValueTask<int> PurgeGrantAsync(string grantPeerDid, CancellationToken ct) => Inner.PurgeGrantAsync(grantPeerDid, ct);
        public ValueTask<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken ct) => Inner.PurgeExpiredAsync(now, ct);

        public sealed class Parked
        {
            private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Arrived => _arrived.Task;

            public void Release() => _gate.TrySetResult();

            internal async Task ArriveAsync()
            {
                _arrived.TrySetResult();
                // Bounded, so a test that failed before Release() does not hold the pump for ever.
                await Task.WhenAny(_gate.Task, Task.Delay(MediatorTestServer.Timeout));
            }
        }
    }
}
