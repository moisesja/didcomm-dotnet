using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance over the mediator's WebSocket (the harness's WebSocket transport with
/// <c>ReceiveInbound</c> + <c>UseInboundDispatch()</c>, reached through <c>serviceEndpointOverride</c>):
/// <see cref="PickupClient.ReceiveLiveAsync"/> takes what was queued before live delivery was turned
/// on by its drain and what arrives later by push, acknowledges an item only when the next one is
/// asked for, turns live delivery off at the end, and the last item of a stream comes back on the
/// next one; a delayed message is picked up by the periodic cycle (FR-PICK-08) on the client's
/// injected clock; a message that does not unpack is yielded and never acknowledged; fetch and the
/// live toggle work over the same socket; every request is stamped from the client's clock (FR-RR-09).
/// </summary>
public sealed class PickupClientWebSocketEndToEndTests
{
    [Fact]
    public async Task Queued_messages_arrive_by_the_drain_later_ones_by_push_each_acknowledged_when_the_next_is_asked_for()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var m1 = await PickupE2E.SendAsync(mediator, alice, recipient, "m1");
        var m2 = await PickupE2E.SendAsync(mediator, alice, recipient, "m2");

        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator();
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(stream), alice, recipient, m1);
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(stream), alice, recipient, m2);
        mediator.Store.MessageCount.Should().Be(2, "nothing is acknowledged before the consumer asks for the next item");

        // Asking for a third: m2 is taken, nothing else is ready, so m1 and m2 are acknowledged;
        // the drain then finds nothing and the stream waits.
        var third = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 4);
        observer.KindsFrom(bob.Did).Should().Equal("live-on", "delivery-request", "messages-received", "delivery-request");
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "m1 and m2 were acknowledged");
        third.IsCompleted.Should().BeFalse();

        // Now a message arrives: it is pushed on the live socket.
        var m3 = await PickupE2E.SendAsync(mediator, alice, recipient, "m3");
        (await PickupE2E.Within(third)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, m3);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "m3 arrived after live delivery was on");
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now);

        // Like a `break` after m3: the enumeration ends without asking for another item.
        await stream.DisposeAsync().AsTask().WaitAsync(MediatorTestServer.Timeout);

        await observer.WaitForAsync(bob.Did, 5);
        observer.KindsFrom(bob.Did)[^1].Should().Be("live-off", "the stream turns live delivery off at the end");
        mediator.Store.MessageCount.Should().Be(1, "m3 was the last item: not acknowledged, it comes back");

        // After the lease, the next stream gets m3 again (it is not remembered as handled).
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        await using (var next = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint).GetAsyncEnumerator())
        {
            var again = await PickupE2E.NextAsync(next);
            again.Unpacked!.Message.Body!["content"]!.GetValue<string>().Should().Be("m3");
        }
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }

    [Fact]
    public async Task A_delayed_message_is_picked_up_by_the_periodic_cycle_on_the_clients_clock()
    {
        // FR-PICK-08: a delayed message is never pushed when it falls due; the client's next cycle
        // drains it. The cycle runs on the client's injected TimeProvider.
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.LivePollInterval = TimeSpan.FromMinutes(1);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);

        var note = alice.Note(recipient.Did, "later");
        var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did), Qa.Delay(30_000));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Clock.Advance(TimeSpan.FromSeconds(30));
        await mediator.PushesIdleAsync();
        first.IsCompleted.Should().BeFalse("a delayed message is not pushed when it falls due");
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(0);

        time.Advance(TimeSpan.FromMinutes(1));

        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        // The re-armed live-delivery-change also wakes the grant (#95), so the now-due message reaches
        // the stream either by that push or by the drain's delivery-request, whichever leases it first;
        // both are the cycle's doing. The observer runs off the reply path, so wait for its record.
        await observer.WaitForAsync(bob.Did, 3);
        observer.KindsFrom(bob.Did).Should().StartWith(new[] { "live-on", "delivery-request", "live-on" },
            "the periodic cycle turns live delivery on again");
        // Asking for the next item takes it, and it is acknowledged.
        var next = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 0, "the delayed message was acknowledged");
        observer.KindsFrom(bob.Did).Should().Contain("messages-received");
        await PickupE2E.StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task A_live_message_that_does_not_unpack_is_yielded_and_never_acknowledged()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var good = await PickupE2E.SendAsync(mediator, alice, recipient, "good");

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var failed = await PickupE2E.NextAsync(stream);
        failed.Error.Should().NotBeNull();
        failed.Unpacked.Should().BeNull();
        PickupE2E.ShouldBeAlicesNote(await PickupE2E.NextAsync(stream), alice, recipient, good);
        var waiting = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => mediator.Store.MessageCount == 1, "the good one was acknowledged");
        await PickupE2E.StopAsync(stream, waiting, cts);

        mediator.Store.MessageCount.Should().Be(1, "the failure is never acknowledged");
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Fetch_status_acknowledge_and_the_live_toggle_work_over_the_socket()
    {
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o => o.UseWebSocket = true);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);
        await PickupE2E.PostUnopenableAsync(mediator, alice, recipient);
        var ws = harness.WebSocketEndpoint;
        var pickup = harness.Pickup;

        (await pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: ws)).MessageCount.Should().Be(2);
        var batch = await pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws);
        batch.Items.Should().HaveCount(2);
        PickupE2E.ShouldBeAlicesNote(batch.Items[0], alice, recipient, note);
        batch.Items[1].Error.Should().NotBeNull();
        (await pickup.AckAsync(mediator.Did, bob.Did, new[] { batch.Items[1].Id }, ws)).MessageCount.Should().Be(0);

        (await pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, true, ws)).LiveDelivery.Should().BeTrue();
        (await pickup.SetLiveDeliveryAsync(mediator.Did, bob.Did, false, ws)).LiveDelivery.Should().BeFalse();

        await observer.WaitForAsync(bob.Did, 6);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now);
        mediator.LeakingLogLines(recipient.Did).Should().BeEmpty();
    }
}
