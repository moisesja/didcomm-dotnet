using AwesomeAssertions;
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
/// <see cref="PushDispatcher"/>'s invariants (PR #109 review round 2): D-1 the store decides what is
/// pushed; D-2 wake-ups coalesce and none is lost; D-3 one operation per grant until its work has
/// ended, across session replacement; D-5 every push ends its lease exactly once. (D-4's bounds are
/// in <c>DirectPushQaTests</c> and <c>DirectPushDispatcherTests</c>.)
/// </summary>
public sealed class PushDispatcherInvariantTests
{
    private const string Peer = "did:example:inv-peer";
    private const string Recipient = "did:example:inv-recipient";
    private static readonly Uri Inbox = new("https://inv-host.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;

    [Fact]
    public async Task D1_records_that_expired_were_acknowledged_or_picked_up_are_never_pushed_and_never_hold_up_a_due_one()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox, didComm: o => o.ExpiresClockSkew = TimeSpan.Zero);
        await fixture.EnrolAsync(Peer, Recipient);
        var now = fixture.Time.GetUtcNow();
        await fixture.StoreAsync(Recipient, PushFixture.Payload(16, seed: 1), new PickupDeliveryOptions(ExpiresTime: now.ToUnixTimeSeconds() + 1, AllowLive: false));
        var acknowledged = await fixture.StoreAsync(Recipient, PushFixture.Payload(16, seed: 2));
        var pickedUp = await fixture.StoreAsync(Recipient, PushFixture.Payload(16, seed: 3));
        var due = PushFixture.Payload(16, seed: 4);
        await fixture.StoreAsync(Recipient, due);
        fixture.Time.Advance(TimeSpan.FromSeconds(2)); // the first one expires
        (await fixture.Queue.RemoveAsync(Peer, acknowledged.MessageId)).Should().BeTrue();
        var at = fixture.Time.GetUtcNow();
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 1, int.MaxValue, at, new MessageLease("a-pickup", at + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Should().ContainSingle().Which.Id.Should().Be(pickedUp.MessageId);

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().ContainSingle().Which.Payload.ToArray().Should().Equal(due);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Pushed);
    }

    [Fact]
    public async Task D2_wake_ups_of_a_grant_waiting_for_a_worker_coalesce_into_one_operation()
    {
        var gate = new PushGate();
        await using var fixture = PushFixture.Create(
            async (_, ct) => { await gate.PassAsync(ct); return new TransportResult(true, 202); },
            directory: peer => peer == "did:example:inv-busy" ? Inbox : null,
            configure: o => o.PushConcurrency = 1);
        await fixture.EnrolAsync("did:example:inv-busy", "did:example:inv-busy-r");
        await fixture.EnrolAsync(Peer, Recipient);
        fixture.Dispatcher.Wake((await fixture.StoreAsync("did:example:inv-busy-r")).GrantPeerDid).Should().BeTrue();
        await gate.WaitForArrivalsAsync(1, Wait); // the only worker is parked

        for (var i = 0; i < 5; i++)
            fixture.Dispatcher.Wake((await fixture.StoreAsync(Recipient)).GrantPeerDid).Should().BeTrue();
        gate.Open();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        // Recipient's grant has no endpoint: each operation counts that once.
        fixture.Pushes(PushOutcomes.SkippedNoEndpoint).Should().Be(1, "five wake-ups, one operation");
        fixture.Store.MessageCount.Should().Be(5);
    }

    [Fact]
    public async Task D2_a_wake_up_that_arrives_after_the_operation_last_read_the_store_is_served_right_after_it()
    {
        // The flag is consumed before the store is read, so a wake-up that lands between the
        // operation's last (empty) read and its end is not lost: the grant is served again.
        var store = new ScriptedMessageStore();
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(16, seed: 1));
        var reads = 0;
        var emptyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnLease = (request, _) => Interlocked.Increment(ref reads) == 2 ? new ValueTask<IReadOnlyList<QueuedMessage>>(EmptyAfterAsync()) : store.Inner.LeaseAsync(request, CancellationToken.None);

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await emptyRead.Task.WaitAsync(Wait);
        var late = PushFixture.Payload(16, seed: 2);
        await fixture.StoreAsync(Recipient, late); // stored and woken while the operation is finishing
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        proceed.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Pushes(PushOutcomes.Pushed) == 2, "the late message is pushed by the next operation");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Select(r => r.Payload.ToArray()).Last().Should().Equal(late);
        store.Inner.MessageCount.Should().Be(0);

        async Task<IReadOnlyList<QueuedMessage>> EmptyAfterAsync()
        {
            emptyRead.TrySetResult();
            await proceed.Task;
            return Array.Empty<QueuedMessage>();
        }
    }

    [Fact]
    public async Task D3_a_grant_gets_no_second_operation_while_its_abandoned_push_runs_even_on_a_new_session()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did, directory: _ => Inbox, configure: o => o.DirectPush = false);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var first = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReturnRouteSendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.OnPush = (_, _) =>
        {
            entered.TrySetResult();
            return release.Task; // ignores its token
        };
        await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await entered.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        first.Close();
        var second = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(second);
        fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);
        var next = await fixture.StoreAsync(Recipient);
        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue("held until the abandoned push ends");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Dispatcher.Unsettled.Should().Be(1);
        Deliveries(second).Should().BeEmpty("no second operation while the first one's send still runs");

        release.SetResult(ReturnRouteSendResult.NotSent);
        await MessagePickupLive.UntilAsync(() => Deliveries(second).Count == 1, "the held wake-up is served once the abandoned push ends");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.Dispatcher.Unsettled.Should().Be(0);
        Deliveries(second).Single().Id.Should().Be(next.MessageId, "what was stored after the new session went live; the first message is that session's to pick up");
    }

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();

    public static TheoryData<string, int, int> Settlements => new()
    {
        { "direct 2xx", 1, 0 },
        { "direct 500", 0, 1 },
        { "live sent", 0, 0 },
        { "live not sent", 0, 1 },
    };

    [Theory]
    [MemberData(nameof(Settlements))]
    public async Task D5_every_push_ends_the_lease_it_took_exactly_once(string how, int acknowledgements, int releases)
    {
        var acks = 0;
        var gives = 0;
        var store = new ScriptedMessageStore();
        store.OnAcknowledge = (grant, ids, ct) =>
        {
            Interlocked.Increment(ref acks);
            return store.Inner.AcknowledgeAsync(grant, ids, ct);
        };
        store.OnRelease = (grant, ids, lease, ct) =>
        {
            Interlocked.Increment(ref gives);
            return store.Inner.ReleaseAsync(grant, ids, lease, ct);
        };
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var live = how.StartsWith("live", StringComparison.Ordinal);
        await using var fixture = PushFixture.Create(
            (_, _) => Task.FromResult(new TransportResult(true, how == "direct 2xx" ? 202 : 500)),
            directory: _ => Inbox,
            configure: o => o.DirectPush = !live,
            services: s => s.AddSingleton<IMessageStore>(store),
            secrets: peers.Secrets,
            mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        if (live)
        {
            var session = new LiveTestSession(peer.Did, mediator.Did);
            await fixture.GoLiveAsync(session);
            session.OnPush = (_, _) => Task.FromResult(how == "live sent" ? ReturnRouteSendResult.Sent : ReturnRouteSendResult.NotSent);
        }
        await fixture.StoreAsync(Recipient);

        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        Volatile.Read(ref acks).Should().Be(acknowledgements, how);
        Volatile.Read(ref gives).Should().Be(releases, how);
        fixture.PushOutcomeLog.Should().ContainSingle(how);
    }
}
