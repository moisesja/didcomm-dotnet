using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Samples.Shared;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// What the pushes take, by arrival order (#95, PR #109 review round 8): the store's arrival sequence
/// (<see cref="QueuedMessage.Sequence"/>) decides which messages a live session is pushed — those
/// after the high-water mark of the status that answered its turning live delivery on — never two
/// clock readings; and a record a store hands out without the sequence it was asked for is refused.
/// </summary>
public sealed class PushArrivalOrderTests
{
    private const string Peer = "did:example:arrival-peer";
    private const string Recipient = "did:example:arrival-recipient";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

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

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();
}
