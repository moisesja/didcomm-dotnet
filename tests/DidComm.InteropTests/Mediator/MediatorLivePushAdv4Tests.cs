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

    [Fact(Skip = "ADV-4 DEFECT 3: PushConcurrency grants whose DID resolution stalls hold every push worker; every other live recipient's push waits a full PushTimeout per round (default 4 workers, 30 s), and grants are free under the default policy")]
    public async Task A_live_recipients_push_is_not_held_up_by_other_grants_whose_resolution_stalls()
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

        // No time passes: B's push must not wait for the stalled grants' deadline.
        var pushed = (await mediator.ReceiveOverSocketAsync(socketB, b.Peer)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b.Recipient, pushed.Payload, alice)).Message.Id.Should().Be(forB.Id);
        keys.Released.Should().BeFalse();
        GC.KeepAlive(socketA1);
        GC.KeepAlive(socketA2);
    }

    [Fact]
    public async Task Grants_whose_resolution_stalls_cannot_make_the_push_backlog_refuse_another_live_recipients_push()
    {
        // ADV-4 D1/D3 (global form), rewritten for the bounds sized from the queue: 64 grants whose
        // DID resolution stalls each get sixteen forwards — 1 024 jobs, the old backlog's whole
        // capacity. B's message is still stored and handed over (no Push-Skipped-Saturated: the
        // backlog is as large as the store, so only the store's own quota can refuse a message, as
        // a Dropped-Quota forward). Its push then waits for a worker: with K = 64 stalled grants
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
        mediator.Pushes.Outstanding.Should().Be(Stalled * PerStalled);

        var forB = await Qa.SendNoteAsync(mediator, alice, b.Recipient, content: "b");
        mediator.PushOutcome(PushOutcomes.SkippedSaturated).Should().Be(0, "another grant's stalled pushes must not refuse B's");
        mediator.Outcomes.Should().OnlyContain(o => o == ForwardOutcomes.Queued);
        mediator.Pushes.Outstanding.Should().Be(Stalled * PerStalled + 1);

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
