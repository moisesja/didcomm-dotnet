using System.Collections.Concurrent;
using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Jose;
using DidComm.Mediator;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// ADV-4 (#95, PR #109 review R1-2) over the real mediator endpoints: the revised live push — the
/// forward pump only stores, the push workers pack and push per grant — attacked where the peer
/// controls the latency: the resolution of its own DID's <c>keyAgreement</c> keys, which packing a
/// <c>delivery</c> for it needs. Tests marked <c>Skip</c> are confirmed defects: each fails when the
/// skip is removed.
/// </summary>
public sealed class MediatorLivePushAdv4Tests
{
    // ---- loss and starvation: a burst beyond the per-grant line -----------------------------------

    [Fact]
    public async Task A_burst_while_the_recipients_first_push_is_slow_loses_nothing_and_later_messages_are_pushed_again()
    {
        // L-020 (ADV-4 D1): this test asserted the defect — sixteen pushed, four refused by the
        // 16-job share (Push-Skipped-Saturated) and left for pickup. The share is now the queue's
        // per-grant quota, so all twenty are pushed, in order, and nothing is left to fetch.
        var (mediator, keys) = await StartAsync(honourCancellation: true);
        await using var _ = mediator;
        var bob = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        keys.Pause(bob.Did);

        var notes = new List<Message>();
        for (var i = 0; i < 20; i++)
            notes.Add(await Qa.SendNoteAsync(mediator, alice, a, content: "burst " + i));
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 1, "the first push is packing");
        mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(0, "the grant's share of the backlog is its queue quota");
        keys.Release();

        // All twenty, in arrival order, over the live connection.
        for (var i = 0; i < 20; i++)
        {
            var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
            (await MediatorRecipient.UnpackFromSenderAsync(a, pushed.Payload, alice)).Message.Id.Should().Be(notes[i].Id);
        }
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(20);
        mediator.PushOutcomes.Should().HaveCount(20, "one outcome per handed-over message");
        mediator.Outcomes.Should().HaveCount(20).And.OnlyContain(o => o == ForwardOutcomes.Queued);

        // Nothing is left for pickup: every message is leased to the live session until acknowledged.
        (await mediator.FetchAsync(bob)).Should().BeEmpty();

        // A message that arrives after the line drained is pushed live again.
        var later = await Qa.SendNoteAsync(mediator, alice, a, content: "later");
        var again = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(a, again.Payload, alice)).Message.Id.Should().Be(later.Id);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task Every_message_that_arrives_while_live_delivery_is_on_reaches_the_live_connection()
    {
        // Pickup 3.0, recipient mode 2: fetch the queue, turn live mode on, then rely on live delivery.
        var (mediator, keys) = await StartAsync(honourCancellation: true);
        await using var _ = mediator;
        var bob = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        keys.Pause(bob.Did);

        var notes = new List<Message>();
        for (var i = 0; i < 20; i++)
            notes.Add(await Qa.SendNoteAsync(mediator, alice, a, content: "burst " + i));
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 1, "the first push is packing");
        keys.Release();

        var received = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
            received.Add((await MediatorRecipient.UnpackFromSenderAsync(a, pushed.Payload, alice)).Message.Id);
        }
        received.Should().Equal(notes.Select(n => n.Id));
    }

    [Fact]
    public async Task Review_round_2_an_expired_message_queued_ahead_never_costs_a_later_message_its_live_push()
    {
        // PR #109 review round 2, finding 1, over the real endpoints: MaxQueueMessages = 2; the first
        // live push is held in its key resolution; a second forward expires one second later; the
        // clock moves two seconds and a third forward arrives (the store drops the expired record and
        // accepts the third). On 755c497 the dispatcher still counted the expired record's job and
        // refused the third (Push-Skipped-Saturated): it was never pushed. The push now takes what the
        // store holds (D-1), so the third goes out live once the first push moves.
        var (mediator, keys) = await StartAsync(honourCancellation: true, mediatorOptions: m => m.Pickup.MaxQueueMessages = 2);
        await using var _ = mediator;
        var bob = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        keys.Pause(bob.Did);

        var first = await Qa.SendNoteAsync(mediator, alice, a, content: "first");
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 1, "the first push is packing");
        await Qa.SendNoteAsync(mediator, alice, a, Qa.Expires(mediator.Clock.Now.AddSeconds(1)), content: "expires");
        mediator.Clock.Advance(TimeSpan.FromSeconds(2));
        var third = await Qa.SendNoteAsync(mediator, alice, a, content: "third");
        mediator.Outcomes.Should().Equal(new[] { ForwardOutcomes.Queued, ForwardOutcomes.Queued, ForwardOutcomes.Queued }, "the store dropped the expired record and accepted the third");
        keys.Release();

        var received = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
            received.Add((await MediatorRecipient.UnpackFromSenderAsync(a, pushed.Payload, alice)).Message.Id);
        }
        received.Should().Equal(first.Id, third.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(0);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    [Fact]
    public async Task A_message_that_ages_out_while_the_grants_first_live_push_is_held_is_never_pushed()
    {
        // FR-PICK-10 (QA-2; its form for a push to the recipient's own endpoint left #95, #110): the
        // mailbox age limit is part of the record's expiry, so a message that ages out while the
        // grant's first push is held in its key resolution is never handed to a push; only the push
        // that already took its record completes.
        var (mediator, keys) = await StartAsync(honourCancellation: true, mediatorOptions: m =>
        {
            m.MaxForwardDelay = TimeSpan.Zero;
            m.Pickup.MaxMessageAge = TimeSpan.FromMinutes(1);
        });
        await using var _ = mediator;
        var bob = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        keys.Pause(bob.Did);

        var first = await Qa.SendNoteAsync(mediator, alice, a, content: "first");
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 1, "the first push is packing");
        mediator.Clock.Advance(TimeSpan.FromSeconds(30));
        await Qa.SendNoteAsync(mediator, alice, a, content: "second");
        // The first ages out at +60 s, the second at +90 s: past +90 s both are beyond the limit.
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        keys.Release();

        var pushed = (await mediator.ReceiveOverSocketAsync(socket, bob)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(a, pushed.Payload, alice)).Message.Id.Should().Be(first.Id, "only the push that had already taken its record completes");
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes.Should().Equal(new[] { PushOutcomes.PushedLive }, "the aged-out second message is never handed to a push");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
    }

    // ---- isolation: grants that stall their own resolution vs another live recipient ---------------

    [Fact]
    public async Task With_every_push_worker_held_by_stalled_grants_another_live_recipient_is_pushed_once_their_push_timeout_ends()
    {
        var time = new QaManualTimeProvider();
        var (mediator, keys) = await StartAsync(honourCancellation: true, time, m => m.PushConcurrency = 2);
        await using var _ = mediator;
        var alice = await mediator.NewSenderAsync();
        var (a1, socketA1) = await LiveGrantAsync(mediator);
        var (a2, socketA2) = await LiveGrantAsync(mediator);
        var (b, socketB) = await LiveGrantAsync(mediator);
        keys.Pause(a1.Peer.Did);
        keys.Pause(a2.Peer.Did);

        await Qa.SendNoteAsync(mediator, alice, a1.Recipient, content: "a1");
        await Qa.SendNoteAsync(mediator, alice, a2.Recipient, content: "a2");
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 2, "both workers are packing for the stalled grants");
        var forB = await Qa.SendNoteAsync(mediator, alice, b.Recipient, content: "b");

        time.Advance(new MediatorOptions().PushTimeout);
        var pushed = (await mediator.ReceiveOverSocketAsync(socketB, b.Peer)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b.Recipient, pushed.Payload, alice)).Message.Id.Should().Be(forB.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.Failed).Should().Be(2);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1);
        keys.Release();
        foreach (var socket in new[] { socketA1, socketA2, socketB })
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
            socket.Dispose();
        }
    }

    [Fact]
    public async Task A_live_recipients_push_is_not_held_up_by_other_grants_whose_resolution_stalls()
    {
        // ADV-4 D3, with the bound the design guarantees (the original expectation, "no time
        // passes at all", is stricter than any fixed pool can give the first time): K = 2 grants
        // stall every push and W = 2 workers. The first round holds both workers for one PushTimeout,
        // so B's first push waits that long. Then both stalled grants are paused for the cooldown:
        // their next pushes end at once without a worker, and B's next push goes out with no time
        // passing — a stalled peer holds a worker once per cooldown, not push after push.
        var time = new QaManualTimeProvider();
        var (mediator, keys) = await StartAsync(honourCancellation: true, time, m => m.PushConcurrency = 2);
        await using var _ = mediator;
        var alice = await mediator.NewSenderAsync();
        var (a1, socketA1) = await LiveGrantAsync(mediator);
        var (a2, socketA2) = await LiveGrantAsync(mediator);
        var (b, socketB) = await LiveGrantAsync(mediator);
        keys.Pause(a1.Peer.Did);
        keys.Pause(a2.Peer.Did);

        await Qa.SendNoteAsync(mediator, alice, a1.Recipient, content: "a1");
        await Qa.SendNoteAsync(mediator, alice, a2.Recipient, content: "a2");
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 2, "both workers are packing for the stalled grants");
        var forB = await Qa.SendNoteAsync(mediator, alice, b.Recipient, content: "b");

        // Round one: B waits for a worker, at most one PushTimeout.
        time.Advance(new MediatorOptions().PushTimeout);
        var pushed = (await mediator.ReceiveOverSocketAsync(socketB, b.Peer)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b.Recipient, pushed.Payload, alice)).Message.Id.Should().Be(forB.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.Failed).Should().Be(2);

        // Round two, no time passing: the stalled grants are paused, B's push goes out at once.
        await Qa.SendNoteAsync(mediator, alice, a1.Recipient, content: "a1 again");
        await Qa.SendNoteAsync(mediator, alice, a2.Recipient, content: "a2 again");
        var forB2 = await Qa.SendNoteAsync(mediator, alice, b.Recipient, content: "b again");
        var pushed2 = (await mediator.ReceiveOverSocketAsync(socketB, b.Peer)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b.Recipient, pushed2.Payload, alice)).Message.Id.Should().Be(forB2.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.SkippedCooldown).Should().Be(2);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(2);
        keys.Arrivals.Should().Be(2, "the stalled grants never took a worker again during their cooldown");
        keys.Released.Should().BeFalse();

        // Nothing is lost: the stalled grants' messages wait for pickup. (The pickup answer is packed
        // for the stalled peer, so its resolution has to finish now.)
        keys.Release();
        foreach (var stalled in new[] { a1, a2 })
        {
            IReadOnlyList<DidComm.Mediator.Pickup.DeliveredMessage> fetched = Array.Empty<DidComm.Mediator.Pickup.DeliveredMessage>();
            await MediatorTestServer.WaitUntilAsync(() =>
            {
                fetched = mediator.FetchAsync(stalled.Peer).GetAwaiter().GetResult();
                return fetched.Count == 2;
            }, "both of the stalled grant's messages are fetchable");
        }
        GC.KeepAlive(socketA1);
        GC.KeepAlive(socketA2);
    }

    [Fact]
    public async Task Grants_whose_resolution_stalls_cannot_make_the_push_backlog_refuse_another_live_recipients_push()
    {
        // ADV-4 D1/D3 (global form): 64 grants whose DID resolution stalls each get sixteen forwards
        // (1 024, the old backlog's whole capacity). B's message is still stored and its grant woken
        // (no Push-Skipped-Saturated: there is no per-message backlog any more, PR #109 review round
        // 2; the stalled grants' abandoned pushes settle as soon as they see their deadline). Its push then waits for a worker: with K = 64 stalled grants
        // ahead of it in the turn and W = 4 workers, ceil(K / W) = 16 rounds of PushTimeout.
        const int Stalled = 64;
        const int PerStalled = 16;
        const int Workers = 4;
        Qa.RaiseThreadPoolFloor();
        var time = new QaManualTimeProvider();
        var (mediator, keys) = await StartAsync(honourCancellation: true, time, m => m.PushConcurrency = Workers);
        await using var _ = mediator;
        var alice = await mediator.NewSenderAsync();
        var sockets = new List<WebSocket>();
        var stalled = new List<LiveGrant>();
        for (var i = 0; i < Stalled; i++)
        {
            var (grant, socket) = await LiveGrantAsync(mediator);
            keys.Pause(grant.Peer.Did);
            stalled.Add(grant);
            sockets.Add(socket);
        }
        var (b, socketB) = await LiveGrantAsync(mediator);
        sockets.Add(socketB);

        // Sixteen forwards to each stalled grant: anyone may send a forward.
        foreach (var grant in stalled)
        {
            for (var n = 0; n < PerStalled; n++)
                await Qa.SendNoteAsync(mediator, alice, grant.Recipient, flush: false, content: "filler");
        }
        await mediator.FlushAsync();
        mediator.Pushes.Unsettled.Should().BeLessThanOrEqualTo(mediator.Pushes.MaxUnsettled);

        var forB = await Qa.SendNoteAsync(mediator, alice, b.Recipient, content: "b");
        mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(0, "another grant's stalled pushes must not refuse B's");
        mediator.Outcomes.Should().OnlyContain(o => o == ForwardOutcomes.Queued);

        // Round by round: each round's stalled pushes have started before the clock moves.
        var receive = mediator.ReceiveOverSocketAsync(socketB, b.Peer);
        for (var round = 1; round <= Stalled / Workers && !receive.IsCompleted; round++)
        {
            await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= round * Workers, $"round {round}'s stalled pushes are packing");
            time.Advance(new MediatorOptions().PushTimeout);
        }
        var pushed = (await receive).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b.Recipient, pushed.Payload, alice)).Message.Id.Should().Be(forB.Id);
        keys.Arrivals.Should().BeGreaterThanOrEqualTo(Stalled, "B waited its turn behind each stalled grant once, no longer");
        mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(0);
        keys.Release();
        GC.KeepAlive(sockets);
    }

    // ---- the deadline vs a resolution that ignores cancellation ---------------------------------------

    [Fact]
    public async Task A_live_push_abandoned_at_the_deadline_is_given_back_to_pickup_and_not_written_later_even_when_the_resolution_ignores_cancellation()
    {
        var time = new QaManualTimeProvider();
        var (mediator, keys) = await StartAsync(honourCancellation: false, time, m => m.PushTimeout = TimeSpan.FromSeconds(5));
        await using var _ = mediator;
        var bob = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bob, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, bob);
        keys.Pause(bob.Did);

        var note = await Qa.SendNoteAsync(mediator, alice, a, content: "abandoned");
        await MediatorTestServer.WaitUntilAsync(() => keys.Arrivals >= 1, "the push is packing");
        time.Advance(TimeSpan.FromSeconds(5));
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        mediator.Logs.Entries.Should().Contain(line => line.Contains("stays queued for pickup", StringComparison.Ordinal));

        // The resolution, which ignored the deadline, finishes.
        keys.Release();

        // As the outcome, the log and PushDispatcher's remarks say: the lease is given back and the
        // message waits for pickup.
        IReadOnlyList<DidComm.Mediator.Pickup.DeliveredMessage> fetched = Array.Empty<DidComm.Mediator.Pickup.DeliveredMessage>();
        await MediatorTestServer.WaitUntilAsync(() =>
        {
            fetched = mediator.FetchAsync(bob).GetAwaiter().GetResult();
            return fetched.Count > 0;
        }, "the abandoned push's lease was given back");
        (await MediatorRecipient.UnpackFromSenderAsync(a, fetched.Single().Payload, alice)).Message.Id.Should().Be(note.Id);

        // The lease is given back only after the session's send ended without queuing a frame, so
        // the next frame on the socket answers this request: the abandoned delivery never went out.
        var status = MessagePickup.CreateStatusRequest(bob.Did, mediator.Did);
        (await mediator.AskOverSocketAsync(socket, bob, status)).Status(status.Id).MessageCount.Should().Be(1);
        mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
    }

    // ---- support --------------------------------------------------------------------------------------

    private sealed record LiveGrant(TestPeer Peer, TestPeer Recipient);

    private static async Task<(LiveGrant Grant, WebSocket Socket)> LiveGrantAsync(MediatorTestServer mediator)
    {
        var peer = await mediator.NewPeerAsync();
        var recipient = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(peer, recipient.Did);
        var socket = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socket, peer);
        return (new LiveGrant(peer, recipient), socket);
    }

    private static async Task<(MediatorTestServer Mediator, Adv4Keys Keys)> StartAsync(bool honourCancellation, TimeProvider? time = null, Action<MediatorOptions>? mediatorOptions = null)
    {
        Adv4Keys? keys = null;
        var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = mediatorOptions;
            o.ConfigureServices = services =>
            {
                if (time is not null)
                    services.AddSingleton(time);
                services.AddSingleton<IDidKeyService>(sp => keys = new Adv4Keys(new NetDidKeyService(sp.GetRequiredService<NetDid.Core.IDidResolver>()), honourCancellation));
            };
        });
        // The key service is built with the container's first resolution; make sure it exists.
        _ = mediator.App.Services.GetRequiredService<IDidKeyService>();
        return (mediator, keys ?? throw new InvalidOperationException("The mediator does not resolve keys through the gated service."));
    }

    /// <summary>
    /// The mediator's key service with the <c>keyAgreement</c> resolution of chosen DIDs paused until
    /// <see cref="Release"/> — what packing a <c>delivery</c> for that DID needs — either honouring
    /// cancellation or ignoring it end to end (a network resolver whose controller stalls it).
    /// </summary>
    private sealed class Adv4Keys(NetDidKeyService inner, bool honourCancellation) : IDidKeyService, IDidKeyBindingService
    {
        private readonly ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        /// <summary>Resolutions that reached the pause.</summary>
        public int Arrivals => Volatile.Read(ref _arrivals);

        public bool Released => _release.Task.IsCompleted;

        public void Pause(string did) => _paused[did] = 0;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            if (relationship == VerificationRelationship.KeyAgreement && _paused.ContainsKey(did) && !_release.Task.IsCompleted)
            {
                Interlocked.Increment(ref _arrivals);
                if (honourCancellation)
                {
                    await _release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await _release.Task.ConfigureAwait(false);
                    return await inner.GetVerificationMethodsAsync(did, relationship, CancellationToken.None).ConfigureAwait(false);
                }
            }
            return await inner.GetVerificationMethodsAsync(did, relationship, ct).ConfigureAwait(false);
        }

        public Task<bool> IsKeyAuthorizedAsync(string did, string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.IsKeyAuthorizedAsync(did, kid, relationship, ct);

        public void RejectUnsupportedMethod(string did) => inner.RejectUnsupportedMethod(did);

        public Task<ResolvedKeyBinding?> ResolveKeyBindingAsync(string kid, VerificationRelationship relationship, CancellationToken ct = default)
            => inner.ResolveKeyBindingAsync(kid, relationship, ct);
    }
}
