using System.Net;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using DidComm.Mediator.Pipeline;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96): delayed forwards (<c>delay_milli</c>) in live mode, on the injected clocks
/// (FR-PICK-08: a delayed message is never pushed when it falls due). A delay longer than
/// <see cref="MediatorClientOptions.LivePollInterval"/>: the cycles before it is due bring nothing,
/// the first after does. A delayed message queued before the stream turned live mode on (before the
/// arrival mark) is reached only by a cycle's drain. A due message after the mark goes out with the
/// grant's next push, without waiting for the client's cycle.
/// </summary>
public sealed class PickupClientDelayedQa2Tests
{
    [Fact]
    public async Task A_delay_longer_than_the_poll_interval_is_not_delivered_early_and_comes_at_the_first_cycle_after_it_is_due()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.LivePollInterval = TimeSpan.FromSeconds(10);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);

        var note = alice.Note(recipient.Did, "in five minutes");
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(note, recipient.Did), Qa.Delay(300_000));

        // Three cycles while it is not due: each turns live on and drains, and brings nothing.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await Qa2.StreamIdleAsync(harness, time);
            mediator.Clock.Advance(TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(10));
            await observer.WaitForAsync(bob.Did, 2 + (2 * cycle));
        }
        first.IsCompleted.Should().BeFalse("a delayed message is not delivered before it is due");

        // Due: the next cycle drains it.
        await Qa2.StreamIdleAsync(harness, time);
        mediator.Clock.Advance(TimeSpan.FromMinutes(5));
        await mediator.PushesIdleAsync();
        first.IsCompleted.Should().BeFalse("it is never pushed when it falls due (FR-PICK-08)");
        time.Advance(TimeSpan.FromSeconds(10));
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        await PickupE2E.StopAsync(stream, next, cts);
    }

    [Fact]
    public async Task A_delayed_message_queued_before_live_mode_is_reached_only_by_a_cycles_drain()
    {
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
        var delayed = alice.Note(recipient.Did, "delayed, queued first");
        await Qa2.PostForwardAsync(mediator, alice, recipient.Did, await alice.AuthcryptAsync(delayed, recipient.Did), Qa.Delay(30_000));

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);
        await Qa2.StreamIdleAsync(harness, time);
        mediator.Clock.Advance(TimeSpan.FromSeconds(31));

        // A later message wakes the grant: it is pushed; the delayed one (before the mark) is not.
        var later = await PickupE2E.SendAsync(mediator, alice, recipient, "later");
        await mediator.PushesIdleAsync();
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, later);
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1);

        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 1, "the pushed one was acknowledged; the delayed one waits");
        await Qa2.StreamIdleAsync(harness, time);
        next.IsCompleted.Should().BeFalse();
        time.Advance(TimeSpan.FromMinutes(1));
        (await PickupE2E.Within(next)).Should().BeTrue("the cycle's drain reaches it");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, delayed);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    [Fact]
    public async Task A_delayed_message_after_the_mark_goes_out_with_the_grants_next_push_without_the_clients_cycle()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.LivePollInterval = TimeSpan.FromHours(1);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await observer.WaitForAsync(bob.Did, 2);

        var delayed = alice.Note(recipient.Did, "delayed");
        var (packed, _) = await alice.ForwardAsync(mediator.Did, recipient.Did, await alice.AuthcryptAsync(delayed, recipient.Did), Qa.Delay(30_000));
        (await mediator.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
        await mediator.FlushAsync();
        mediator.Clock.Advance(TimeSpan.FromSeconds(31));
        await mediator.PushesIdleAsync();
        first.IsCompleted.Should().BeFalse("not pushed when it falls due");

        var later = await PickupE2E.SendAsync(mediator, alice, recipient, "later");
        (await PickupE2E.Within(first)).Should().BeTrue();
        var got = new List<string> { stream.Current.Unpacked!.Message.Id };
        got.Add((await PickupE2E.NextAsync(stream)).Unpacked!.Message.Id);
        got.Should().Equal(delayed.Id, later.Id);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        observer.KindsFrom(bob.Did).Count(k => k == "live-on").Should().Be(1, "no client cycle was needed");
        await PickupE2E.StopAsync(stream, last, cts);
    }
}
