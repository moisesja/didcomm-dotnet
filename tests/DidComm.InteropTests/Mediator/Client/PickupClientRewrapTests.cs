using System.Net;
using System.Text;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance, the receiver side of FR-ROUTE-06: when the mediator re-wraps what it delivers
/// (<c>RewrapMode.ReanoncryptToNext</c>: a fresh forward anoncrypted to the recipient), the
/// <see cref="PickupClient"/> opens the layer the mediator added and returns Alice's message, over
/// HTTP and over a live WebSocket; and through a two-mediator route whose last hop re-wraps.
/// </summary>
public sealed class PickupClientRewrapTests
{
    [Fact]
    public async Task With_ReanoncryptToNext_the_fetched_items_are_Alices_messages()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);

        var batch = await harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task With_ReanoncryptToNext_the_live_items_are_Alices_messages()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var queued = await PickupE2E.SendAsync(mediator, alice, recipient, "queued");

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(stream), alice, recipient, queued);
        var next = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "the drained message was acknowledged");
        var pushed = await PickupE2E.SendAsync(mediator, alice, recipient, "pushed");

        (await PickupE2E.Within(next)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, pushed);
        var last = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "the pushed message was acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    [Fact]
    public async Task Through_two_mediators_whose_last_hop_rewraps_Bob_gets_Alices_message()
    {
        var network = new MediatorNetwork();
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var m2Identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://m2.test/", Accept: new[] { "didcomm/v2" }));
        await using var m1 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Network = network;
            o.Clock = clock;
        });
        await using var m2 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m2.test";
            o.Network = network;
            o.Clock = clock;
            o.Identity = m2Identity;
            o.Mediator = m => m.RewrapMode = RewrapMode.ReanoncryptToNext;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(m2);

        // M2 is an ordinary recipient of M1 (the #95 two-hop set-up).
        var m2AtM1 = m1.Own(TestPeer.Create(m2Identity));
        await m1.EnrolAsync(m2AtM1);

        // Bob enrols at M2; B is reached through M1, then M2.
        var bob = await harness.NewPeerAsync();
        (await m2.RequestMediationAsync(bob)).Should().Equal(m2.Did);
        var b = m2.Own(await MediatorCast.MintRecipientAsync(m1.Did, m2.Did));
        harness.Hold(b);
        await m2.AddRecipientsAsync(bob, new[] { b.Did });

        var alice = await m1.NewSenderAsync();
        var note = alice.Note(b.Did);
        (await alice.Client.SendAsync(note, new DidComm.Transports.SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await m1.FlushAsync();

        // M2 takes its relay traffic from M1 and posts it to itself (hand-rolled, as in #95).
        var relayed = (await m1.FetchAsync(m2AtM1)).Should().ContainSingle().Subject;
        (await m2.PostAsync(Encoding.UTF8.GetString(relayed.Payload.Span))).Status.Should().Be(HttpStatusCode.Accepted);
        (await m1.AcknowledgeAsync(m2AtM1, relayed.Id)).MessageCount.Should().Be(0);
        await m2.FlushAsync();
        m2.Outcomes.Should().Equal(ForwardOutcomes.Queued);

        var batch = await harness.Pickup.FetchAsync(m2.Did, bob.Did, 10);

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, b, note);
        (await m2.StatusAsync(bob)).MessageCount.Should().Be(0);
        var everyone = new[] { m1.Did, m2.Did, b.Did, bob.Did, alice.Did };
        m1.LeakingLogLines(everyone).Should().BeEmpty();
        m2.LeakingLogLines(everyone).Should().BeEmpty();
    }

    [Fact]
    public async Task A_relay_layer_for_someone_else_is_a_failure_not_a_message()
    {
        // M2 picking up its relay traffic from M1 with the PickupClient: the delivered forward is
        // M2's to relay (its next is B), not a delivery re-wrapped for M2, so it is not opened as a
        // message (FR-ROUTE-06: only self-addressed layers are opened) and is left with M1.
        var network = new MediatorNetwork();
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var m2Identity = await MediatorCast.MintAsync(new DidCommServiceSpec("http://m2.test/", Accept: new[] { "didcomm/v2" }));
        await using var m1 = await MediatorTestServer.StartAsync(o =>
        {
            o.Host = "m1.test";
            o.Network = network;
            o.Clock = clock;
        });
        await using var harness = await MediatorClientHarness.CreateAsync(m1);
        var m2AtM1 = m1.Own(TestPeer.Create(m2Identity));
        harness.Hold(m2AtM1);
        await m1.EnrolAsync(m2AtM1);
        var b = m1.Own(await MediatorCast.MintRecipientAsync(m1.Did, m2Identity.Did));
        var alice = await m1.NewSenderAsync();
        (await alice.Client.SendAsync(alice.Note(b.Did), new DidComm.Transports.SendOptions(new[] { b.Did }, alice.Did))).Transport.Accepted.Should().BeTrue();
        await m1.FlushAsync();

        var batch = await harness.Pickup.FetchAsync(m1.Did, m2AtM1.Did, 10);

        var item = batch.Items.Should().ContainSingle().Subject;
        item.Unpacked.Should().BeNull();
        item.Error.Should().NotBeNull();
        (await m1.StatusAsync(m2AtM1)).MessageCount.Should().Be(1, "a failure is never acknowledged");
    }
}
