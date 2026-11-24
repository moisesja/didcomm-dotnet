using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Mediator.Tests.Pickup.MessagePickupStoreSupport;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.TestSupport;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The direct push step (#110, D6 step 3): <see cref="PickupCoordinator.PushNextDirectAsync"/> reads
/// the recipient's endpoint from the host's directory, takes the grant's next record from the store
/// under a push lease (marked in the same step), sends it once, and ends the lease exactly once:
/// removed after a 2xx; released with its mark kept after an attempt that did not deliver; given back
/// as unsent, mark cleared, when nothing was attempted (plan invariants D-1, D-5, D-6). Every test
/// runs against the library's store and a host's (<see cref="ReferenceMessageStore"/>, written from
/// the contract text). The replays of the #109 findings (Q-1, ADV-5 D3/D4, ADV-6 D1) are here at the
/// step level.
/// </summary>
public sealed class DirectPushStepTests
{
    private static CancellationToken None => CancellationToken.None;

    // A hang guard only: every wait below is on an event that is certain with a correct product.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    private static readonly Uri OtherInbox = new("https://inbox-two.direct-push.example/didcomm");

    // The store calls that end a take's lease.
    private static (string Method, string? LeaseId)[] Ends(HookedStore store)
        => store.Calls.Where(c => c.Method is nameof(IMessageStore.AcknowledgeAsync) or nameof(IMessageStore.ReleaseAsync) or nameof(IMessageStore.ReturnUnsentAsync)).ToArray();

    // The lease of the (one) push take since the last reset.
    private static MessageLease PushTakeLease(HookedStore store)
        => store.LeaseRequests.Should().ContainSingle(r => r.Lease.ForPush, "one push take").Subject.Lease;

    // ---- each step, and what it leaves in the store -----------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_2xx_removes_the_record_and_its_one_post_carries_exactly_the_stored_bytes_to_the_registered_endpoint(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var id = await world.QueueAsync(peer, seed: 1, size: 80);
        world.Store.Reset();
        var at = world.Clock.GetUtcNow();

        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);

        var post = world.Sender.Posts.Should().ContainSingle().Subject;
        post.Endpoint.Should().Be(DirectPushWorld.Inbox);
        post.Payload.Should().Equal(StandInJwe.OfSize(80, 1), "the stored message, unchanged");
        world.Directory.Calls.Should().ContainSingle().Which.GrantPeerDid.Should().Be(peer);

        // Taken under a push lease with no owner, for the lease timeout, any record of the grant.
        var request = world.Store.LeaseRequests.Should().ContainSingle().Subject;
        request.Lease.ForPush.Should().BeTrue("a direct push takes under a push lease: the store marks the record in the same step");
        request.Lease.Owner.Should().BeNull("no connection owns a direct push's lease");
        request.Lease.ExpiresAt.Should().Be(at + world.LeaseFor);
        request.AfterSequence.Should().BeNull();
        request.MaxMessages.Should().Be(1);
        request.RecipientDid.Should().BeNull();

        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.AcknowledgeAsync), null) }, "removed, and nothing else ends the lease");
        (await world.StatusCountAsync(peer)).Should().Be(0);
        (await world.PeekPickupAsync(peer)).Should().BeEmpty();
        id.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(StoreKind.InMemory, SendAnswer.Refuse)]
    [InlineData(StoreKind.InMemory, SendAnswer.Fail)]
    [InlineData(StoreKind.InMemory, SendAnswer.Throw)]
    [InlineData(StoreKind.Reference, SendAnswer.Refuse)]
    [InlineData(StoreKind.Reference, SendAnswer.Fail)]
    [InlineData(StoreKind.Reference, SendAnswer.Throw)]
    public async Task An_attempt_that_does_not_deliver_releases_the_lease_keeps_the_mark_and_only_a_delivery_request_gets_the_record_again(StoreKind kind, SendAnswer answer)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        // Live delivery on before the message arrives, so a live push would take it but for the mark;
        // the session is downgraded (no live target, live first does not apply) while the direct push runs.
        var pushedLive = new ConcurrentQueue<string>();
        var session = await world.GoLiveAsync(peer, pushedLive);
        var id = await world.QueueAsync(peer, seed: 1);
        session.Downgrade();
        world.Sender.Script.Enqueue(answer);
        world.Store.Reset();

        (await world.PushAsync(peer)).Should().Be(answer == SendAnswer.Refuse ? DirectStep.Refused : DirectStep.NotDelivered, "a 4xx refusal is told apart from every other failure");

        world.Sender.Posts.Should().ContainSingle();
        var lease = PushTakeLease(world.Store);
        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.ReleaseAsync), lease.Id) }, "released by the take's own lease id, once: the attempt is spent");

        // Never pushed again, direct or live, also once every lease of it would have run out.
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty);
        world.Clock.Advance(world.LeaseFor + TimeSpan.FromSeconds(1));
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty);
        session.Resume();
        (await world.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty);
        world.Sender.Posts.Should().ContainSingle("one attempt per message");
        pushedLive.Should().BeEmpty();

        // A delivery-request gets it, marked; it is still held and counted.
        var held = (await world.PeekPickupAsync(peer)).Should().ContainSingle().Subject;
        held.Id.Should().Be(id);
        held.PushTaken.Should().BeTrue();
        (await world.StatusCountAsync(peer)).Should().Be(1);
        if (answer == SendAnswer.Throw)
            world.Logs.Entries.Should().Contain(e => e.Rendered.Contains(nameof(HandlerBoomException), StringComparison.Ordinal), "the fault is logged by its type");
    }

    public enum NoEndpointCase
    {
        None,
        DirectoryThrows,
        DirectoryThrowsAnotherTokensCancellation,
        NotAPushTarget,
    }

    public static TheoryData<StoreKind, NoEndpointCase> NoEndpointCases()
    {
        var data = new TheoryData<StoreKind, NoEndpointCase>();
        foreach (var kind in Enum.GetValues<StoreKind>())
            foreach (var c in Enum.GetValues<NoEndpointCase>())
                data.Add(kind, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(NoEndpointCases))]
    public async Task Without_an_endpoint_the_mediator_may_push_to_nothing_is_taken_and_the_store_is_untouched(StoreKind kind, NoEndpointCase how)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var id = await world.QueueAsync(peer, seed: 1);
        switch (how)
        {
            case NoEndpointCase.None:
                world.Directory.Endpoint = null;
                break;
            case NoEndpointCase.DirectoryThrows:
                world.Directory.Fault = new HandlerBoomException(HandlerBoomException.Marker);
                break;
            case NoEndpointCase.DirectoryThrowsAnotherTokensCancellation:
                world.Directory.Fault = new OperationCanceledException(new CancellationToken(canceled: true));
                break;
            default:
                world.Sender.RefuseTarget = "Guard";
                break;
        }
        world.Store.Reset();

        (await world.PushAsync(peer)).Should().Be(DirectStep.NoEndpoint);

        world.Store.Calls.Should().BeEmpty("nothing is taken, nothing is read: the store is untouched");
        world.Sender.Posts.Should().BeEmpty();
        world.Directory.Calls.Should().ContainSingle();
        if (how == NoEndpointCase.DirectoryThrows)
            world.Logs.Entries.Should().Contain(e => e.Rendered.Contains(nameof(HandlerBoomException), StringComparison.Ordinal));
        if (how == NoEndpointCase.NotAPushTarget)
            world.Logs.Entries.Should().Contain(e => e.Rendered.Contains("(Guard)", StringComparison.Ordinal), "the reason word is logged");

        // Still eligible: unmarked, and pushed once there is an endpoint.
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeFalse();
        world.Directory.Endpoint = DirectPushWorld.Inbox;
        world.Directory.Fault = null;
        world.Sender.RefuseTarget = null;
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Posts.Should().ContainSingle();
        id.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_record_whose_recipient_DID_is_no_longer_registered_is_removed_and_not_sent(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        const string Recipient = "did:example:direct-push-alice-inbox";
        var peer = await world.GrantAsync("alice", Recipient);
        await world.QueueAsync(Recipient, seed: 1);
        await world.Mediation.UpdateRecipientsAsync(peer, new[] { new DidComm.Mediator.Coordination.RecipientUpdate(Recipient, DidComm.Mediator.Coordination.RecipientUpdateAction.Remove) }, None);
        world.Store.Reset();

        (await world.PushAsync(peer)).Should().Be(DirectStep.Removed);

        world.Sender.Posts.Should().BeEmpty();
        (await world.StatusCountAsync(peer)).Should().Be(0, "removed by id, as a pickup does");
        world.Store.CountOf(nameof(IMessageStore.ReleaseAsync)).Should().Be(0);
        world.Store.CountOf(nameof(IMessageStore.ReturnUnsentAsync)).Should().Be(0);
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task With_nothing_deliverable_the_step_is_empty_and_posts_nothing(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");

        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "nothing queued");

        // Not yet due: queued, never pushed before its time.
        await world.QueueAsync(peer, seed: 1, notBefore: world.Clock.GetUtcNow().AddMinutes(5));
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "not yet due");
        world.Sender.Posts.Should().BeEmpty();
        world.Directory.Calls.Should().HaveCount(2, "the directory is read once per push");

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered, "due now");
    }

    // ---- Q-1: what was not in the store at the take is never sent -------------------------------

    public enum Q1Case
    {
        Expired,
        AgedOut,
        Acknowledged,
        PurgedAndRegranted,
    }

    public static TheoryData<StoreKind, Q1Case> Q1Cases()
    {
        var data = new TheoryData<StoreKind, Q1Case>();
        foreach (var kind in Enum.GetValues<StoreKind>())
            foreach (var c in Enum.GetValues<Q1Case>())
                data.Add(kind, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(Q1Cases))]
    public async Task Q1_A_direct_push_never_sends_a_record_that_expired_aged_out_was_acknowledged_or_was_purged_and_regranted_before_its_take(StoreKind kind, Q1Case how)
    {
        // #109 Q-1: a job queued at forward time sent its message later whatever had happened to the
        // record since. Here the wake-up carries only the grant, and the push takes from the store.
        var age = TimeSpan.FromMinutes(10);
        using var world = DirectPushWorld.Create(kind, o => o.MaxMessageAge = how == Q1Case.AgedOut ? age : o.MaxMessageAge);
        var peer = await world.GrantAsync("alice");
        var expires = how == Q1Case.Expired ? world.Clock.GetUtcNow().ToUnixTimeSeconds() + 60 : (long?)null;
        var old = await world.QueueAsync(peer, seed: 1, expiresTime: expires);

        switch (how)
        {
            case Q1Case.Expired:
                world.Clock.Advance(TimeSpan.FromMinutes(2));
                break;
            case Q1Case.AgedOut:
                world.Clock.Advance(age + TimeSpan.FromMinutes(1));
                break;
            case Q1Case.Acknowledged:
                // Picked up and acknowledged by the recipient (messages-received).
                (await world.PeekPickupAsync(peer)).Should().ContainSingle();
                await world.Coordinator.AcknowledgeAsync(peer, new[] { old }, session: null, answerWithStatus: false, "ack-1", None);
                break;
            default:
                // The host revoked the grant and purged it; the same peer enrolled again since.
                (await world.Coordinator.RevokeAndPurgeAsync(peer, None)).Purged.Should().Be(1);
                (await world.GrantAsync("alice")).Should().Be(peer);
                break;
        }
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "the record is not in the store to be taken ({0})", how);

        // The step itself works: a message that is there is pushed, and only it.
        await world.QueueAsync(peer, seed: 2);
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty);
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().Equal(new byte[] { 2 }, "the old record is never posted");
    }

    [Theory]
    [InlineData(StoreKind.InMemory, false)]
    [InlineData(StoreKind.InMemory, true)]
    [InlineData(StoreKind.Reference, false)]
    [InlineData(StoreKind.Reference, true)]
    public async Task Q1_A_record_that_expires_between_its_take_and_its_send_is_not_sent(StoreKind kind, bool pastTheInstant)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1, expiresTime: world.Clock.GetUtcNow().ToUnixTimeSeconds() + 60);
        // Between the take (judged unexpired) and the send: the clock reaches the record's expiry instant, or passes it.
        world.Store.AfterLease = (request, taken) =>
        {
            if (request.Lease.ForPush && taken.Count == 1)
            {
                var at = taken[0].ExpiresAt!.Value;
                world.Clock.Advance(at - world.Clock.GetUtcNow() + (pastTheInstant ? TimeSpan.FromTicks(1) : TimeSpan.Zero));
            }
            return Task.CompletedTask;
        };
        world.Store.Reset();

        (await world.PushAsync(peer)).Should().Be(DirectStep.Expired);

        world.Sender.Posts.Should().BeEmpty("an expired message is never sent");
        var lease = PushTakeLease(world.Store);
        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.ReturnUnsentAsync), lease.Id) }, "never attempted: given back as unsent, once");
        world.Store.AfterLease = null;
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "expired: never handed out again");
        (await world.StatusCountAsync(peer)).Should().Be(0);
    }

    // ---- the deadline ---------------------------------------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_deadline_that_passes_during_the_take_sends_nothing_and_gives_the_record_back_as_unsent(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        using var cts = new CancellationTokenSource();
        world.Store.AfterLease = (request, _) =>
        {
            if (request.Lease.ForPush)
                cts.Cancel(); // the take came back after the deadline
            return Task.CompletedTask;
        };
        world.Store.Reset();

        var thrown = await Record.ExceptionAsync(async () => await world.PushAsync(peer, cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>("the deadline propagates after the give-back");
        world.Sender.Posts.Should().BeEmpty("nothing is sent after the deadline");
        var lease = PushTakeLease(world.Store);
        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.ReturnUnsentAsync), lease.Id) });

        // Unmarked: the next direct push takes it and sends it.
        world.Store.AfterLease = null;
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeFalse();
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Posts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_deadline_that_passes_during_the_send_releases_the_lease_keeps_the_mark_and_propagates(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        using var cts = new CancellationTokenSource();
        world.Sender.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Sender.ExpectSend();
        world.Store.Reset();

        var push = world.PushAsync(peer, cts.Token).AsTask();
        await world.Sender.Entered.WaitAsync(HangGuard);
        cts.Cancel();
        var thrown = await Record.ExceptionAsync(() => push.WaitAsync(HangGuard));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        var lease = PushTakeLease(world.Store);
        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.ReleaseAsync), lease.Id) }, "attempted: released, the mark kept");
        world.Sender.Gate = null;
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "the attempt is spent");
        world.Sender.Posts.Should().ContainSingle();
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
    }

    [Theory]
    [InlineData(StoreKind.InMemory, false)]
    [InlineData(StoreKind.InMemory, true)]
    [InlineData(StoreKind.Reference, false)]
    [InlineData(StoreKind.Reference, true)]
    public async Task A_deadline_that_passes_while_the_directory_answers_propagates_and_nothing_is_taken(StoreKind kind, bool directoryThrowsItsOwnFault)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        using var cts = new CancellationTokenSource();
        world.Directory.OnFind = (_, ct) =>
        {
            cts.Cancel();
            // A directory that honours the token, or one that fails its own way once cancelled.
            if (directoryThrowsItsOwnFault)
                throw new HandlerBoomException(HandlerBoomException.Marker);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        world.Store.Reset();

        var thrown = await Record.ExceptionAsync(async () => await world.PushAsync(peer, cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>("cancellation is what leaves, never the directory's own fault");
        world.Store.Calls.Should().BeEmpty();
        world.Sender.Posts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_call_whose_deadline_has_already_passed_never_reaches_the_directory_or_the_store(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        world.Store.Reset();

        var thrown = await Record.ExceptionAsync(async () => await world.PushAsync(peer, cts.Token));

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        world.Directory.Calls.Should().BeEmpty("no host code runs for a push that is already over");
        world.Store.Calls.Should().BeEmpty();
        world.Sender.Posts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task The_step_returns_to_its_caller_before_any_host_code_runs_so_a_directory_that_blocks_cannot_hold_the_caller(StoreKind kind)
    {
        // The push worker's deadline (WaitAsync) can only govern a call that has returned its task.
        // One pool thread is parked in the directory below: raise the floor first (L-065).
        MessagePickupStoreKit.RaiseThreadPoolFloor(2);
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        using var unblock = new ManualResetEventSlim(false);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Directory.OnFind = (_, _) =>
        {
            reached.TrySetResult();
            unblock.Wait(); // synchronous host code that ignores the token
            return Task.CompletedTask;
        };
        try
        {
            // The call is made on a thread of its own, so a call that blocks shows up as a timeout here, not a hang.
            var call = Task.Factory.StartNew(() => world.PushAsync(peer).AsTask(), None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var push = await call.WaitAsync(HangGuard);
            await reached.Task.WaitAsync(HangGuard);
            push.IsCompleted.Should().BeFalse("the directory is still blocked, and the caller already holds the task");
            unblock.Set();
            (await push.WaitAsync(HangGuard)).Should().Be(DirectStep.Delivered);
        }
        finally
        {
            unblock.Set();
        }
    }

    // ---- where it goes ----------------------------------------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task The_post_goes_to_the_endpoint_read_before_the_take_even_when_the_directory_changes_during_the_take(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        await world.QueueAsync(peer, seed: 2);
        world.Store.AfterLease = (request, _) =>
        {
            if (request.Lease.ForPush)
                world.Directory.Endpoint = OtherInbox; // the host moved the recipient meanwhile
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource();

        (await world.PushAsync(peer, cts.Token)).Should().Be(DirectStep.Delivered);

        var first = world.Sender.Posts.Should().ContainSingle().Subject;
        first.Endpoint.Should().Be(DirectPushWorld.Inbox, "the endpoint read before the take");
        first.Token.Should().Be(cts.Token, "the send runs under the caller's deadline");
        var call = world.Directory.Calls.Should().ContainSingle("the directory is read once per push").Subject;
        call.GrantPeerDid.Should().Be(peer);
        call.Token.Should().Be(cts.Token, "the directory gets the caller's token");

        // The change applies from the next push.
        world.Store.AfterLease = null;
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Posts.Select(p => p.Endpoint).Should().Equal(DirectPushWorld.Inbox, OtherInbox);
        world.Directory.Calls.Should().HaveCount(2);
    }

    public enum PurgeMoment
    {
        BeforeTheTake,
        DuringAnAcceptedSend,
        DuringARefusedSend,
    }

    public static TheoryData<StoreKind, PurgeMoment> PurgeCases()
    {
        var data = new TheoryData<StoreKind, PurgeMoment>();
        foreach (var kind in Enum.GetValues<StoreKind>())
            foreach (var c in Enum.GetValues<PurgeMoment>())
                data.Add(kind, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(PurgeCases))]
    public async Task A_revoke_and_purge_before_the_take_leaves_nothing_to_send_and_one_during_the_send_lets_it_finish_and_nothing_is_pushed_again(StoreKind kind, PurgeMoment when)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        if (when == PurgeMoment.BeforeTheTake)
        {
            // The endpoint has been read; the take has not happened yet.
            world.Directory.OnFind = async (_, _) => (await world.Coordinator.RevokeAndPurgeAsync(peer, None)).Purged.Should().Be(1);
        }
        else
        {
            world.Sender.OnSend = async _ => (await world.Coordinator.RevokeAndPurgeAsync(peer, None)).Purged.Should().Be(1);
            world.Sender.Script.Enqueue(when == PurgeMoment.DuringAnAcceptedSend ? SendAnswer.Accept : SendAnswer.Fail);
        }

        var step = await world.PushAsync(peer);

        step.Should().Be(when switch
        {
            PurgeMoment.BeforeTheTake => DirectStep.Empty,
            PurgeMoment.DuringAnAcceptedSend => DirectStep.Delivered,
            _ => DirectStep.NotDelivered,
        });
        world.Sender.Posts.Should().HaveCount(when == PurgeMoment.BeforeTheTake ? 0 : 1, "a send in flight completes; nothing is sent after a purge that came first");
        (await world.StatusCountAsync(peer)).Should().Be(0, "the purge removed it; the step's own end was a no-op");

        // Enrolled again: nothing of the old grant is pushed, and a new message is.
        world.Directory.OnFind = null;
        world.Sender.OnSend = null;
        await world.GrantAsync("alice");
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty);
        await world.QueueAsync(peer, seed: 2);
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().Equal(when == PurgeMoment.BeforeTheTake ? new byte[] { 2 } : new byte[] { 1, 2 });
    }

    // ---- one mark for both pushes ------------------------------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_record_a_live_push_took_is_never_taken_by_a_direct_push(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var pushedLive = new ConcurrentQueue<string>();
        var session = await world.GoLiveAsync(peer, pushedLive);
        var id = await world.QueueAsync(peer, seed: 1);
        (await world.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Sent);
        pushedLive.Should().Equal(id);

        // The session closes and its lease runs out unacknowledged; the mark stays.
        session.Close();
        world.Clock.Advance(world.LeaseFor + TimeSpan.FromSeconds(1));
        world.Store.Reset();
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "pushed live once: the recipient asks for it now");

        world.Sender.Posts.Should().BeEmpty();
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_record_a_direct_push_took_is_never_taken_by_a_live_push(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var pushedLive = new ConcurrentQueue<string>();
        var session = await world.GoLiveAsync(peer, pushedLive);
        await world.QueueAsync(peer, seed: 1);
        // Downgraded while the direct push runs (no live target), live again afterwards.
        session.Downgrade();
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NotDelivered);
        session.Resume();

        (await world.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty, "pushed directly once: the recipient asks for it now");

        pushedLive.Should().BeEmpty();
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
    }

    // ---- live first, decided right before the take (QA1-2) ------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_session_that_turns_live_delivery_on_while_the_directory_answers_wins_and_nothing_is_taken(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var counted = await world.QueueAsync(peer, seed: 1); // before live-on: its status counts it
        var pushedLive = new ConcurrentQueue<string>();
        PickupFakeSession? session = null;
        world.Directory.OnFind = async (_, _) => { session ??= await world.GoLiveAsync(peer, pushedLive); };
        world.Store.Reset();

        (await world.PushAsync(peer)).Should().Be(DirectStep.LiveSession, "the live session wins (D6)");

        world.Store.LeaseRequests.Should().BeEmpty("nothing is taken");
        world.Sender.Posts.Should().BeEmpty();
        (await world.PushAsync(peer)).Should().Be(DirectStep.LiveSession, "while the session is live, a direct push takes nothing");
        (await world.Coordinator.PushNextLiveAsync(peer, None)).Should().Be(LiveStep.Empty, "what the live-on status counted is the recipient's to pick up");
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeFalse("untouched by either push");
        pushedLive.Should().BeEmpty();

        // Once the session is no live target, the direct push goes ahead.
        session!.Downgrade();
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Posts.Should().ContainSingle();
        counted.Should().NotBeNullOrEmpty();
    }

    // ---- the live-on wake-up and a DID with no grant (ADV-110-1b-2, 2b-2, 2b-3) -------------------------

    // Turning live delivery on wakes the push workers whatever the ownership read said (a grant may be
    // made meanwhile, ADV-110-2b-2). For a DID with no grant the direct step may ask the directory, and
    // whatever it answers, nothing is taken or sent (IRecipientEndpointDirectory's contract).
    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task Turning_live_delivery_on_wakes_the_push_workers_for_any_peer_and_a_direct_push_for_a_DID_with_no_grant_takes_and_sends_nothing(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var woken = new ConcurrentQueue<string>();
        world.Coordinator.LiveDeliveryStarted += woken.Enqueue;
        var enrolled = await world.GrantAsync("alice");
        const string Stranger = "did:example:direct-push-never-enrolled";

        var session = await world.GoLiveAsync(Stranger, new ConcurrentQueue<string>());
        await world.GoLiveAsync(enrolled, new ConcurrentQueue<string>());
        woken.Should().Equal(Stranger, enrolled);

        // The stranger's session downgraded: its direct step asks the directory (an endpoint, as for
        // anyone), takes nothing and sends nothing.
        session.Downgrade();
        world.Store.Reset();
        (await world.PushAsync(Stranger)).Should().Be(DirectStep.Empty);
        world.Directory.Calls.Should().Contain(c => c.GrantPeerDid == Stranger, "the directory may be asked about a DID with no grant");
        world.Sender.Posts.Should().BeEmpty();
        Ends(world.Store).Should().BeEmpty("nothing was taken");
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_record_still_held_under_a_revoked_grant_is_removed_as_unregistered_and_never_sent_whatever_the_directory_answers(StoreKind kind)
    {
        // A host that revokes through IMediationStore.RevokeAsync alone, without the purge: the record
        // stays under the peer's DID. The direct step asks the directory (it answers an endpoint), takes
        // the record, finds its recipient DID no longer registered, and removes it unsent.
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        (await world.Mediation.RevokeAsync(peer, None)).Should().BeTrue();

        (await world.PushAsync(peer)).Should().Be(DirectStep.Removed);

        world.Sender.Posts.Should().BeEmpty("nothing is sent for a DID with no grant");
        (await world.StatusCountAsync(peer)).Should().Be(0);
    }

    // ---- ADV-5 D3/D4 and ADV-6 D1: progress lives on the record -------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task ADV5_D3_D4_A_refused_record_is_posted_once_never_blocks_later_messages_and_never_rides_along_with_them(StoreKind kind)
    {
        // #109 ADV-5: the oldest refused record was re-sent at every wake-up and the rest never went.
        // Each call here is one push of the step; the push workers space them (after a push that does
        // not deliver, the next one is at the first wake-up after its recipient's direct pause:
        // DirectPushPauseTests).
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        var sizes = new[] { 100, 150, 200, 250 };
        for (var i = 0; i < sizes.Length; i++)
            await world.QueueAsync(peer, seed: (byte)(i + 1), size: sizes[i]);
        world.Sender.Script.Enqueue(SendAnswer.Fail); // the oldest, A, is refused; the rest are accepted

        var steps = new List<DirectStep>();
        for (var wake = 0; wake < 10; wake++)
            steps.Add(await world.PushAsync(peer));

        steps.Should().Equal(new[] { DirectStep.NotDelivered, DirectStep.Delivered, DirectStep.Delivered, DirectStep.Delivered }.Concat(Enumerable.Repeat(DirectStep.Empty, 6)));
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().Equal(new byte[] { 1, 2, 3, 4 }, "each record is posted once, A first, and A never again");
        for (var i = 0; i < sizes.Length; i++)
            world.Sender.Bodies[i].Should().Equal(StandInJwe.OfSize(sizes[i], (byte)(i + 1)), "a POST carries exactly one stored message");
        world.Sender.Bodies.Sum(b => (long)b.Length).Should().Be(sizes.Sum(), "the bytes posted are the bytes stored, once");

        // A waits for pickup, marked; a later message is not held up by it.
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
        await world.QueueAsync(peer, seed: 5, size: 120);
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().Equal(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task ADV6_D1_After_a_restart_over_the_same_store_no_attempted_record_is_posted_again(StoreKind kind)
    {
        // #109 ADV-6 D1: progress kept in a cache was lost on restart, and refused records went again.
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        for (byte seed = 1; seed <= 4; seed++)
            await world.QueueAsync(peer, seed);
        var before = world.Coordinator;

        // A refused, B faulted, C in flight when the process goes.
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        world.Sender.Script.Enqueue(SendAnswer.Throw);
        (await world.PushAsync(before, peer)).Should().Be(DirectStep.NotDelivered);
        (await world.PushAsync(before, peer)).Should().Be(DirectStep.NotDelivered);
        var inFlightGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Sender.Gate = inFlightGate;
        world.Sender.ExpectSend();
        var inFlight = world.PushAsync(before, peer).AsTask();
        await world.Sender.Entered.WaitAsync(HangGuard);
        world.Sender.Gate = null;

        // A new mediator over the same store, once the old one's lease would have run out.
        var after = world.Restart();
        world.Clock.Advance(world.LeaseFor + TimeSpan.FromSeconds(1));
        var steps = new List<DirectStep>();
        for (var wake = 0; wake < 5; wake++)
            steps.Add(await world.PushAsync(after, peer));

        steps.Should().Equal(DirectStep.Delivered, DirectStep.Empty, DirectStep.Empty, DirectStep.Empty, DirectStep.Empty);
        world.Sender.Bodies.Select(DirectPushWorld.SeedOf).Should().Equal(new byte[] { 1, 2, 3, 4 }, "every record is posted at most once, across the restart");

        // The old process's send ends after all (refused): it changes nothing for anyone.
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        inFlightGate.SetResult();
        (await inFlight.WaitAsync(HangGuard)).Should().Be(DirectStep.NotDelivered);
        (await world.PushAsync(after, peer)).Should().Be(DirectStep.Empty);
        world.Sender.Posts.Should().HaveCount(4);
        (await world.PeekPickupAsync(peer)).Select(m => (DirectPushWorld.SeedOf(m.Payload.ToArray()), m.PushTaken))
            .Should().Equal(((byte)1, true), ((byte)2, true), ((byte)3, true));
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_host_store_that_hands_a_push_a_record_an_earlier_push_took_is_refused_and_the_record_is_not_sent_again(StoreKind kind)
    {
        // A store that lost the mark (plan residual 2): the take's contract check refuses the record,
        // fail-closed. The record is given back under this take's lease without touching the mark
        // another push set, the store fault propagates to the push worker, and pickup still works.
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NotDelivered);
        world.Store.IgnorePushMark = true;
        world.Store.Reset();

        var thrown = await Record.ExceptionAsync(async () => await world.PushAsync(peer));

        thrown.Should().BeOfType<InvalidOperationException>("a record outside the contract is a store fault");
        world.Sender.Posts.Should().ContainSingle("the refused record is never posted again");
        var lease = PushTakeLease(world.Store);
        Ends(world.Store).Should().Equal(new (string, string?)[] { (nameof(IMessageStore.ReturnUnsentAsync), lease.Id) }, "given back under this take's own lease");
        world.Store.IgnorePushMark = false;
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue("the earlier push's mark is not this take's to clear");
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty);
    }

    // ---- a removal that fails, and the log lines ---------------------------------------------------

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task A_2xx_whose_removal_fails_is_still_delivered_and_the_record_is_kept_from_pushes(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        var peer = await world.GrantAsync("alice");
        await world.QueueAsync(peer, seed: 1);
        world.Store.FailAcknowledge = true;

        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered, "the endpoint accepted it");

        world.Logs.Entries.Should().Contain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Rendered.Contains(nameof(HandlerBoomException), StringComparison.Ordinal));
        world.Store.FailAcknowledge = false;
        world.Clock.Advance(world.LeaseFor + TimeSpan.FromSeconds(1));
        (await world.PushAsync(peer)).Should().Be(DirectStep.Empty, "never pushed again; a delivery-request may deliver it a second time");
        world.Sender.Posts.Should().ContainSingle();
        (await world.PeekPickupAsync(peer)).Should().ContainSingle().Which.PushTaken.Should().BeTrue();
    }

    [Theory]
    [InlineData(StoreKind.InMemory)]
    [InlineData(StoreKind.Reference)]
    public async Task No_log_line_of_the_direct_step_carries_a_DID_a_record_id_an_endpoint_or_an_exception_message(StoreKind kind)
    {
        using var world = DirectPushWorld.Create(kind);
        const string Recipient = "did:example:direct-push-alice-secret-inbox";
        var peer = await world.GrantAsync("alice", Recipient);
        var endpoint = new Uri("https://secret-host.direct-push.example/secret-path?secret-query=1");
        world.Directory.Endpoint = endpoint;
        var ids = new List<string>();
        async Task QueueAsync(byte seed) => ids.Add(await world.QueueAsync(Recipient, seed));

        await QueueAsync(1);
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        await QueueAsync(2);
        world.Sender.Script.Enqueue(SendAnswer.Fail);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NotDelivered);
        await QueueAsync(3);
        world.Sender.Script.Enqueue(SendAnswer.Throw);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NotDelivered);
        await QueueAsync(4);
        world.Store.FailAcknowledge = true;
        (await world.PushAsync(peer)).Should().Be(DirectStep.Delivered);
        world.Store.FailAcknowledge = false;
        world.Directory.Fault = new HandlerBoomException(HandlerBoomException.Marker);
        (await world.PushAsync(peer)).Should().Be(DirectStep.NoEndpoint);
        world.Directory.Fault = null;
        world.Sender.RefuseTarget = "Guard";
        (await world.PushAsync(peer)).Should().Be(DirectStep.NoEndpoint);
        world.Sender.RefuseTarget = null;
        await QueueAsync(5);
        world.Store.AfterLease = (request, taken) =>
        {
            if (request.Lease.ForPush && taken.Count == 1)
                world.Clock.Advance(TimeSpan.FromDays(31)); // past the mailbox age limit
            return Task.CompletedTask;
        };
        (await world.PushAsync(peer)).Should().Be(DirectStep.Expired);

        // The lines were written (not vacuous): the faults by type, the refusal by its reason word.
        world.Logs.Entries.Count(e => e.Rendered.Contains(nameof(HandlerBoomException), StringComparison.Ordinal)).Should().Be(3, "the send fault, the removal fault and the directory fault");
        world.Logs.Entries.Should().Contain(e => e.Rendered.Contains("(Guard)", StringComparison.Ordinal));
        var secrets = new[] { peer, Recipient, endpoint.ToString(), endpoint.Host, endpoint.AbsolutePath, "secret-query", HandlerBoomException.Marker }.Concat(ids).ToArray();
        PipelineLogs.Leaking(world.Logs, secrets).Should().BeEmpty();
        world.Logs.Entries.Should().OnlyContain(e => e.Exception == null, "no exception object (and its message) reaches a provider");
    }
}
