using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Threading;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-6 (#95, PR #109 review round 2, after ADV-5): a break-it pass on the arrival-sequence fix —
/// the live-on high-water mark (what a live session is pushed), the registration check at the take,
/// and host stores outside the arrival-sequence contract. Every test drives the real dispatcher in a
/// container built by <c>AddMediator()</c>, with a manual clock for the deadlines and a rendezvous
/// for each interleaving (L-044). Tests marked <c>Skip</c> are confirmed defects: each fails when
/// the skip is removed.
/// </summary>
public sealed class PushDispatcherAdv6Tests
{
    private const string Recipient = "did:example:adv6-recipient";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseTimeout = new MessagePickupOptions().LeaseTimeout;

    // ---- the live-on mark ----------------------------------------------------------------------------

    [Fact]
    public async Task A_message_stored_inside_the_live_on_change_before_its_status_read_is_counted_and_never_pushed_and_one_stored_right_after_is_pushed_once()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var store = new ScriptedMessageStore();
        PushFixture? fixture = null;
        StoredMessage? before = null, after = null;
        QueueStatus? answered = null;
        var armed = 0;
        store.OnGetStatus = async (grant, recipient, now, ct) =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return await store.Inner.GetStatusAsync(grant, recipient, now, ct);
            // Inside the live-delivery-change, just before its status read: this one is counted.
            before = await fixture!.StoreAsync(Recipient, PushFixture.Payload(64, 1));
            var status = await store.Inner.GetStatusAsync(grant, recipient, now, ct);
            answered = status;
            // Just after it, before the session is live: its own wake-up finds no live session.
            after = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
            fixture.Dispatcher.Wake(grant).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            return status;
        };
        await using var created = PushFixture.Create(services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
        fixture = created;
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);

        Volatile.Write(ref armed, 1);
        await fixture.GoLiveAsync(session);
        for (var i = 0; i < 3; i++)
        {
            fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        answered!.MessageCount.Should().Be(1, "the status that answered the change counted the message stored before its read");
        Deliveries(session).Select(d => d.Id).Should().Equal(new[] { after!.MessageId }, "counted → picked up; arrived after the status → pushed, once, whatever wakes follow");
        (await PickupIdsAsync(store.Inner, peer.Did, fixture.Time.GetUtcNow())).Should().Equal(new[] { before!.MessageId }, "the counted one is the recipient's to pick up");
    }

    [Fact]
    public async Task Turning_live_delivery_on_again_while_it_is_on_keeps_the_first_mark_so_a_message_that_arrived_in_between_is_still_pushed_once()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);

        // Stored after live delivery went on; its wake-up is lost (a saturation or a cooldown skip).
        var between = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        // The same session turns live delivery on again: the mark stays, and the change wakes the grant.
        await ChangeLiveAsync(fixture, session, liveDelivery: true);
        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        Deliveries(session).Select(d => d.Id).Should().Equal(between.MessageId);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.PushedLive);
    }

    [Fact]
    public async Task Turning_live_delivery_off_and_on_again_takes_the_new_statuss_mark()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        await ChangeLiveAsync(fixture, session, liveDelivery: false);

        var whileOff = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        await ChangeLiveAsync(fixture, session, liveDelivery: true);
        var afterOn = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        Deliveries(session).Select(d => d.Id).Should().Equal(new[] { afterOn.MessageId }, "what was stored while live delivery was off was counted by the status that turned it on again");
        (await PickupIdsAsync(fixture.Store, peer.Did, fixture.Time.GetUtcNow())).Should().Equal(whileOff.MessageId);
    }

    // L-044: no assertion depends on which racer won; the invariant holds per message for every
    // interleaving. Half the forwards are stored before the status read (rendezvous), the rest race
    // the read, the session turning live and the live-on wake-up, through the real sink.
    [Fact]
    public async Task Forwards_racing_the_live_on_change_are_each_either_counted_in_its_status_or_pushed_live_exactly_once()
    {
        const int Forwards = 24;
        MessagePickupLive.RaiseThreadPoolFloor(Forwards);
        for (var round = 0; round < 4; round++)
        {
            await using var peers = new PipelinePeers();
            var mediator = await peers.NewAsync();
            var peer = await peers.NewAsync();
            var store = new ScriptedMessageStore();
            var half = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var storedCount = 0;
            QueueStatus? answered = null;
            store.OnGetStatus = async (grant, recipient, now, ct) =>
            {
                if (answered is null)
                    await half.Task.WaitAsync(Wait, ct);
                var status = await store.Inner.GetStatusAsync(grant, recipient, now, ct);
                answered ??= status;
                return status;
            };
            await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
            await fixture.EnrolAsync(peer.Did, Recipient);
            var session = new LiveTestSession(peer.Did, mediator.Did);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwards = Enumerable.Range(0, Forwards).Select(i => Task.Run(async () =>
            {
                await start.Task;
                await ForwardAsync(fixture, mediator.Did, Recipient, PushFixture.Payload(64, (byte)i), $"fwd-adv6-race-{round}-{i}");
                if (Interlocked.Increment(ref storedCount) == Forwards / 2)
                    half.TrySetResult();
            })).ToArray();
            var goLive = Task.Run(async () =>
            {
                await start.Task;
                await fixture.GoLiveAsync(session);
            });
            start.SetResult();
            await Task.WhenAll(forwards).WaitAsync(Wait);
            await goLive.WaitAsync(Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);

            fixture.ForwardOutcomeLog.Should().HaveCount(Forwards).And.OnlyContain(o => o == ForwardOutcomes.Queued);
            // Every record with its arrival number: past every lease, before any expiry.
            var later = fixture.Time.GetUtcNow() + LeaseTimeout + TimeSpan.FromSeconds(1);
            var all = await store.Inner.LeaseAsync(new MessageLeaseRequest(peer.Did, null, int.MaxValue, long.MaxValue, later, new MessageLease("adv6-audit", later + TimeSpan.FromMinutes(1))), CancellationToken.None);
            all.Should().HaveCount(Forwards);
            var mark = answered!.HighWaterSequence;
            var pushed = Deliveries(session).Select(d => d.Id).ToList();

            pushed.Should().OnlyHaveUniqueItems("no message is pushed live twice");
            pushed.Should().BeEquivalentTo(all.Where(r => r.Sequence > mark).Select(r => r.Id), "every message that arrived after the live-on status is pushed (round {0})", round);
            all.Count(r => r.Sequence <= mark).Should().Be(answered.MessageCount, "the status counted exactly the records at or below its own mark (round {0})", round);
            answered.MessageCount.Should().BeGreaterThanOrEqualTo(Forwards / 2, "half were stored before the status read (the rendezvous)");
        }
    }

    [Fact]
    public async Task Of_two_live_sessions_of_one_peer_each_message_is_pushed_to_one_of_them_once_while_its_lease_holds()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var older = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(older);
        var early = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1)); // after the older's mark, before the newer's
        var newer = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(newer);
        var stored = new List<StoredMessage> { early };
        for (byte i = 2; i <= 4; i++)
            stored.Add(await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i)));

        for (var wake = 0; wake < 4; wake++)
        {
            fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        var toNewer = Deliveries(newer).Select(d => d.Id).ToList();
        var toOlder = Deliveries(older).Select(d => d.Id).ToList();
        toNewer.Concat(toOlder).Should().OnlyHaveUniqueItems("a pushed message stays leased to its session");
        toOlder.Should().BeEmpty("the newest live session is the target");
        toNewer.Should().Equal(stored.Skip(1).Select(s => s.MessageId), "the newer session's status counted the one stored before it went live");
    }

    [Fact]
    public async Task A_session_that_never_takes_a_push_costs_one_send_attempt_per_wake_up_and_leaves_every_message_to_pickup()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        var attempts = 0;
        session.OnPush = (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(ReturnRouteSendResult.NotSent);
        };
        await fixture.GoLiveAsync(session);
        var stored = new List<string>();
        for (byte i = 1; i <= 5; i++)
            stored.Add((await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i))).MessageId);

        for (var wake = 0; wake < 7; wake++)
        {
            fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        Volatile.Read(ref attempts).Should().Be(7, "one live attempt per wake-up, the operation ends there");
        fixture.Dispatcher.Unsettled.Should().Be(0);
        Deliveries(session).Should().BeEmpty();
        (await PickupIdsAsync(fixture.Store, peer.Did, fixture.Time.GetUtcNow())).Should().Equal(stored, "every lease was given back");
    }

    // ---- the registration check at the take ----------------------------------------------------------

    [Fact]
    public async Task A_record_whose_recipient_was_unregistered_is_removed_without_an_attempt_and_the_next_one_is_pushed()
    {
        const string Other = "did:example:adv6-other-recipient";
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        await using var fixture = PushFixture.Create(secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient, Other);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        var attempts = 0;
        session.OnPush = (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(ReturnRouteSendResult.Sent);
        };
        await fixture.StoreAsync(Other, PushFixture.Payload(64, 1));
        var next = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        (await fixture.Mediation.UpdateRecipientsAsync(peer.Did, new[] { new RecipientUpdate(Other, RecipientUpdateAction.Remove) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Removed);

        fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered, PushOutcomes.PushedLive);
        Volatile.Read(ref attempts).Should().Be(1, "the unregistered record is never offered to the session");
        Deliveries(session).Select(d => d.Id).Should().Equal(next.MessageId);
        fixture.Store.MessageCount.Should().Be(1, "the unregistered one was removed; the pushed one is leased to the session");
    }

    // ---- host stores outside the arrival-sequence contract ------------------------------------------

    [Fact]
    public async Task A_host_store_that_numbers_nothing_pushes_nothing_and_pickup_still_answers()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var store = new ScriptedMessageStore();
        store.OnLease = async (request, ct) => (await store.Inner.LeaseAsync(request, ct)).Select(r => r with { Sequence = 0 }).ToList();
        store.OnGetStatus = async (grant, recipient, now, ct) => (await store.Inner.GetStatusAsync(grant, recipient, now, ct)) with { HighWaterSequence = 0 };
        await using var fixture = PushFixture.Create(services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
        await fixture.EnrolAsync(peer.Did, Recipient);
        var session = new LiveTestSession(peer.Did, mediator.Did);
        await fixture.GoLiveAsync(session);
        var stored = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));

        for (var wake = 0; wake < 3; wake++)
        {
            fixture.Dispatcher.Wake(peer.Did).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        Deliveries(session).Should().BeEmpty();
        fixture.PushOutcomeLog.Should().OnlyContain(o => o == PushOutcomes.Failed, "a record without an arrival number is refused, never pushed");
        (await fixture.Queue.Coordinator.StatusAsync(peer.Did, null, session, "adv6-status", CancellationToken.None))!
            .Queue.MessageCount.Should().Be(1, "a status-request still answers");
        (await PickupIdsAsync(store.Inner, peer.Did, fixture.Time.GetUtcNow())).Should().Equal(new[] { stored.MessageId }, "pickup still hands it out");
    }

    // ---- support ---------------------------------------------------------------------------------------

    private static async Task<string[]> PickupIdsAsync(IMessageStore store, string grantPeerDid, DateTimeOffset now)
        => (await store.LeaseAsync(new MessageLeaseRequest(grantPeerDid, null, 100, long.MaxValue, now, new MessageLease("adv6-audit", now + TimeSpan.FromMinutes(1))), CancellationToken.None))
            .Select(r => r.Id).ToArray();

    private static IReadOnlyList<DeliveredMessage> Deliveries(LiveTestSession session)
        => session.Sent.SelectMany(m => MessagePickup.TryParseDelivery(m, out var delivered, out _) ? delivered : Array.Empty<DeliveredMessage>()).ToList();

    /// <summary>A <c>live-delivery-change</c> from the session's peer on an already registered session, through the real protocol dispatcher; returns once its wake-up has been served.</summary>
    private static async Task ChangeLiveAsync(PushFixture fixture, LiveTestSession session, bool liveDelivery)
    {
        var client = fixture.Services.GetRequiredService<DidCommClient>();
        var change = MessagePickup.CreateLiveDeliveryChange(session.PeerDid, session.LocalDid, liveDelivery);
        var packed = await client.PackEncryptedAsync(change, new PackEncryptedOptions(Recipients: new[] { session.LocalDid }, From: session.PeerDid));
        await fixture.Services.GetRequiredService<ProtocolDispatcher>().DispatchAsync(await client.UnpackAsync(packed.Message), client, fixture.Options, Task.CompletedTask, session, CancellationToken.None);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
    }

    /// <summary>What the forward pump does with a relayed forward: hand it to the mediator's sink.</summary>
    private static Task ForwardAsync(PushFixture fixture, string mediatorDid, string recipient, byte[] payload, string forwardId)
        => fixture.Sink.AcceptAsync(new ForwardProcessingResult(recipient, payload, null, Delay: null), Context(fixture, mediatorDid, forwardId), CancellationToken.None);

    private static ProtocolContext Context(PushFixture fixture, string mediatorDid, string forwardId)
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
                RecipientKid: mediatorDid + "#key-1",
                AllRecipientKids: new[] { mediatorDid + "#key-1" },
                FromPrior: null),
            new DidComm.Threading.ThreadState(forwardId),
            Client: null,
            fixture.Options,
            new InMemoryThreadStateStore());
}
