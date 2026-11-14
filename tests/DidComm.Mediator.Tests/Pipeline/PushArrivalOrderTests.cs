using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Samples.Shared;
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// What the pushes take, by arrival order (#95, PR #109 review round 8): the store's arrival sequence
/// (<see cref="QueuedMessage.Sequence"/>) decides which messages a live session is pushed — those
/// after the high-water mark of the status that answered its turning live delivery on — never two
/// clock readings; a record a store hands out without the sequence it was asked for is refused; and
/// each grant's direct push has a cursor on the same sequence, so each message gets one direct
/// attempt, a refused one never blocks or rides along with the grant's later messages, and the
/// cursors are bounded (least recently used dropped first).
/// </summary>
public sealed class PushArrivalOrderTests
{
    private const string Peer = "did:example:arrival-peer";
    private const string Recipient = "did:example:arrival-recipient";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly Uri Inbox = new("https://arrival.example/inbox");

    // ---- live push: the mark of the live-on status ---------------------------------------------------

    [Fact]
    public async Task On_a_frozen_clock_what_was_stored_before_live_delivery_went_on_is_picked_up_and_what_arrived_after_is_pushed()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var before = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        var session = new LiveTestSession(peer.Did, mediator.Did);

        // The clock never moves: every record has the instant live delivery went on.
        await fixture.GoLiveAsync(session);
        var after = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        Deliveries(session).Select(d => d.Id).Should().Equal(new[] { after.MessageId }, "the status that answered live-on counted the first message; the second arrived after it");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
        _ = before;
    }

    [Fact]
    public async Task A_message_stored_after_the_live_on_status_was_read_but_before_the_session_was_live_is_pushed_by_the_live_on_wake_up()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var store = new ScriptedMessageStore();
        PushFixture? fixture = null;
        StoredMessage? inWindow = null;
        var armed = 0;
        store.OnGetStatus = async (grant, recipient, now, ct) =>
        {
            var status = await store.Inner.GetStatusAsync(grant, recipient, now, ct);
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                // A forward lands here: stored after the status was read, and its wake-up finds no
                // live session yet (the change is still being answered).
                inWindow = await fixture!.StoreAsync(Recipient, PushFixture.Payload(64, 2));
                fixture.Dispatcher.Wake(grant).Should().BeTrue();
                await fixture.Dispatcher.WaitForIdleAsync(Wait);
            }
            return status;
        };
        await using var created = PushFixture.Create(
            configure: o => o.DirectPush = false,
            services: s => s.AddSingleton<IMessageStore>(store),
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        fixture = created;
        await fixture.EnrolAsync(peer.Did, Recipient);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        var session = new LiveTestSession(peer.Did, mediator.Did);

        Volatile.Write(ref armed, 1);
        await fixture.GoLiveAsync(session);

        inWindow.Should().NotBeNull();
        fixture.PushOutcomeLog.Should().Equal(new[] { PushOutcomes.SkippedNoLiveSession, PushOutcomes.PushedLive }, "the sink's wake-up came too early; the live-on wake-up pushed it");
        Deliveries(session).Select(d => d.Id).Should().Equal(new[] { inWindow!.MessageId }, "the message the status counted is the recipient's to pick up");
    }

    [Fact]
    public async Task Turning_live_delivery_on_wakes_the_grant_and_with_nothing_after_the_mark_records_no_outcome()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        var session = new LiveTestSession(peer.Did, mediator.Did);
        var lives = 0;
        fixture.Queue.Coordinator.LiveDeliveryStarted += _ => Interlocked.Increment(ref lives);

        await fixture.GoLiveAsync(session);

        Volatile.Read(ref lives).Should().Be(1);
        fixture.PushOutcomeLog.Should().BeEmpty("a wake-up that finds nothing to push is no outcome");
        Deliveries(session).Should().BeEmpty();
    }

    // ---- the contract: what a store hands out --------------------------------------------------------

    [Fact]
    public async Task A_record_without_an_arrival_sequence_is_refused_by_a_push_given_back_and_still_picked_up()
    {
        var store = new ScriptedMessageStore();
        store.OnLease = async (request, ct) => (await store.Inner.LeaseAsync(request, ct)).Select(r => r with { Sequence = 0 }).ToList();
        await using var fixture = PushFixture.Create(directory: _ => new Uri("https://arrival.example/inbox"), services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        var stored = await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().BeEmpty("a store that numbers nothing has no records a push may take");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        var now = fixture.Time.GetUtcNow();
        (await store.Inner.LeaseAsync(new MessageLeaseRequest(stored.GrantPeerDid, null, 10, int.MaxValue, now, new MessageLease("a-pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().ContainSingle("the refused take gave its lease back").Which.Id.Should().Be(stored.MessageId);
    }

    // ---- direct push: one attempt per message ----------------------------------------------------------

    [Fact]
    public async Task A_direct_push_makes_one_attempt_per_message_whatever_its_outcome_and_the_cursor_survives_the_idle_grants_pruning()
    {
        await using var fixture = PushFixture.Create((_, _) => Task.FromResult(new TransportResult(true, 503)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        for (byte i = 1; i <= 3; i++)
            await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i));

        for (var wake = 0; wake < 5; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.Dispatcher.GrantEntries.Should().Be(0, "the idle grant's entry is pruned after every operation");
        }

        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(new[] { PushFixture.Payload(64, 1)[0], PushFixture.Payload(64, 2)[0], PushFixture.Payload(64, 3)[0] }, "one request per message, oldest first, each failure ending its operation");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed);
        fixture.Dispatcher.DirectCursors.Should().Be(1, "the cursor is kept apart from the pruned entry");
        fixture.Dispatcher.DirectCursorOf(Peer).Should().Be(3);
        fixture.Store.MessageCount.Should().Be(3, "the refused ones wait for pickup");
    }

    [Fact]
    public async Task A_direct_push_that_reached_its_deadline_counts_as_its_attempt()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = PushFixture.Create(
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct); // honours the deadline
                }
                return new TransportResult(true, 202);
            },
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var slow = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the timed-out push settled");
        fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Pushed);
        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(PushFixture.Payload(64, 1)[0], PushFixture.Payload(64, 2)[0]);
        (await PickupIdsAsync(fixture, Peer)).Should().Equal(new[] { slow.MessageId }, "the timed-out one waits for pickup");
    }

    [Fact]
    public async Task A_record_taken_but_not_sent_for_want_of_an_endpoint_keeps_its_direct_attempt()
    {
        Uri? endpoint = null;
        await using var fixture = PushFixture.Create(directory: _ => Volatile.Read(ref endpoint));
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.DirectCursorOf(Peer).Should().BeNull("nothing was sent");

        Volatile.Write(ref endpoint, Inbox);
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedNoEndpoint, PushOutcomes.Pushed);
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_message_that_was_not_yet_due_when_a_later_one_was_attempted_is_behind_the_cursor_and_waits_for_pickup()
    {
        // The documented cost of one attempt per message: the cursor is one number per grant.
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var delayed = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1), new PickupDeliveryOptions(NotBefore: fixture.Time.GetUtcNow() + TimeSpan.FromMinutes(1), AllowLive: false));
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 3));
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(PushFixture.Payload(64, 2)[0], PushFixture.Payload(64, 3)[0]);
        (await PickupIdsAsync(fixture, Peer)).Should().Equal(delayed.MessageId);
    }

    [Fact]
    public async Task Past_MaxGrants_cursors_the_least_recently_used_is_dropped_and_its_grant_attempts_its_remaining_records_once_more()
    {
        // A host mediation store holds more grants than the in-memory store's MaxGrants, which bounds the cursors.
        await using var fixture = PushFixture.Create(
            (_, _) => Task.FromResult(new TransportResult(true, 503)),
            directory: peer => new Uri($"https://{peer["did:example:".Length..]}.arrival.example/inbox"),
            configure: o => o.CoordinateMediation.MaxGrants = 2,
            services: s => s.AddSingleton<IMediationStore>(new ScriptedMediationStore()));
        foreach (var label in new[] { "a", "b", "c" })
            await fixture.EnrolAsync($"did:example:{label}", $"did:example:{label}-r");
        fixture.Dispatcher.MaxDirectCursors.Should().Be(2);

        async Task StoreAndServeAsync(string label)
        {
            var stored = await fixture.StoreAsync($"did:example:{label}-r");
            fixture.Dispatcher.Wake(stored.GrantPeerDid).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        await StoreAndServeAsync("a");
        await StoreAndServeAsync("b");
        await StoreAndServeAsync("c"); // the third cursor drops A's, the least recently used
        fixture.Dispatcher.DirectCursors.Should().Be(2);
        fixture.Dispatcher.DirectCursorOf("did:example:a").Should().BeNull();

        // A's next wake-up attempts its remaining record once more; then its cursor holds again.
        for (var i = 0; i < 3; i++)
        {
            fixture.Dispatcher.Wake("did:example:a").Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Router.Requests.Count(r => r.Endpoint.Host.StartsWith("a.", StringComparison.Ordinal)).Should().Be(2, "one attempt, and one more after its cursor was dropped");
        fixture.Dispatcher.DirectCursors.Should().Be(2);
        fixture.Dispatcher.DirectCursorOf("did:example:b").Should().BeNull("A's new cursor dropped B's, now the least recently used");
    }

    private static async Task<string[]> PickupIdsAsync(PushFixture fixture, string grantPeerDid)
    {
        var now = fixture.Time.GetUtcNow();
        return (await fixture.Store.LeaseAsync(new MessageLeaseRequest(grantPeerDid, null, 10, int.MaxValue, now, new MessageLease("a-pickup", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Select(r => r.Id).ToArray();
    }

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();
}
