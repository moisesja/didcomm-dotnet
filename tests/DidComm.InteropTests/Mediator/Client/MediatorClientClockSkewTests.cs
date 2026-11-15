using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 acceptance on clocks (design §7 "Clock"): over a WebSocket, the mediator's answers are
/// checked against the recipient's clock. A mediator 61 seconds ahead (beyond the default
/// <c>ReplayProtectionOptions.ClockSkew</c> of 60 s) has its answers dropped as future-dated, so the
/// call times out on the client's injected clock — although the mediator acted — and the same call
/// succeeds once the clocks agree (the requests are safe to repeat); a mediator 30 seconds ahead
/// works. Every request carries <c>created_time</c> from the client's own clock (FR-RR-09).
/// </summary>
public sealed class MediatorClientClockSkewTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_mediator_61_seconds_ahead_times_out_and_the_retry_succeeds_once_the_clocks_agree()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.ClockOffset = TimeSpan.FromSeconds(-61);
            o.Client = c => c.RequestTimeout = RequestTimeout;
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);
        var ws = harness.WebSocketEndpoint;

        // status-request: the mediator answers, the client drops the answer, the call times out.
        var status = harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: ws);
        await DroppedAsFutureDatedAsync(mediator, 1);
        status.IsCompleted.Should().BeFalse();
        time.Advance(RequestTimeout);
        var timedOut = (await FluentActions.Awaiting(() => PickupE2E.Within(status)).Should().ThrowAsync<TimeoutException>()).Which;
        timedOut.Message.Should().NotContain(mediator.Did).And.NotContain(bob.Did);

        // delivery-request: the mediator leases the message to the session and its answer is
        // dropped; nothing was acknowledged.
        var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws);
        await DroppedAsFutureDatedAsync(mediator, 2);
        time.Advance(RequestTimeout);
        await FluentActions.Awaiting(() => PickupE2E.Within(fetch)).Should().ThrowAsync<TimeoutException>();
        await observer.WaitForAsync(bob.Did, 2);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now - TimeSpan.FromSeconds(61));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "the mediator acted (leased the message) but nothing was acknowledged");

        // The clocks agree again: the same calls succeed; after the lease the message is delivered.
        harness.Options.ClockOffset = TimeSpan.Zero;
        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: ws))).MessageCount.Should().Be(1);
        mediator.Clock.Advance(TimeSpan.FromSeconds(61));
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws));
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_mediator_30_seconds_ahead_works()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.ClockOffset = TimeSpan.FromSeconds(-30);
            o.Client = c => c.RequestTimeout = RequestTimeout;
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);
        var ws = harness.WebSocketEndpoint;

        (await PickupE2E.Within(harness.Pickup.GetStatusAsync(mediator.Did, bob.Did, serviceEndpointOverride: ws))).MessageCount.Should().Be(1);
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws));

        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        await observer.WaitForAsync(bob.Did, 3);
        PickupE2E.ShouldAllBeStampedWith(observer, bob.Did, mediator.Clock.Now - TimeSpan.FromSeconds(30));
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
        mediator.Logs.Entries.Should().NotContain(e => e.Contains("replay admission"));
    }

    // The client's inbound dispatch dropped the mediator's answer as future-dated (FR-RR-08), the
    // n-th time: the rendezvous before the client's deadline is moved.
    private static Task DroppedAsFutureDatedAsync(MediatorTestServer mediator, int count)
        => MediatorTestServer.WaitUntilAsync(
            () => mediator.Logs.Entries.Count(e => e.Contains("replay admission FutureDated")) >= count,
            $"the client dropped {count} future-dated answer(s)");
}
