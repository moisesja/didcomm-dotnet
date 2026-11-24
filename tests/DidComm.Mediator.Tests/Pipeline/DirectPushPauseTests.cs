using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using Polly.Timeout;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// The rules for a direct push that does not deliver (#110: the churn simplification of QA round 3,
/// and the coordinator's call on ADV-110-1a-4). A 4xx other than 408 and 429 — the endpoint's
/// definite "not this message" — costs one POST and moves on to the next record with no pause; a run
/// that met one ends at its bound without the yield. Anything else — a 5xx, 408 or 429, a fault of
/// the send, the transport's or the HTTP client's own timeout, or the push deadline — costs exactly
/// one POST, ends its run with no yield, and pauses that recipient's direct pushes for
/// 2 × <c>PushTimeout</c> on the monotonic clock. Wake-ups during the pause post nothing (the live
/// step still runs: a live session that appears gets its pushes); the first wake-up after it pushes
/// the next record; the one that did not deliver is never sent again. The real dispatcher and push
/// step in a container built by <c>AddMediator()</c>, with a scripted directory and sender
/// (<see cref="DirectWorld"/>) and the manual clock.
/// </summary>
public sealed class DirectPushPauseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushTimeout = new MediatorOptions().PushTimeout;
    private static readonly TimeSpan Pause = PushDispatcher.CooldownPushTimeouts * PushTimeout;

    public enum Failure
    {
        /// <summary>The endpoint answered a 5xx, 408 or 429, a 3xx not followed, or a 307 chain past its bound (the sender's Failed).</summary>
        ServerError,

        /// <summary>The send failed (a reset, a refused connection, a guard refusal on a hop).</summary>
        SendFault,

        /// <summary>The transport's own request timeout (Polly's timeout strategy).</summary>
        TransportTimeout,

        /// <summary>The HTTP client's own timeout: a cancellation that is not the push's deadline.</summary>
        ClientTimeout,

        /// <summary>The push deadline.</summary>
        Deadline,
    }

    [Theory]
    [InlineData(Failure.ServerError)]
    [InlineData(Failure.SendFault)]
    [InlineData(Failure.TransportTimeout)]
    [InlineData(Failure.ClientTimeout)]
    [InlineData(Failure.Deadline)]
    public async Task A_direct_push_that_does_not_deliver_costs_one_POST_and_pauses_its_recipients_direct_pushes_and_the_next_record_goes_at_the_first_wake_up_after_the_pause(Failure failure)
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("pause-" + failure);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        direct.Sends.OnSend = async (_, payload, ct) =>
        {
            if (DidComm.TestSupport.StandInJwe.SeedOf(payload.Span) != 61)
                return true;
            switch (failure)
            {
                case Failure.ServerError:
                    return false;
                case Failure.SendFault:
                    throw new HttpRequestException("connection reset");
                case Failure.TransportTimeout:
                    throw new TimeoutRejectedException();
                case Failure.ClientTimeout:
                    throw new TaskCanceledException("the client's own timeout", new TimeoutException());
                default:
                    held.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct); // never answers; honours the deadline
                    return true;
            }
        };
        var first = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 61));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 62));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 63));
        var mark = fixture.Time.Mark();

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        if (failure == Failure.Deadline)
        {
            await held.Task.WaitAsync(Wait);
            await fixture.Time.WaitForTimerAsync(PushTimeout, mark, Wait);
            fixture.Time.Advance(PushTimeout).Should().Be(1, "only the held push's deadline is due");
        }
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await MessagePickupLive.UntilAsync(() => fixture.Dispatcher.Unsettled == 0, "the failed push settled");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
        direct.Sends.Seeds.Should().Equal(new byte[] { 61 }, "one POST, and the run ended there ({0})", failure);

        // During the pause: each wake-up is served, its direct step skipped; nothing is posted.
        for (var wake = 0; wake < 2; wake++)
        {
            fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await fixture.WaitForPushOutcomesAsync(2 + wake, Wait);
            await fixture.Dispatcher.WaitForIdleAsync(Wait);
        }
        direct.Sends.Seeds.Should().Equal(new byte[] { 61 }, "nothing is posted during the pause");
        fixture.PushOutcomeLog.Skip(1).Should().Equal(PushOutcomes.SkippedCooldown, PushOutcomes.SkippedCooldown);

        // Just before the pause ends: still paused.
        fixture.Time.Advance(Pause - TimeSpan.FromTicks(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog[3].Should().Be(PushOutcomes.SkippedCooldown, "the pause is 2 × PushTimeout from the failure");

        // The first wake-up after the pause pushes the records behind it; the failed one never again.
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(6, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Skip(4).Should().Equal(PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 61, 62, 63 });
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { first.MessageId }, "the one that did not deliver is the recipient's to pick up");
    }

    // A 4xx other than 408 and 429 is the endpoint's definite "not this message": one POST, the
    // record kept for pickup, and the next record goes on at once — no pause, so messages anyone
    // stored ahead of a real one cannot hold it back (ADV-110-1a-4).
    [Fact]
    public async Task A_4xx_refusal_costs_one_POST_and_the_next_record_goes_on_at_once_with_no_pause()
    {
        await using var direct = await DirectWorld.StartAsync();
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("refused-4xx");
        direct.Sends.Refuse(61);
        var refused = await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 61));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 62));
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 63));

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(3, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.PushedDirect, PushOutcomes.PushedDirect);
        direct.Sends.Seeds.Should().Equal(new byte[] { 61, 62, 63 }, "one wake-up: the refusal held nothing back");

        // No pause: the next wake-up runs the direct step at once, and finds nothing left to push.
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().NotContain(PushOutcomes.SkippedCooldown);
        direct.Sends.Seeds.Should().HaveCount(3, "the refused record is never sent again");
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { refused.MessageId }, "the refused one is the recipient's to pick up");
        PipelineLogs.Of<PushDispatcher>(fixture.Logs).Should().NotContain(l => l.Formatted.Contains("paused for", StringComparison.Ordinal), "nothing was paused");
    }

    // A run that met a refusal ends at its MaxDeliveryBatch bound without the per-run yield: a real
    // message behind more refused ones than the bound waits for the next wake-up (or pickup), and a
    // store that drops the push mark costs at most MaxDeliveryBatch POSTs per wake-up.
    [Fact]
    public async Task A_run_that_met_a_refusal_ends_at_its_bound_without_the_yield_and_the_rest_go_at_the_next_wake_up()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = 2);
        var fixture = direct.Fixture;
        var grant = await direct.GrantAsync("refused-bound");
        for (byte seed = 71; seed <= 73; seed++)
        {
            direct.Sends.Refuse(seed);
            await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, seed));
        }
        await fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 74)); // the real one, behind three refusals

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().Equal(new byte[] { 71, 72 }, "the run's bound, and no yield after a refusal");

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(4, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        direct.Sends.Seeds.Should().Equal(new byte[] { 71, 72, 73, 74 });
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.Failed, PushOutcomes.PushedDirect);
    }

    [Fact]
    public async Task A_direct_pause_leaves_other_recipients_and_this_recipients_live_session_alone()
    {
        await using var direct = await DirectWorld.StartAsync(o => o.PushConcurrency = 1);
        var fixture = direct.Fixture;
        var grant = await direct.World.GrantAsync("pause-goes-live");
        direct.Endpoints.Set(grant.Peer, DirectWorld.EndpointFor("pause-goes-live"));
        var other = await direct.GrantAsync("pause-other");
        direct.Sends.Fail(LivePushWorld.Seed(1));
        var refused = await direct.World.StoreAsync(grant, 1);

        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(1, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);

        // Another recipient's direct pushes go on.
        await fixture.StoreAsync(other.Recipient, PushFixture.Payload(64, 71));
        fixture.Dispatcher.Wake(other.Peer).Should().BeTrue();
        await fixture.WaitForPushOutcomesAsync(2, Wait);
        await fixture.Dispatcher.WaitForIdleAsync(Wait);
        fixture.PushOutcomeLog[1].Should().Be(PushOutcomes.PushedDirect);

        // This recipient connects live during its direct pause: what arrives now goes live.
        var session = await direct.World.LiveAsync(grant);
        var live = await direct.World.StoreAsync(grant, 2);
        fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue("a direct pause does not skip a wake-up");
        await fixture.Recorder.WaitForAsync(r => r.TagValues(MediatorMetrics.PushOutcomesInstrument, MediatorMetrics.PushOutcomeTag).Contains(PushOutcomes.PushedLive), Wait, "the live session got its push during the direct pause");
        await fixture.Dispatcher.WaitForIdleAsync(Wait);

        LivePushWorld.Deliveries(session).Select(d => d.Id).Should().Equal(live.MessageId);
        direct.Sends.Seeds.Should().Equal(new[] { LivePushWorld.Seed(1), (byte)71 }, "the refused one was posted once, and nothing else of this recipient");
        (await direct.World.PickupAsync(grant.Peer)).Should().Equal(new[] { refused.MessageId }, "the refused one waits for pickup; the live one is leased to the session");
    }
}
