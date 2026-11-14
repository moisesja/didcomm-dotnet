using System.Net.WebSockets;
using AwesomeAssertions;
using DidComm.Jose;
using DidComm.Mediator.Pipeline;
using DidComm.Protocols.Routing;
using DidComm.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// PR #109 review R1-2 over the real mediator endpoints: one live recipient whose DID is slow to
/// resolve holds up no other recipient. The forward pump only stores; the live push, which packs a
/// <c>delivery</c> for the recipient and so resolves its DID, runs on the push workers, per grant.
/// </summary>
public sealed class MediatorLiveIsolationTests
{
    [Fact]
    public async Task A_live_recipient_whose_key_resolution_stalls_holds_up_no_other_recipients_storage_or_pickup()
    {
        // The reviewer's reproduction: two enrolled grants, A live over a WebSocket; A's outbound key
        // resolution paused (cancellation-cooperative) after A turned live delivery on. Before the fix the forward pump awaited A's live push, so B's forward was accepted
        // (202) but B's stored count stayed 0 until A's resolution was released.
        GatedKeyService? keys = null;
        await using var mediator = await MediatorTestServer.StartAsync(o => o.ConfigureServices = services =>
            services.AddSingleton<IDidKeyService>(sp => keys = new GatedKeyService(new NetDidKeyService(sp.GetRequiredService<NetDid.Core.IDidResolver>()))));
        var bobA = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bobA, a.Did);
        var carolB = await mediator.NewPeerAsync();
        var b = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(carolB, b.Did);
        var alice = await mediator.NewSenderAsync();
        using var socketA = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socketA, bobA);
        keys.Should().NotBeNull("the mediator resolves keys through the gated service");
        keys!.Pause(bobA.Did);

        var forA = await Qa.SendNoteAsync(mediator, alice, a, flush: false, content: "for A");
        await keys.Paused.WaitAsync(MediatorTestServer.Timeout);
        var forB = await Qa.SendNoteAsync(mediator, alice, b, flush: false, content: "for B");

        // B's message reaches storage, and the forward pump goes idle, while A's packing stays stalled.
        await MediatorTestServer.WaitUntilAsync(() => StoredFor(mediator, carolB.Did) == 1, "B's message is stored while A's live push is stalled");
        await mediator.FlushAsync();
        keys.Released.Should().BeFalse();
        mediator.Outcomes.Should().Equal(ForwardOutcomes.Queued, ForwardOutcomes.Queued);
        mediator.PushOutcomes.Should().BeEmpty("A's push is still running and B had nothing to push live");

        // B picks its message up while A's push is still stalled.
        var pickedUp = (await mediator.FetchAsync(carolB)).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(b, pickedUp.Payload, alice)).Message.Id.Should().Be(forB.Id);
        (await mediator.AcknowledgeAsync(carolB, pickedUp.Id)).MessageCount.Should().Be(0);
        keys.Released.Should().BeFalse("all of B's delivery happened with A's resolution still paused");

        // Released: A's live push goes out.
        keys.Release();
        var pushed = (await mediator.ReceiveOverSocketAsync(socketA, bobA)).Delivery(thid: null).Should().ContainSingle().Subject;
        (await MediatorRecipient.UnpackFromSenderAsync(a, pushed.Payload, alice)).Message.Id.Should().Be(forA.Id);
        await mediator.PushesIdleAsync();
        mediator.PushOutcomes.Should().Equal(PushOutcomes.PushedLive);
        (await MediatorDeliveryModesQaTests.AckOverSocketAsync(mediator, socketA, bobA, pushed.Id)).MessageCount.Should().Be(0);
        await socketA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        mediator.LeakingLogLines(a.Did, b.Did, pushed.Id, pickedUp.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task A_live_push_whose_key_resolution_outlasts_the_push_timeout_is_abandoned_and_the_message_is_fetchable()
    {
        // The bound behind the isolation: the stalled push holds its worker only until PushTimeout,
        // then the message goes back to the queue (the session's lease is given back).
        var time = new QaManualTimeProvider();
        GatedKeyService? keys = null;
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.Mediator = m => m.PushTimeout = TimeSpan.FromSeconds(5);
            o.ConfigureServices = services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IDidKeyService>(sp => keys = new GatedKeyService(new NetDidKeyService(sp.GetRequiredService<NetDid.Core.IDidResolver>())));
            };
        });
        var bobA = await mediator.NewPeerAsync();
        var a = await mediator.NewRecipientAsync();
        await mediator.EnrolAsync(bobA, a.Did);
        var alice = await mediator.NewSenderAsync();
        using var socketA = await mediator.ConnectAsync();
        await MediatorDeliveryModesQaTests.GoLiveAsync(mediator, socketA, bobA);
        keys!.Pause(bobA.Did);

        var forA = await Qa.SendNoteAsync(mediator, alice, a, content: "for A");
        await keys.Paused.WaitAsync(MediatorTestServer.Timeout);
        time.Advance(TimeSpan.FromSeconds(5));
        await mediator.PushesIdleAsync();

        mediator.PushOutcomes.Should().Equal(PushOutcomes.Failed);
        keys.Released.Should().BeFalse("the deadline ended the push, not the resolution");
        await socketA.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        keys.Release();
        IReadOnlyList<DidComm.Mediator.Pickup.DeliveredMessage> fetched = Array.Empty<DidComm.Mediator.Pickup.DeliveredMessage>();
        await MediatorTestServer.WaitUntilAsync(() =>
        {
            fetched = mediator.FetchAsync(bobA).GetAwaiter().GetResult();
            return fetched.Count > 0;
        }, "the abandoned push gave its lease back");
        (await MediatorRecipient.UnpackFromSenderAsync(a, fetched.Single().Payload, alice)).Message.Id.Should().Be(forA.Id);
    }

    private static int StoredFor(MediatorTestServer mediator, string grantPeerDid)
        => mediator.Store.GetStatusAsync(grantPeerDid, null, mediator.Clock.Now, CancellationToken.None).AsTask().GetAwaiter().GetResult().MessageCount;

    /// <summary>
    /// The mediator's key service with one DID's <c>keyAgreement</c> resolution paused on demand —
    /// what packing a message to that DID needs — honouring cancellation, as a network resolver
    /// that the DID's controller slows down would.
    /// </summary>
    private sealed class GatedKeyService(NetDidKeyService inner) : IDidKeyService, IDidKeyBindingService
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string? _gated;

        /// <summary>Completes when a resolution of the paused DID reached the gate.</summary>
        public Task Paused => _paused.Task;

        public bool Released => _release.Task.IsCompleted;

        public void Pause(string did) => _gated = did;

        public void Release() => _release.TrySetResult();

        public async Task<IReadOnlyList<Jwk>> GetVerificationMethodsAsync(string did, VerificationRelationship relationship, CancellationToken ct = default)
        {
            if (relationship == VerificationRelationship.KeyAgreement && string.Equals(did, _gated, StringComparison.Ordinal))
            {
                _paused.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
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
