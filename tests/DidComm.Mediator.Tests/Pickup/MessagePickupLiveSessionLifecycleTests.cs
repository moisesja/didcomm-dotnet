using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The per-session state of the pickup role (FR-PICK-06, FR-PICK-04/08; JC-5): the live flag belongs
/// to one session and ends with that session's own <see cref="IReturnRouteSession.Closed"/> signal —
/// not with anything the session registry does — and so do the leases taken over a live session,
/// while a lease taken over a request-scoped session ends by time only. The races are driven by a
/// rendezvous where an interleaving matters, and assert only what must hold for every interleaving
/// (L-044). No test moves the clock to make a lease go away unless it says so.
/// </summary>
public sealed class MessagePickupLiveSessionLifecycleTests
{
    // ---- the flag is the session's, and ends with its Closed signal ------------------------

    [Fact]
    public async Task Closing_a_live_session_without_unregistering_it_turns_live_delivery_off_and_frees_what_it_was_pushed()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue();
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        socket.Close();

        fixture.Sessions.Find(peer).Should().Contain(socket, "the registry was not told: the pickup role must not need it to be");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeFalse("the flag ended with the session");
        var after = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 2));
        after.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        socket.Sent.Should().ContainSingle("nothing is pushed to a closed session");

        // What it was pushed and did not acknowledge is deliverable to another session, clock untouched.
        var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, new[] { pushed.MessageId!, after.MessageId! }, "the closed session's lease is released");
        delivered.Should().BeEquivalentTo(new[] { pushed.MessageId, after.MessageId }, "each exactly once");

        // A new connection of the same peer starts with live delivery off.
        var next = fixture.Session(peer, mediator, live: true);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, next)).LiveDelivery.Should().BeFalse("a new inbound connection starts with live mode disabled (FR-PICK-06)");
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 3))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        next.Attempts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asking_for_live_delivery_on_a_session_that_has_already_closed_is_answered_live_delivery_false_and_nothing_is_pushed_later(bool signalOnly)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        IReturnRouteSession session;
        Func<int> attempts;
        if (signalOnly)
        {
            // Closed has fired, yet the session still says "all" and still accepts sends.
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose).Registered.Should().BeTrue();
            loose.FireClosed();
            (session, attempts) = (loose, () => loose.Attempts.Count);
        }
        else
        {
            var socket = fixture.Session(peer, mediator, live: true);
            socket.Close();
            (session, attempts) = (socket, () => socket.Attempts.Count);
        }

        var status = await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session);

        status.LiveDelivery.Should().BeFalse("live delivery can never be on for a session that has ended");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeFalse();
        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30));
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        attempts().Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);

        // Asking again changes nothing.
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeFalse();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        attempts().Should().Be(0);
    }

    [Fact]
    public async Task The_live_flag_of_one_session_is_not_changed_by_what_another_session_of_the_same_peer_does()
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        var first = fixture.Session(peer, mediator, live: true);
        var second = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, first);

        // The second turns it off for itself (it never had it on), then closes.
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, second, on: false)).LiveDelivery.Should().BeFalse();
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, first)).LiveDelivery.Should().BeTrue();
        second.Close();
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, first)).LiveDelivery.Should().BeTrue();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        first.Sent.Should().ContainSingle();

        // The first closes: a third does not inherit anything.
        first.Close();
        var third = fixture.Session(peer, mediator, live: true);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, third)).LiveDelivery.Should().BeFalse();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        third.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Turning_live_delivery_off_stops_pushes_but_does_not_release_what_was_already_pushed()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 1));

        var off = await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket, on: false);

        off.LiveDelivery.Should().BeFalse();
        off.MessageCount.Should().Be(1);
        var queued = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 2));
        queued.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        socket.Sent.Should().ContainSingle();
        fixture.FakeStore!.CountOf(nameof(IMessageStore.ReleaseLeasesAsync)).Should().Be(0, "the session is still open: its leases stand");
        fixture.FakeStore.CountOf(nameof(IMessageStore.ReleaseAsync)).Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("the pushed message is still leased to the socket").Which.Id.Should().Be(queued.MessageId);

        // On again: pushes resume on the same session.
        (await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30, 3))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);
        socket.Sent.Should().HaveCount(2);

        var released = ExpectOwnerRelease(fixture);
        socket.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(2, "both pushes were this session's");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().Contain(m => m.Id == pushed.MessageId);
    }

    // ---- close racing enable ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_that_closes_while_its_live_delivery_change_is_being_answered_does_not_end_up_live(bool signalOnly)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        IReturnRouteSession session;
        Action close;
        Func<int> attempts;
        if (signalOnly)
        {
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose);
            (session, close, attempts) = (loose, loose.FireClosed, () => loose.Attempts.Count);
        }
        else
        {
            var socket = fixture.Session(peer, mediator, live: true);
            (session, close, attempts) = (socket, socket.Close, () => socket.Attempts.Count);
        }

        // Park the request inside the store read it makes between "the session is known" and "the flag is set".
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnGetStatus = async (grant, recipient, now, ct) =>
        {
            inside.TrySetResult();
            await proceed.Task.WaitAsync(MessagePickupLive.Deadline);
            return await fixture.FakeStore.Inner.GetStatusAsync(grant, recipient, now, ct);
        };
        var enabling = MessagePickupLive.SetLiveAsync(fixture, peer, mediator, session);
        await inside.Task.WaitAsync(MessagePickupLive.Deadline);

        close();
        proceed.SetResult();
        var status = await enabling.WaitAsync(MessagePickupLive.Deadline);

        fixture.FakeStore.OnGetStatus = null;
        status.LiveDelivery.Should().BeFalse("the session ended before the flag was set, so the flag is not set");
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, session)).LiveDelivery.Should().BeFalse();
        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30));
        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        attempts().Should().Be(0);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(result.MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_racing_enable_and_an_arrival_never_leaves_a_closed_session_live_or_holding_a_lease(bool signalOnly)
    {
        // Forty rounds of three racers each, with real envelopes for the requests: the slowest test of
        // the slice (some hundred milliseconds), because one round can only show one interleaving.
        // With signalOnly the closed session still says "all" and still accepts sends, so the only
        // thing that can keep a push away from it is the per-session state under test.
        const int rounds = 40;
        MessagePickupLive.RaiseThreadPoolFloor(3);
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();

        for (var round = 0; round < rounds; round++)
        {
            IReturnRouteSession session;
            Action close;
            Func<int> sent;
            if (signalOnly)
            {
                var loose = new MessagePickupLiveLooseSession(peer, mediator);
                fixture.Sessions.Register(loose);
                (session, close, sent) = (loose, loose.FireClosed, () => loose.Sent.Count);
            }
            else
            {
                var socket = fixture.Session(peer, mediator, live: true);
                (session, close, sent) = (socket, socket.Close, () => socket.Sent.Count);
            }

            var enable = await fixture.ReceiveAsync(MessagePickup.CreateLiveDeliveryChange(peer, mediator, true), mediator);
            using var startLine = new Barrier(3);
            var enabling = Task.Run(async () =>
            {
                startLine.SignalAndWait(MessagePickupLive.Deadline).Should().BeTrue();
                return await fixture.Host.Dispatcher.DispatchAsync(enable, fixture.Host.Client, fixture.Host.Options, Task.CompletedTask, session, CancellationToken.None);
            });
            var closing = Task.Run(() =>
            {
                startLine.SignalAndWait(MessagePickupLive.Deadline).Should().BeTrue();
                close();
            });
            var arriving = Task.Run(async () =>
            {
                startLine.SignalAndWait(MessagePickupLive.Deadline).Should().BeTrue();
                return await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(16, (byte)round));
            });
            await Task.WhenAll(enabling, closing, arriving).WaitAsync(MessagePickupLive.Deadline);

            // Whatever the interleaving: the arrival was retained ...
            var raced = await arriving;
            raced.Outcome.Should().BeOneOf(PickupDeliveryOutcome.PushedLive, PickupDeliveryOutcome.Queued);

            // ... once both have completed the closed session is never live and never a target again ...
            var sentBefore = sent();
            var after = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(16, (byte)round));
            after.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "round {0}: a push must never be PushedLive to a closed session", round);
            sent().Should().Be(sentBefore);

            // ... and no lease it owns remains: both messages reach a fresh session with the clock where it was.
            var expected = new[] { raced.MessageId!, after.MessageId! };
            var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, expected, $"round {round}: no lease owned by the closed session may remain");
            delivered.Should().BeEquivalentTo(expected);
            (await MessagePickupLive.AckAsync(fixture, peer, mediator, expected)).MessageCount.Should().Be(0);
        }

        fixture.Memory.MessageCount.Should().Be(0);
    }

    // ---- close racing an in-flight push ----------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_that_closes_inside_the_push_leaves_the_message_deliverable_without_waiting_for_the_lease(bool signalOnly)
    {
        await using var fixture = PickupFixture.Create();
        var (mediator, peer) = await fixture.EnrolledAsync();
        Func<int> sent;
        if (signalOnly)
        {
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose);
            await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, loose);
            loose.OnSend = (_, _, _) =>
            {
                loose.FireClosed();
                return new ValueTask<ReturnRouteSendResult>(ReturnRouteSendResult.Sent);
            };
            sent = () => loose.Sent.Count;
        }
        else
        {
            var socket = fixture.Session(peer, mediator, live: true);
            await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
            socket.OnSend = (_, _) =>
            {
                socket.Close();
                return ReturnRouteSendResult.Sent;
            };
            sent = () => socket.Sent.Count;
        }

        var result = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30));

        // Which of the two it reports is the race; that the message is retained and free is not.
        result.Outcome.Should().BeOneOf(PickupDeliveryOutcome.PushedLive, PickupDeliveryOutcome.Queued);
        result.MessageId.Should().NotBeNull();
        sent().Should().Be(1);
        fixture.Memory.MessageCount.Should().Be(1);
        var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, new[] { result.MessageId! }, "the message is deliverable shortly after, the clock was not moved");
        delivered.Should().Equal(result.MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_session_that_closes_before_the_store_admitted_the_push_does_not_strand_the_record_under_its_dead_lease(bool signalOnly)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        Action close;
        if (signalOnly)
        {
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose);
            await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, loose);
            close = loose.FireClosed;
        }
        else
        {
            var socket = fixture.Session(peer, mediator, live: true);
            await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
            close = socket.Close;
        }

        // The arrival has chosen its target and is parked just before the store admits the record.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnEnqueue = async (message, lease, now, ct) =>
        {
            parked.TrySetResult();
            await resume.Task.WaitAsync(MessagePickupLive.Deadline);
            return await fixture.FakeStore.Inner.EnqueueAsync(message, lease, now, ct);
        };
        var released = ExpectOwnerRelease(fixture);
        var delivering = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30)).AsTask();
        await parked.Task.WaitAsync(MessagePickupLive.Deadline);

        // The session closes and its release runs to the end — before the lease exists.
        close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(0, "there was nothing to release yet");
        resume.SetResult();
        var result = await delivering.WaitAsync(MessagePickupLive.Deadline);

        result.Outcome.Should().BeOneOf(PickupDeliveryOutcome.PushedLive, PickupDeliveryOutcome.Queued);
        fixture.FakeStore.Enqueued.Should().ContainSingle().Which.Lease.Should().NotBeNull("the record was admitted under the lease of the session that has since closed");
        // The close's own release is over, so nothing but the delivery itself can free the record now.
        var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, new[] { result.MessageId! }, "a record admitted after its session closed must not wait for the lease timeout");
        delivered.Should().Equal(result.MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_session_that_closes_before_the_store_leased_its_requested_batch_does_not_strand_the_batch(bool signalOnly)
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var ids = new[] { await fixture.QueueAsync(peer, PickupFixture.Payload(20, 1)), await fixture.QueueAsync(peer, PickupFixture.Payload(20, 2)) };
        IReturnRouteSession session;
        Action close;
        if (signalOnly)
        {
            var loose = new MessagePickupLiveLooseSession(peer, mediator);
            fixture.Sessions.Register(loose);
            (session, close) = (loose, loose.FireClosed);
        }
        else
        {
            var socket = fixture.Session(peer, mediator, live: true);
            (session, close) = (socket, socket.Close);
        }

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnLease = async (request, ct) =>
        {
            parked.TrySetResult();
            await resume.Task.WaitAsync(MessagePickupLive.Deadline);
            return await fixture.FakeStore.Inner.LeaseAsync(request, ct);
        };
        var released = ExpectOwnerRelease(fixture);
        var requesting = fixture.SendAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 10), mediator, session);
        await parked.Task.WaitAsync(MessagePickupLive.Deadline);

        close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(0, "the batch was not leased yet");
        resume.SetResult();
        await requesting.WaitAsync(MessagePickupLive.Deadline);

        fixture.FakeStore.OnLease = null;
        fixture.FakeStore.LeaseRequests.Should().ContainSingle().Which.Lease.Owner.Should().NotBeNull("a lease taken over a live session carries its owner token");
        var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, ids, "a batch leased to a session that had already closed must not wait for the lease timeout");
        delivered.Should().Equal(ids);
        fixture.Memory.MessageCount.Should().Be(2);
    }

    // ---- whose leases end how --------------------------------------------------------------

    [Fact]
    public async Task A_lease_taken_over_a_live_session_ends_when_it_closes_and_one_taken_over_a_request_scoped_session_only_by_time()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(30), fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var overSocket = await fixture.QueueAsync(peer, PickupFixture.Payload(20, 1));
        var overHttp = await fixture.QueueAsync(peer, PickupFixture.Payload(20, 2));
        // The socket never turns live delivery on: whose lease ends how does not depend on the flag.
        var socket = fixture.Session(peer, mediator, live: true);
        var http = fixture.Session(peer, mediator);

        (await fixture.FetchAsync(peer, mediator, socket, 1))!.Value.Messages.Should().ContainSingle().Which.Id.Should().Be(overSocket);
        (await fixture.FetchAsync(peer, mediator, http, 1))!.Value.Messages.Should().ContainSingle().Which.Id.Should().Be(overHttp);
        var owners = fixture.FakeStore!.LeaseRequests.Select(r => r.Lease.Owner).ToArray();
        owners.Should().HaveCount(2);
        owners[0].Should().NotBeNullOrEmpty("a lease over a live session carries its owner token");
        owners[1].Should().BeNull("a request-scoped session cannot signal its end to anyone: its leases have no owner (JC-5)");
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty("both are leased");

        // The request-scoped session ends: its lease stands.
        http.Close();
        // The live session ends: its lease — and only its lease — is released.
        var released = ExpectOwnerRelease(fixture);
        socket.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        fixture.FakeStore.ReleasedOwners.Should().Equal(owners[0]);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("only the live session's lease ended with its session").Which.Id.Should().Be(overSocket);
        fixture.FakeStore.CountOf(nameof(IMessageStore.ReleaseAsync)).Should().Be(0);

        // Only time ends the other.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(overSocket, overHttp);
        fixture.FakeStore.ReleasedOwners.Should().ContainSingle("nothing but the one close ever released by owner");
    }

    [Fact]
    public async Task A_lease_taken_over_a_live_session_that_stays_open_ends_by_time_like_any_other()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(30));
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(20));
        var socket = fixture.Session(peer, mediator, live: true);
        (await fixture.FetchAsync(peer, mediator, socket, 10))!.Value.Messages.Should().ContainSingle();

        fixture.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));
        (await fixture.FetchAsync(peer, mediator, socket, 10)).Should().BeNull("still leased: the request is answered with status");
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        (await fixture.FetchAsync(peer, mediator, socket, 10))!.Value.Messages.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Fact]
    public async Task Closing_one_live_session_releases_its_own_leases_and_not_those_of_another_session_of_the_same_peer()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var requested = await fixture.QueueAsync(peer, PickupFixture.Payload(20, 1));
        var first = fixture.Session(peer, mediator, live: true);
        var second = fixture.Session(peer, mediator, live: true);
        // The first holds a requested message; the second, the newest and live, holds a pushed one.
        (await fixture.FetchAsync(peer, mediator, first, 10))!.Value.Messages.Should().ContainSingle().Which.Id.Should().Be(requested);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, second);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        var released = ExpectOwnerRelease(fixture);
        first.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("the second session's lease is its own").Which.Id.Should().Be(requested);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, second)).LiveDelivery.Should().BeTrue();

        released = ExpectOwnerRelease(fixture);
        second.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(pushed.MessageId);
        fixture.FakeStore!.ReleasedOwners.Should().HaveCount(2).And.OnlyHaveUniqueItems("each session has its own owner token");
    }

    // ---- a cleanup ends its own lease, not the one that came after it ------------------------

    [Fact]
    public async Task The_cleanup_of_a_refused_push_does_not_end_the_lease_a_later_delivery_request_took_on_the_same_message()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        // The socket is going away: it does not take the push.
        socket.OnSend = (_, _) => ReturnRouteSendResult.NotSent;

        // The arrival is admitted under the socket's lease, the push is refused, and the arrival is
        // parked just before it gives that lease back.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnRelease = async (grant, ids, ct) =>
        {
            parked.TrySetResult();
            await resume.Task.WaitAsync(MessagePickupLive.Deadline);
            return await fixture.FakeStore.Inner.ReleaseAsync(grant, ids, ct);
        };
        var closed = ExpectOwnerRelease(fixture);
        var delivering = fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(30)).AsTask();
        await parked.Task.WaitAsync(MessagePickupLive.Deadline);

        // Meanwhile the socket closes — which releases its leases — and the recipient, on another
        // connection, is delivered the message. That delivery holds a lease of its own.
        socket.Close();
        (await closed.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        var delivered = (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Subject;

        // Now the arrival finishes its cleanup.
        resume.SetResult();
        var result = await delivering.WaitAsync(MessagePickupLive.Deadline);

        result.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        delivered.Id.Should().Be(result.MessageId);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty(
            "the message was delivered and is not acknowledged: it is not delivered again within that delivery's lease (FR-PICK-04). The arrival's cleanup may give back the lease the arrival took, not one taken after it");
    }

    [Fact]
    public async Task The_cleanup_of_a_requested_delivery_the_session_did_not_take_does_not_end_the_lease_a_later_delivery_request_took()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var id = await fixture.QueueAsync(peer, PickupFixture.Payload(30));
        var socket = new MessagePickupLiveLooseSession(peer, mediator);
        fixture.Sessions.Register(socket);

        // A delivery-request over the socket leases the message; the send is slow, and in the end not taken.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.OnSend = async (_, _, _) =>
        {
            parked.TrySetResult();
            await resume.Task.WaitAsync(MessagePickupLive.Deadline);
            return ReturnRouteSendResult.NotSent;
        };
        var closed = ExpectOwnerRelease(fixture);
        var requesting = fixture.SendAsync(MessagePickup.CreateDeliveryRequest(peer, mediator, 10), mediator, socket);
        await parked.Task.WaitAsync(MessagePickupLive.Deadline);

        // While the send is pending the socket closes — its lease is released — and the recipient,
        // on another connection, is delivered the message under a lease of its own.
        socket.FireClosed();
        (await closed.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle().Which.Id.Should().Be(id);

        // The first request now learns its send was not taken and gives back what it leased.
        resume.SetResult();
        await requesting.WaitAsync(MessagePickupLive.Deadline);

        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().BeEmpty(
            "the message is delivered and unacknowledged under the second request's lease (FR-PICK-04); the first request's cleanup may give back its own lease, not one taken after it");
    }

    // ---- a store that cannot release ---------------------------------------------------------

    [Theory]
    [InlineData("throws")]
    [InlineData("answers a faulted task")]
    public async Task A_store_that_cannot_release_the_leases_of_a_closed_session_breaks_nothing_and_they_end_by_time(string how)
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(30), fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));
        var owner = fixture.FakeStore!.Enqueued.Single().Lease!.Owner;
        fixture.FakeStore.OnReleaseLeases = (_, _) => how == "throws"
            ? throw new HandlerBoomException(HandlerBoomException.Marker)
            : new ValueTask<int>(Task.FromException<int>(new HandlerBoomException(HandlerBoomException.Marker)));

        FluentActions.Invoking(socket.Close).Should().NotThrow("the thread that closes a session never sees the store");

        // The failure is logged by type, once, on the thread pool.
        await MessagePickupLive.UntilAsync(() => MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Count > 0, "the failed release is logged");
        MessagePickupLive.QueueLogs(fixture, LogLevel.Warning).Should().ContainSingle().Which.State.Should().Contain("ExceptionType", nameof(HandlerBoomException));
        MessagePickupLive.ShouldNotLeak(fixture.PickupLogs, owner, peer, mediator, pushed.MessageId);
        fixture.FakeStore.ReleasedOwners.Should().Equal(owner);

        // The mediator keeps serving: the flag is off, new messages are queued and delivered.
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeFalse();
        var later = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));
        later.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("the pushed message is still under the lease that could not be released").Which.Id.Should().Be(later.MessageId);

        // ... and that lease ends by its timeout.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(pushed.MessageId, later.MessageId);
    }

    [Fact]
    public async Task The_thread_that_closes_a_session_does_not_wait_for_the_store()
    {
        MessagePickupLive.RaiseThreadPoolFloor(2);
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));

        // A store that is slow the blocking way: the release does not return until the gate opens.
        using var gate = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnReleaseLeases = (owner, ct) =>
        {
            entered.TrySetResult();
            gate.Wait(MessagePickupLive.Deadline).Should().BeTrue();
            return fixture.FakeStore.Inner.ReleaseLeasesAsync(owner, ct);
        };

        // Had the release run on the closing thread, Close would still be inside the store now.
        var closing = Task.Run(socket.Close);
        await entered.Task.WaitAsync(MessagePickupLive.Deadline);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        gate.IsSet.Should().BeFalse("Close returned while the store was still releasing");

        // The role keeps serving meanwhile.
        var later = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));
        later.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeFalse();

        gate.Set();
        var delivered = await MessagePickupLive.FetchUntilDeliveredAsync(fixture, peer, mediator, new[] { pushed.MessageId!, later.MessageId! }, "the release completes once the store lets it");
        delivered.Should().BeEquivalentTo(new[] { pushed.MessageId, later.MessageId });
    }

    [Fact]
    public async Task A_store_that_cannot_release_and_a_logging_provider_that_is_down_together_still_break_nothing()
    {
        await using var fixture = PickupFixture.Create(pickup: o => o.LeaseTimeout = TimeSpan.FromSeconds(30), fakeStore: true, throwingLogger: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20));
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnReleaseLeases = (_, _) =>
        {
            attempted.TrySetResult();
            throw new HandlerBoomException(HandlerBoomException.Marker);
        };

        FluentActions.Invoking(socket.Close).Should().NotThrow();
        await attempted.Task.WaitAsync(MessagePickupLive.Deadline);

        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20))).Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().HaveCount(2).And.Contain(m => m.Id == pushed.MessageId);
    }

    // ---- the registry's bookkeeping is not the session's end --------------------------------

    [Fact]
    public async Task Unregistering_a_live_session_without_closing_it_changes_neither_its_flag_nor_its_leases()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));
        pushed.Outcome.Should().Be(PickupDeliveryOutcome.PushedLive);

        // Wait for the registry's own notification, so that anything listening to it has run.
        var notified = new TaskCompletionSource<SessionCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sessions.SessionClosed += (_, e) =>
        {
            if (ReferenceEquals(e.Session, socket))
                notified.TrySetResult(e.Reason);
        };
        fixture.Sessions.Unregister(socket).Should().BeTrue();
        (await notified.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(SessionCloseReason.Unregistered);

        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue("the flag ends with the session's Closed signal, not with a registry notification");
        fixture.FakeStore!.CountOf(nameof(IMessageStore.ReleaseLeasesAsync)).Should().Be(0, "and so do its leases");
        fixture.FakeStore.CountOf(nameof(IMessageStore.ReleaseAsync)).Should().Be(0);

        // A push finds its session through the registry, so this one is not found — and is found again once registered.
        var unseen = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));
        unseen.Outcome.Should().Be(PickupDeliveryOutcome.Queued);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().ContainSingle("the pushed message is still leased").Which.Id.Should().Be(unseen.MessageId);
        fixture.Sessions.Register(socket).Registered.Should().BeTrue();
        (await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 3))).Outcome.Should().Be(PickupDeliveryOutcome.PushedLive, "the flag never left");
        socket.Sent.Should().HaveCount(2);

        // The session's own end is what ends both.
        var released = ExpectOwnerRelease(fixture);
        socket.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(2);
        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeFalse();
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Should().Contain(m => m.Id == pushed.MessageId);
    }

    [Fact]
    public async Task A_live_session_evicted_from_the_registry_keeps_its_flag_and_its_leases_until_it_closes()
    {
        await using var fixture = PickupFixture.Create(fakeStore: true);
        var (mediator, peer) = await fixture.EnrolledAsync();
        var socket = fixture.Session(peer, mediator, live: true);
        await MessagePickupLive.SetLiveAsync(fixture, peer, mediator, socket);
        var pushed = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 1));

        // The peer opens connection after connection until the registry evicts the oldest: this one.
        var notified = new TaskCompletionSource<SessionCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sessions.SessionClosed += (_, e) =>
        {
            if (ReferenceEquals(e.Session, socket))
                notified.TrySetResult(e.Reason);
        };
        IReturnRouteSession? evicted = null;
        for (var i = 0; i < 64 && evicted is null; i++)
            evicted = fixture.Sessions.Register(new PickupFakeSession(peer, mediator, live: true)).Evicted;
        evicted.Should().BeSameAs(socket);
        (await notified.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(SessionCloseReason.Evicted);

        (await MessagePickupLive.StatusAsync(fixture, peer, mediator, socket)).LiveDelivery.Should().BeTrue("eviction is the registry's bookkeeping; the transport closes the session, and that is the signal");
        fixture.FakeStore!.CountOf(nameof(IMessageStore.ReleaseLeasesAsync)).Should().Be(0);
        var unseen = await fixture.Queue.DeliverAsync(peer, PickupFixture.Payload(20, 2));
        unseen.Outcome.Should().Be(PickupDeliveryOutcome.Queued, "the registry no longer lists it, and none of the newer sessions turned live delivery on");
        socket.Sent.Should().ContainSingle();

        var released = ExpectOwnerRelease(fixture);
        socket.Close();
        (await released.Task.WaitAsync(MessagePickupLive.Deadline)).Should().Be(1);
        (await MessagePickupLive.FetchNowAsync(fixture, peer, mediator)).Select(m => m.Id).Should().Equal(pushed.MessageId, unseen.MessageId);
    }

    /// <summary>
    /// Completes with the number of leases released when the store has finished the next
    /// release-by-owner (the product starts it on the thread pool when a live session closes).
    /// </summary>
    private static TaskCompletionSource<int> ExpectOwnerRelease(PickupFixture fixture)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakeStore!.OnReleaseLeases = async (owner, ct) =>
        {
            var released = await fixture.FakeStore.Inner.ReleaseLeasesAsync(owner, ct);
            done.TrySetResult(released);
            return released;
        };
        return done;
    }
}
