using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Transports.ReturnRoute;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Every way a live push can end, on both push paths, in both orders of the two ends of its lease
/// (PR #143 review P2-1; owner decision 2026-11-21, OD-5). The two paths are the queue's own push of
/// a record admitted already leased (<see cref="MessagePickupQueue.DeliverAsync"/>) and the push
/// workers' take (<see cref="PickupCoordinator.PushNextLiveAsync"/>). The two ends are the session
/// close's clean-up (<see cref="IMessageStore.ReleaseLeasesAsync"/> by owner) and the coordinator's
/// own give-back (by id, as unsent, or a pass-over).
/// The rule, by construction: the take marks the record, and only the push's own "not sent" clears
/// the mark — the session answered <see cref="ReturnRouteSendResult.NotSent"/> or
/// <see cref="ReturnRouteSendResult.TooLarge"/>, open or closing, or the push never got to the
/// session. Anything else counts as pushed, so the message is never pushed live again. Whatever
/// the outcome, a delivery-request delivers the message and the quota does not move.
/// </summary>
public sealed class MessagePickupLivePushExitMatrixTests
{
    private static CancellationToken None => CancellationToken.None;

    private static readonly TimeSpan LeaseFor = new MessagePickupOptions().LeaseTimeout;

    public enum PushPath
    {
        Queue,
        Worker,
    }

    public enum Exit
    {
        SentOpen,
        SentThenClosed,
        NotSentOpen,
        NotSentClosed,
        ClosedThenThrows,
        ClosedThenCancelled,
        TooLarge,
        ThrowsAfterAttempt,
        CancelledAfterAttempt,
        ClosedBeforeAttempt,
        CancelledBeforeAttempt,
    }

    public enum CleanUpOrder
    {
        OwnerCleanUpFirst,
        CoordinatorGiveBackFirst,
    }

    // Handed to the session and not answered "not sent": counts as pushed (OD-5).
    private static bool CountsAsPushed(Exit exit)
        => exit is Exit.SentOpen or Exit.SentThenClosed or Exit.ClosedThenThrows or Exit.ClosedThenCancelled or Exit.ThrowsAfterAttempt or Exit.CancelledAfterAttempt;

    private static bool Attempted(Exit exit) => exit is not (Exit.ClosedBeforeAttempt or Exit.CancelledBeforeAttempt);

    // The coordinator's own give-back, as the store sees it; null when it gives nothing back.
    private static string? GiveBack(PushPath path, Exit exit) => exit switch
    {
        Exit.SentOpen => null,
        Exit.SentThenClosed or Exit.ClosedThenThrows or Exit.ClosedThenCancelled or Exit.ThrowsAfterAttempt or Exit.CancelledAfterAttempt => "release",
        Exit.TooLarge when path == PushPath.Worker => "pass-over",
        _ => "return-unsent",
    };

    public static TheoryData<PushPath, Exit, CleanUpOrder> Cases()
    {
        var data = new TheoryData<PushPath, Exit, CleanUpOrder>();
        foreach (var path in Enum.GetValues<PushPath>())
            foreach (var exit in Enum.GetValues<Exit>())
                foreach (var order in Enum.GetValues<CleanUpOrder>())
                    data.Add(path, exit, order);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_live_push_is_kept_from_live_pushes_exactly_when_it_was_handed_to_the_session_and_not_answered_not_sent(PushPath path, Exit exit, CleanUpOrder order)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var older = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, older);
        var newer = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, newer);
        var store = fixture.FakeStore!;
        string? id = null;
        if (path == PushPath.Worker)
            id = await fixture.QueueAsync(peer, PickupFixture.Payload(40, 1), new PickupDeliveryOptions(AllowLive: false));

        // The two ends of the push's lease, ordered by gates, each recorded once it has run to its end.
        var events = new ConcurrentQueue<string>();
        var ownerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (order == CleanUpOrder.OwnerCleanUpFirst)
            ownerGate.SetResult();
        store.OnReleaseLeases = async (owner, ct) =>
        {
            await ownerGate.Task.WaitAsync(MessagePickupLive.Deadline);
            var released = await store.Inner.ReleaseLeasesAsync(owner, ct);
            events.Enqueue("owner");
            ownerDone.TrySetResult();
            return released;
        };
        async ValueTask<int> GiveBackAsync(string kind, Func<ValueTask<int>> inner)
        {
            if (order == CleanUpOrder.OwnerCleanUpFirst)
            {
                // The session closes (if the exit left it open) and its clean-up runs to its end first.
                newer.Close();
                await ownerDone.Task.WaitAsync(MessagePickupLive.Deadline);
            }
            var changed = await inner();
            events.Enqueue(kind);
            return changed;
        }
        store.OnRelease = (g, ids, lease, ct) => GiveBackAsync("release", () => store.Inner.ReleaseAsync(g, ids, lease, ct));
        store.OnReturnUnsent = (g, ids, lease, ct) => GiveBackAsync("return-unsent", () => store.Inner.ReturnUnsentAsync(g, ids, lease, ct));
        store.OnPassOver = (g, ids, lease, until, ct) => GiveBackAsync("pass-over", () => store.Inner.PassOverAsync(g, ids, lease, until, ct));

        // The exit.
        using var cts = new CancellationTokenSource();
        var sends = 0;
        newer.OnSend = (_, _) =>
        {
            Interlocked.Increment(ref sends);
            switch (exit)
            {
                case Exit.SentOpen:
                    return ReturnRouteSendResult.Sent;
                case Exit.SentThenClosed:
                    newer.Close();
                    return ReturnRouteSendResult.Sent;
                case Exit.NotSentOpen:
                    return ReturnRouteSendResult.NotSent;
                case Exit.NotSentClosed:
                    newer.Close();
                    return ReturnRouteSendResult.NotSent;
                case Exit.ClosedThenThrows:
                    newer.Close();
                    throw new HandlerBoomException(HandlerBoomException.Marker);
                case Exit.ClosedThenCancelled:
                    newer.Close();
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                case Exit.TooLarge:
                    return ReturnRouteSendResult.TooLarge;
                case Exit.ThrowsAfterAttempt:
                    throw new HandlerBoomException(HandlerBoomException.Marker);
                case Exit.CancelledAfterAttempt:
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                default:
                    return ReturnRouteSendResult.Sent; // never reached: the exit is before the send
            }
        };
        void BeforeTheSend()
        {
            if (exit == Exit.ClosedBeforeAttempt)
                newer.Close();
            else if (exit == Exit.CancelledBeforeAttempt)
                cts.Cancel();
        }
        if (path == PushPath.Queue)
        {
            store.OnEnqueue = async (message, lease, now, ct) =>
            {
                var admitted = await store.Inner.EnqueueAsync(message, lease, now, ct);
                BeforeTheSend();
                return admitted;
            };
        }
        else
        {
            store.OnLease = async (request, ct) =>
            {
                var leased = await store.Inner.LeaseAsync(request, ct);
                if (request.Lease.ForPush && leased.Count > 0)
                    BeforeTheSend();
                return leased;
            };
        }

        PickupDeliveryResult? delivered = null;
        LiveStep? step = null;
        Exception? thrown = null;
        if (path == PushPath.Queue)
            delivered = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(40, 1), ct: cts.Token);
        else
            thrown = await Record.ExceptionAsync(async () => step = await fixture.Queue.Coordinator.PushNextLiveAsync(peer, cts.Token));
        store.OnEnqueue = null;
        store.OnLease = null;
        store.OnRelease = null;
        store.OnReturnUnsent = null;
        store.OnPassOver = null;

        // The other end lands now: the session closes if it is still open, and its clean-up runs.
        newer.Close();
        ownerGate.TrySetResult();
        await ownerDone.Task.WaitAsync(MessagePickupLive.Deadline);

        // What the call reported.
        if (path == PushPath.Queue)
        {
            delivered!.Value.Outcome.Should().Be(exit == Exit.SentOpen ? PickupDeliveryOutcome.PushedLive : PickupDeliveryOutcome.Queued, "a stored message is reported held whatever ends the call");
            id = delivered.Value.MessageId;
        }
        else
        {
            switch (exit)
            {
                case Exit.SentOpen:
                    step.Should().Be(LiveStep.Sent);
                    break;
                case Exit.TooLarge:
                    step.Should().Be(LiveStep.PassedOver);
                    break;
                case Exit.ClosedThenThrows or Exit.ThrowsAfterAttempt:
                    thrown.Should().BeOfType<HandlerBoomException>("a session fault propagates after the give-back");
                    break;
                case Exit.ClosedThenCancelled or Exit.CancelledAfterAttempt or Exit.CancelledBeforeAttempt:
                    thrown.Should().BeAssignableTo<OperationCanceledException>("the deadline propagates after the give-back");
                    break;
                default:
                    step.Should().Be(LiveStep.NotSent);
                    break;
            }
            if (thrown is not null)
                step.Should().BeNull();
            else
                thrown.Should().BeNull();
        }
        id.Should().NotBeNull();
        Volatile.Read(ref sends).Should().Be(Attempted(exit) ? 1 : 0, "precondition: the push was handed to the session, or not");

        // The forced interleaving (precondition): which give-back the coordinator made, and in which order.
        var giveBack = GiveBack(path, exit);
        if (giveBack is null)
            events.Should().Equal("owner");
        else if (order == CleanUpOrder.OwnerCleanUpFirst)
            events.Should().Equal("owner", giveBack);
        else
            events.Should().Equal(giveBack, "owner");

        // The mark: read by a delivery-request's take whose lease ends at once (it changes no mark).
        fixture.Clock.Advance(LeaseFor); // past a pass-over's first period and every lease of the push
        var now = fixture.Clock.Now;
        var peek = await store.Inner.LeaseAsync(new MessageLeaseRequest(peer, null, 10, long.MaxValue, now, new MessageLease(null, now)), None);
        var expectMarked = CountsAsPushed(exit);
        peek.Should().ContainSingle().Which.PushTaken.Should().Be(expectMarked, "marked exactly when the push was handed to the session and not answered NotSent or TooLarge ({0}, {1}, {2})", path, exit, order);

        // A delivery-request always delivers it.
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(new[] { id }, "a delivery-request always delivers it");

        // A later live push takes it, or not, accordingly.
        fixture.Clock.Advance(LeaseFor);
        var next = await fixture.Queue.Coordinator.PushNextLiveAsync(peer, None);
        if (expectMarked)
        {
            next.Should().Be(LiveStep.Empty, "a push that may have reached the connection is never pushed live again");
            older.Sent.Should().BeEmpty();
        }
        else
        {
            next.Should().Be(LiveStep.Sent, "a push that was not sent stays eligible for a live push");
            PushedIds(older).Should().Equal(id);
        }

        // The quota never moved.
        fixture.Memory.MessageCount.Should().Be(1);
        (await store.Inner.GetStatusAsync(peer, null, fixture.Clock.Now, None)).MessageCount.Should().Be(1);
    }

    // ---- the store: a close racing the coordinator's own give-back ------------------------------------

    [Theory]
    [InlineData("release by id", true)]
    [InlineData("return as unsent", false)]
    [InlineData("pass over", false)]
    public async Task A_connections_close_racing_the_pushs_own_give_back_never_changes_what_the_give_back_decides(string giveBack, bool marked)
    {
        // The two calls run at once, many times over, so every interleaving the lock allows occurs.
        // The mark does not depend on which lands first: only the give-back's own kind decides it.
        // Two pool threads meet at a barrier each round: raise the floor so neighbours do not starve (L-065).
        MessagePickupStoreSupport.MessagePickupStoreKit.RaiseThreadPoolFloor(2);
        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        const string Grant = "did:example:alice";
        for (var round = 0; round < 200; round++)
        {
            var store = new InMemoryMessageStore();
            var record = new QueuedMessage("m", Grant, Grant, PickupFixture.Payload(40, 1), t0, null, t0.AddDays(1));
            (await store.EnqueueAsync(record, null, t0, None)).Should().Be(EnqueueOutcome.Enqueued);
            var lease = new MessageLease("socket", t0 + LeaseFor) { ForPush = true };
            (await store.LeaseAsync(new MessageLeaseRequest(Grant, null, 1, long.MaxValue, t0, lease), None)).Should().ContainSingle();

            using var start = new Barrier(2);
            var close = Task.Run(async () =>
            {
                start.SignalAndWait();
                await store.ReleaseLeasesAsync("socket", None);
            });
            var own = Task.Run(async () =>
            {
                start.SignalAndWait();
                _ = giveBack switch
                {
                    "release by id" => await store.ReleaseAsync(Grant, new[] { "m" }, lease.Id, None),
                    "return as unsent" => await store.ReturnUnsentAsync(Grant, new[] { "m" }, lease.Id, None),
                    _ => await store.PassOverAsync(Grant, new[] { "m" }, lease.Id, t0, None),
                };
            });
            await Task.WhenAll(close, own).WaitAsync(MessagePickupLive.Deadline);

            var peek = await store.LeaseAsync(new MessageLeaseRequest(Grant, null, 1, long.MaxValue, t0, new MessageLease(null, t0)), None);
            peek.Should().ContainSingle("round {0}: no lease is left on the record", round).Which.PushTaken.Should().Be(marked, "round {0}", round);
            store.MessageCount.Should().Be(1);
        }
    }

    private static string[] PushedIds(PickupFakeSession session)
        => session.Sent.Select(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered!.Single().Id : "?").ToArray();
}
