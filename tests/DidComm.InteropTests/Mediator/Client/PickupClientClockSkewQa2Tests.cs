using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.Mediator.Client;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// QA-2 (#96): clock skew between the recipient and the mediator over a WebSocket, in the direction
/// the #96 suites did not cover — the <em>client</em> ahead (its requests are future-dated at the
/// mediator, which refuses them unexecuted) — and in live mode in both directions: what the caller
/// sees (a <see cref="TimeoutException"/> naming no DID) and that retrying, or the stream's own
/// retry after its back-off, works once the clocks agree.
/// </summary>
public sealed class PickupClientClockSkewQa2Tests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_client_61_seconds_ahead_is_refused_unexecuted_times_out_and_the_retry_works_at_once_when_the_clocks_agree()
    {
        var time = new QaManualTimeProvider();
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.RequestTimeout = RequestTimeout;
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var note = await PickupE2E.SendAsync(mediator, alice, recipient);
        var ws = harness.WebSocketEndpoint;
        harness.Options.ClockOffset = TimeSpan.FromSeconds(61);

        var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws);
        await RefusedAtTheMediatorAsync(mediator, 1);
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        time.Advance(RequestTimeout);
        var timedOut = (await FluentActions.Awaiting(() => PickupE2E.Within(fetch)).Should().ThrowAsync<TimeoutException>()).Which;
        timedOut.Message.Should().NotContain(mediator.Did).And.NotContain(bob.Did);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1);

        // The mediator never executed it: nothing is leased, so the retry gets the message at once.
        harness.Options.ClockOffset = TimeSpan.Zero;
        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws));
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, note);
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(0);
    }

    [Fact]
    public async Task A_client_30_seconds_ahead_works_for_fetch_and_live()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.ClockOffset = TimeSpan.FromSeconds(30);
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var fetched = await PickupE2E.SendAsync(mediator, alice, recipient, "fetched");
        var ws = harness.WebSocketEndpoint;

        var batch = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, 10, serviceEndpointOverride: ws));
        PickupE2E.ShouldBeAlicesNote(batch.Items.Should().ContainSingle().Subject, alice, recipient, fetched);

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, ws, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.LiveStreamCount == 1 && harness.Channel.PendingCount == 0, "the stream is live");
        var pushed = await PickupE2E.SendAsync(mediator, alice, recipient, "pushed");
        (await PickupE2E.Within(first)).Should().BeTrue();
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, pushed);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
        mediator.Logs.Entries.Should().NotContain(e => e.Contains("replay admission"));
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(61)]
    public async Task A_live_stream_whose_first_cycle_times_out_on_skew_recovers_by_its_own_retry_once_the_clocks_agree(int clientOffsetSeconds)
    {
        var time = new QaManualTimeProvider();
        await using var mediator = await MediatorTestServer.StartAsync();
        await using var harness = await MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.UseWebSocket = true;
            o.Time = time;
            o.Client = c => c.RequestTimeout = RequestTimeout;
        });
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var queued = await PickupE2E.SendAsync(mediator, alice, recipient, "queued");
        harness.Options.ClockOffset = TimeSpan.FromSeconds(clientOffsetSeconds);

        using var cts = new CancellationTokenSource();
        var stream = harness.Pickup.ReceiveLiveAsync(mediator.Did, bob.Did, harness.WebSocketEndpoint, cts.Token).GetAsyncEnumerator();
        var first = stream.MoveNextAsync().AsTask();
        // live-on: refused at the mediator (client ahead) or answered and the answer dropped (mediator ahead).
        await MediatorTestServer.WaitUntilAsync(() => mediator.Logs.Entries.Any(e => e.Contains("replay admission FutureDated")), "the skew dropped a message");
        await MediatorTestServer.WaitUntilAsync(() => harness.Channel.PendingCount == 1 && time.ScheduledTimers >= 1, "live-on waits for its answer");
        time.Advance(RequestTimeout);
        await Qa2.StreamIdleAsync(harness, time);
        first.IsCompleted.Should().BeFalse("a failed step is retried after the back-off, not thrown");

        harness.Options.ClockOffset = TimeSpan.Zero;
        time.Advance(TimeSpan.FromSeconds(1));
        (await PickupE2E.Within(first)).Should().BeTrue("the retried cycle drained the queued message");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, queued);

        var next = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        var pushed = await PickupE2E.SendAsync(mediator, alice, recipient, "pushed");
        (await PickupE2E.Within(next)).Should().BeTrue("live mode is on after the retry");
        PickupE2E.ShouldBeAlicesNote(stream.Current, alice, recipient, pushed);
        var last = stream.MoveNextAsync().AsTask();
        await Qa2.StoreHoldsAsync(mediator, 0, "acknowledged");
        await PickupE2E.StopAsync(stream, last, cts);
    }

    // The mediator's endpoint refused the client's request as future-dated (FR-RR-08), the n-th time.
    private static Task RefusedAtTheMediatorAsync(MediatorTestServer mediator, int count)
        => MediatorTestServer.WaitUntilAsync(
            () => mediator.Logs.Entries.Count(e => e.Contains("Inbound") && e.Contains("replay admission FutureDated")) >= count,
            $"the mediator refused {count} future-dated request(s)");
}
