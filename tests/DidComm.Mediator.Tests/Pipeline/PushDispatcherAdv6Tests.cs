using System.Collections.Concurrent;
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
using DidComm.Transports;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// ADV-6 (#95, PR #109 review round 2, after ADV-5): a break-it pass on the arrival-sequence fix —
/// the live-on high-water mark (what a live session is pushed) and the per-grant direct-push cursor
/// (one direct attempt per message). Every test drives the real dispatcher in a container built by
/// <c>AddMediator()</c>, with a manual clock for the deadlines and a rendezvous for each interleaving
/// (L-044). Tests marked <c>Skip</c> are confirmed defects: each fails when the skip is removed.
/// </summary>
public sealed class PushDispatcherAdv6Tests
{
    private const string Peer = "did:example:adv6-peer";
    private const string Recipient = "did:example:adv6-recipient";
    private static readonly Uri Inbox = new("https://adv6-inbox.example/inbox");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
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
        await using var created = PushFixture.Create(configure: o => o.DirectPush = false, services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
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
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
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
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
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
            await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
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
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
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
        await using var fixture = PushFixture.Create(configure: o => o.DirectPush = false, secrets: peers.Secrets, mediatorDid: mediator.Did);
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

    // ---- the direct-push cursor: one attempt per message --------------------------------------------

    [Theory]
    [InlineData("202")]
    [InlineData("400")]
    [InlineData("503")]
    [InlineData("307")]
    [InlineData("throws")]
    public async Task Each_message_gets_exactly_one_direct_attempt_whatever_its_outcome(string how)
    {
        await using var fixture = PushFixture.Create(
            (request, _) => how == "throws"
                ? Task.FromException<TransportResult>(PushFixture.TransportFailure(request, 502))
                : Task.FromResult(new TransportResult(true, int.Parse(how, System.Globalization.CultureInfo.InvariantCulture))),
            directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        for (byte i = 1; i <= 3; i++)
            await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i));

        for (var wake = 0; wake < 8; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(new[] { PushFixture.Payload(64, 1)[0], PushFixture.Payload(64, 2)[0], PushFixture.Payload(64, 3)[0] }, "one request per message, in arrival order, never again ({0})", how);
        fixture.Store.MessageCount.Should().Be(how == "202" ? 0 : 3);
        fixture.Dispatcher.Unsettled.Should().Be(0);
    }

    [Fact]
    public async Task A_take_that_comes_back_after_the_deadline_sends_nothing_keeps_the_records_attempt_and_it_is_sent_once_after_the_cooldown()
    {
        var store = new ScriptedMessageStore();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takes = 0;
        store.OnLease = async (request, ct) =>
        {
            if (Interlocked.Increment(ref takes) == 1)
            {
                parked.TrySetResult();
                await resume.Task; // ignores the deadline
            }
            return await store.Inner.LeaseAsync(request, CancellationToken.None);
        };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await parked.Task.WaitAsync(Wait);
        fixture.Time.Advance(PushTimeout);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        resume.SetResult();
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the abandoned take settled");

        fixture.Router.Requests.Should().BeEmpty("the take came back after the deadline");
        fixture.Dispatcher.DirectCursorOf(Peer).Should().BeNull("nothing was sent, so the record keeps its attempt");
        (await PickupIdsAsync(store.Inner, Peer, fixture.Time.GetUtcNow())).Should().HaveCount(1, "the late take gave its lease back at once");
        await store.Inner.ReleaseLeasesAsync("adv6-audit", CancellationToken.None);

        fixture.Time.Advance(PushTimeout * PushDispatcher.CooldownPushTimeouts);
        for (var wake = 0; wake < 3; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Router.Requests.Should().ContainSingle("sent once, after the cooldown");
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Pushed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wss://adv6-inbox.example/socket")]
    public async Task Without_a_usable_endpoint_every_wake_up_costs_one_take_and_no_request_and_the_record_keeps_its_attempt_until_there_is_one(string? unusable)
    {
        Uri? endpoint = unusable is null ? null : new Uri(unusable);
        await using var fixture = PushFixture.Create(directory: _ => Volatile.Read(ref endpoint));
        await fixture.EnrolAsync(Peer, Recipient);
        for (byte i = 1; i <= 3; i++)
            await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i));

        for (var wake = 0; wake < 10; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        fixture.Router.Requests.Should().BeEmpty();
        fixture.Directory!.Calls.Should().Be(10, "one record taken and one directory question per wake-up: no loop within a wake-up");
        fixture.Dispatcher.DirectCursorOf(Peer).Should().BeNull();

        Volatile.Write(ref endpoint, Inbox);
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Should().HaveCount(3, "each record still had its one attempt");
        fixture.Store.MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_record_whose_recipient_was_unregistered_is_removed_without_an_attempt_and_the_next_one_is_pushed()
    {
        const string Other = "did:example:adv6-other-recipient";
        await using var fixture = PushFixture.Create((_, _) => Task.FromResult(new TransportResult(true, 503)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient, Other);
        await fixture.StoreAsync(Other, PushFixture.Payload(64, 1));
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        (await fixture.Mediation.UpdateRecipientsAsync(Peer, new[] { new RecipientUpdate(Other, RecipientUpdateAction.Remove) }, CancellationToken.None))
            .Should().Equal(RecipientStoreOutcome.Removed);

        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.SkippedUnregistered, PushOutcomes.Failed);
        fixture.Router.Requests.Should().ContainSingle().Which.Payload.ToArray()[0].Should().Be(PushFixture.Payload(64, 2)[0]);
        fixture.Store.MessageCount.Should().Be(1, "the unregistered one was removed; the refused one waits for pickup");
    }

    // Pinned, not a defect: the cursor's documented cost ("a message that was not yet due when a later
    // one was attempted waits for pickup") also covers a message that was leased (a pickup in flight,
    // or a live session) or passed over at that moment — the remarks name only the not-yet-due case.
    // Same outcome as 755c497, whose job for such a record ended Push-Skipped-NotHeld.
    [Fact]
    public async Task A_record_a_pickup_held_while_a_later_one_was_attempted_is_behind_the_cursor_and_waits_for_pickup()
    {
        await using var fixture = PushFixture.Create(directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient);
        var held = await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 1));
        var now = fixture.Time.GetUtcNow();
        var pickup = new MessageLease("a-pickup", now + TimeSpan.FromSeconds(30));
        (await fixture.Store.LeaseAsync(new MessageLeaseRequest(Peer, null, 1, long.MaxValue, now, pickup), CancellationToken.None)).Should().ContainSingle();
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 2));
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        (await fixture.Store.ReleaseAsync(Peer, new[] { held.MessageId }, pickup.Id, CancellationToken.None)).Should().Be(1, "the pickup's response was lost: the record is deliverable again");
        await fixture.StoreAsync(Recipient, PushFixture.Payload(64, 3));
        fixture.Dispatcher.Wake(Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(PushFixture.Payload(64, 2)[0], PushFixture.Payload(64, 3)[0]);
        (await PickupIdsAsync(fixture.Store, Peer, fixture.Time.GetUtcNow())).Should().Equal(held.MessageId);
    }

    [Fact]
    public async Task The_cursor_follows_arrival_order_across_the_recipients_of_one_grant()
    {
        const string Second = "did:example:adv6-second-recipient";
        await using var fixture = PushFixture.Create((_, _) => Task.FromResult(new TransportResult(true, 503)), directory: _ => Inbox);
        await fixture.EnrolAsync(Peer, Recipient, Second);
        var order = new byte[] { 1, 2, 3, 4, 5, 6 };
        foreach (var seed in order)
            await fixture.StoreAsync(seed % 2 == 0 ? Second : Recipient, PushFixture.Payload(64, seed));

        for (var wake = 0; wake < 10; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Router.Requests.Select(r => r.Payload.ToArray()[0]).Should().Equal(order.Select(seed => PushFixture.Payload(64, seed)[0]), "one attempt each, in the grant's arrival order whatever the recipient DID");
    }

    // ADV-6 DEFECT 1 (P2). The cursors live in an LRU cache bounded by
    // CoordinateMediationOptions.MaxGrants (the in-memory mediation store's bound), but a host mediation
    // store is not bounded by it. With more direct-push grants than cursors, a grant's cursor is
    // dropped between two of its wake-ups (LRU thrash): every wake-up then takes the grant's oldest
    // record again — the refused one — sends it at full size, and ends the operation at its failure,
    // so the grant's later messages are never pushed. ADV-5 defects 3 and 4, back for every grant
    // beyond MaxGrants. Measured with the skip removed: 15 POSTs of the refused 32 KiB messages (3
    // expected), 0 of the 12 accepted ones pushed, 491 520 bytes sent for 99 072 forwarded — growing
    // by one full-size POST per wake-up. The same test through the sink passes on 755c497 (a job per
    // stored message, taken by id: one attempt per message). `main` has no direct push.
    [Fact(Skip = "ADV-6 DEFECT 1: past MaxGrants direct-push grants the LRU cursors thrash; a refused message is re-sent at every wake-up and blocks its grant's later messages (ADV-5 D3/D4 return)")]
    public async Task Defect1_with_more_direct_push_grants_than_cursors_a_refused_message_is_never_sent_again_and_never_blocks_its_grants_later_messages()
    {
        const int Large = 32 * 1024;
        await using var fixture = PushFixture.Create(
            (request, _) => Task.FromResult(new TransportResult(true, request.Payload.Length == Large ? 413 : 202)),
            directory: peer => new Uri($"https://{peer["did:example:adv6-".Length..]}.adv6.example/inbox"),
            configure: o => o.CoordinateMediation.MaxGrants = 2,
            // A host mediation store (a database, say): MaxGrants does not bound it.
            services: s => s.AddSingleton<IMediationStore>(new ScriptedMediationStore()));
        var labels = new[] { "a", "b", "c" };
        foreach (var label in labels)
            await fixture.EnrolAsync($"did:example:adv6-{label}", $"did:example:adv6-{label}-r");
        long forwarded = 0;
        var forwardIds = 0;

        // Through the real sink (it stores and wakes the grant), as on 755c497, where this test passes.
        async Task ForwardAndServeAsync(string label, int size, byte seed)
        {
            await ForwardAsync(fixture, PushFixture.MediatorDid, $"did:example:adv6-{label}-r", PushFixture.Payload(size, seed), $"fwd-adv6-thrash-{forwardIds++}");
            forwarded += size;
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        // One large message per grant that its endpoint refuses (any sender can make one), then tiny
        // ones the endpoints accept, the grants taking turns.
        foreach (var label in labels)
            await ForwardAndServeAsync(label, Large, 1);
        for (var round = 0; round < 4; round++)
        {
            foreach (var label in labels)
                await ForwardAndServeAsync(label, 64, (byte)(10 + round));
        }

        var sent = fixture.Router.Requests.Sum(r => (long)r.Payload.Length);
        fixture.Router.Requests.Count(r => r.Payload.Length == Large).Should().Be(labels.Length, "each refused message gets one direct attempt (MediatorOptions.DirectPush remarks)");
        fixture.Pushes(PushOutcomes.Pushed).Should().Be(4 * labels.Length, "a refused message never blocks its grant's later messages");
        sent.Should().BeLessThanOrEqualTo(forwarded, "a forward never costs the mediator more egress than what was forwarded");
    }

    [Fact]
    public async Task The_cursors_stay_within_MaxGrants_however_many_grants_push_and_a_cursor_holds_only_its_grants_DID_and_a_number()
    {
        await using var fixture = PushFixture.Create(
            (_, _) => Task.FromResult(new TransportResult(true, 503)),
            directory: _ => Inbox,
            configure: o => o.CoordinateMediation.MaxGrants = 3,
            services: s => s.AddSingleton<IMediationStore>(new ScriptedMediationStore()));
        for (var i = 0; i < 12; i++)
        {
            var peer = $"did:example:adv6-many-{i}";
            await fixture.EnrolAsync(peer, peer + "-r");
            await fixture.StoreAsync(peer + "-r");
            fixture.Dispatcher.Wake(peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
            fixture.Dispatcher.DirectCursors.Should().BeLessThanOrEqualTo(3);
        }

        fixture.Dispatcher.DirectCursors.Should().Be(3);
        fixture.Dispatcher.GrantEntries.Should().Be(0, "the per-grant entries are pruned; the cursors are the only per-grant memory left");
    }

    // ---- host stores outside the arrival-sequence contract ------------------------------------------

    [Fact]
    public async Task A_host_store_that_numbers_nothing_pushes_nothing_live_or_direct_and_pickup_still_answers()
    {
        await using var peers = new PipelinePeers();
        var mediator = await peers.NewAsync();
        var peer = await peers.NewAsync();
        var store = new ScriptedMessageStore();
        store.OnLease = async (request, ct) => (await store.Inner.LeaseAsync(request, ct)).Select(r => r with { Sequence = 0 }).ToList();
        store.OnGetStatus = async (grant, recipient, now, ct) => (await store.Inner.GetStatusAsync(grant, recipient, now, ct)) with { HighWaterSequence = 0 };
        await using var fixture = PushFixture.Create(directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store), secrets: peers.Secrets, mediatorDid: mediator.Did);
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
        fixture.Router.Requests.Should().BeEmpty();
        fixture.PushOutcomeLog.Should().OnlyContain(o => o == PushOutcomes.Failed, "a record without an arrival number is refused, never pushed");
        (await fixture.Queue.Coordinator.StatusAsync(peer.Did, null, session, "adv6-status", CancellationToken.None))!
            .Queue.MessageCount.Should().Be(1, "a status-request still answers");
        (await PickupIdsAsync(store.Inner, peer.Did, fixture.Time.GetUtcNow())).Should().Equal(new[] { stored.MessageId }, "pickup still hands it out");
    }

    [Fact]
    public async Task A_host_store_that_ignores_AfterSequence_makes_each_later_direct_take_a_refusal_never_a_second_request()
    {
        var store = new ScriptedMessageStore();
        store.OnLease = (request, ct) => store.Inner.LeaseAsync(request with { AfterSequence = null }, ct);
        await using var fixture = PushFixture.Create((_, _) => Task.FromResult(new TransportResult(true, 503)), directory: _ => Inbox, services: s => s.AddSingleton<IMessageStore>(store));
        await fixture.EnrolAsync(Peer, Recipient);
        for (byte i = 1; i <= 3; i++)
            await fixture.StoreAsync(Recipient, PushFixture.Payload(64, i));

        for (var wake = 0; wake < 5; wake++)
        {
            fixture.Dispatcher.Wake(Peer).Should().BeTrue();
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }

        fixture.Router.Requests.Should().ContainSingle("the oldest is attempted once; the store then hands it out again and the take is refused (fail closed, no amplification)");
        fixture.PushOutcomeLog.Should().Equal(Enumerable.Repeat(PushOutcomes.Failed, 5));
        (await PickupIdsAsync(store.Inner, Peer, fixture.Time.GetUtcNow())).Should().HaveCount(3, "every refused take gave its lease back");
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
