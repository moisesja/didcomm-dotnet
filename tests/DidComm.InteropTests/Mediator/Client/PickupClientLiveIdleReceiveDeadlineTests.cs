using AwesomeAssertions;
using DidComm.Mediator.Pipeline;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// The client side of live delivery under <c>WebSocketTransportOptions.MessageReceiveTimeout</c>: a
/// <see cref="DidComm.Mediator.Client.PickupClient.ReceiveLiveAsync"/> stream over the mediator's
/// WebSocket sits idle on its session socket while the client's clock (the container's, which the
/// transport times messages on) moves past many limits, and the next arrival is still pushed to it.
/// Only a started message is timed on the client; the wait for the next push never is.
/// </summary>
public sealed class PickupClientLiveIdleReceiveDeadlineTests
{
    // Not the default (60 s): the transport is configured, so the idle wait below spans ten of its limits.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(37);

    [Fact]
    public async Task An_idle_live_stream_outlasts_many_receive_limits_on_the_clients_clock_and_gets_the_next_push()
    {
        var time = new QaManualTimeProvider();
        var observer = new PickupRequestObserver();
        await using var mediator = await PickupE2E.StartMediatorAsync(observer);
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.WebSocket = w => w.MessageReceiveTimeout = Limit;
            // Far beyond the idle wait, so the stream's periodic cycle never fires during it.
            o.Client = c => c.LivePollInterval = TimeSpan.FromHours(24);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = PickupE2E.MoveNext(stream);
        await observer.WaitForAsync(bob.Did, 2); // live-on, then the drain's delivery-request

        // Wait for the client itself: no request waiting (no request deadline left on the clock) and
        // one timer, the stream's next cycle. So the move below can fire nothing but a receive limit.
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 0 && time.ScheduledTimers == 1, "the stream finished its first cycle and waits for a push");
        time.HasOnlyTimerDueIn(TimeSpan.FromHours(24)).Should().BeTrue("the only timer is the stream's next cycle");
        var idle = time.Mark();
        var fired = time.TimersFired;

        time.Advance(10 * Limit);

        time.TimersFired.Should().Be(fired, "the session socket's wait for the next push is not timed");
        time.Mark().Should().Be(idle, "no timer was created for the idle wait");
        first.IsCompleted.Should().BeFalse("nothing was queued yet");

        var note = await PickupE2E.SendAsync(mediator, alice, recipient, "after a long idle");
        (await PickupE2E.Within(first)).Should().BeTrue("the arrival reaches the idle stream");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, note);
        await mediator.PushesIdleAsync();
        mediator.PushOutcome(PushOutcomes.PushedLive).Should().Be(1, "it was pushed live on the idle socket, not fetched by a cycle");

        var next = PickupE2E.MoveNext(stream);
        await PickupE2E.StopAsync(stream, next, cts);
    }
}
