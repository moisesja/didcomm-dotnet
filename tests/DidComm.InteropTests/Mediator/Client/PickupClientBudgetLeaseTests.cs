using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #96 fix round 2, owner decision 2-A: one decrypt budget per delivery
/// (<see cref="MediatorClientOptions.UnpackTimeout"/>), against this library's mediator on a
/// TestServer. The client's clock is a manual one, so the budget runs out exactly when the test says;
/// the container's channel is built over a seam that holds the messages whose content is "slow"
/// until their budget cancels them (a sender DID that never resolves), after the real unpack. Pinned
/// here: with the default lease, a loop that fetches until drained gets past messages the budget gave
/// up on; and what the mediator does when its lease runs out while the client is still inside its
/// budget — it hands the messages to the next fetch again, and it still accepts the client's late
/// acknowledgement of the ones that were opened (it deletes by id, whatever the lease).
/// </summary>
public sealed class PickupClientBudgetLeaseTests
{
    [Fact]
    public async Task With_the_defaults_a_loop_that_fetches_until_drained_gets_past_messages_the_budget_gave_up_on()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var time = new QaManualTimeProvider();
        var slow = new SlowSeam();
        await using var harness = await HarnessAsync(mediator, time, slow);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        await PickupE2E.SendAsync(mediator, alice, recipient, "slow");
        await PickupE2E.SendAsync(mediator, alice, recipient, "slow");
        var behind = await PickupE2E.SendAsync(mediator, alice, recipient, "behind");

        var first = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 2);
        await slow.EnteredAsync(2);
        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        time.Advance(new MediatorClientOptions().UnpackTimeout);
        var failed = await PickupE2E.Within(first);
        failed.Items.Should().HaveCount(2).And.OnlyContain(i => i.Error is TimeoutException);

        // The mediator's clock has not moved: the two are still leased, so the next fetch passes over
        // them and reaches the message behind them.
        var second = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 2));
        var third = await PickupE2E.Within(harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 2));

        PickupE2E.ShouldBeAlicesNote(second.Items.Should().ContainSingle().Subject, alice, recipient, behind);
        third.Drained.Should().BeTrue();
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(2, "the two the budget gave up on were never acknowledged");
    }

    [Fact]
    public async Task When_the_mediators_lease_runs_out_inside_the_budget_the_next_fetch_gets_the_messages_again_and_the_late_acknowledgement_still_deletes_the_opened_one()
    {
        var lease = TimeSpan.FromSeconds(5);
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m => m.Pickup.LeaseTimeout = lease);
        var time = new QaManualTimeProvider();
        var slow = new SlowSeam();
        await using var harness = await HarnessAsync(mediator, time, slow);
        var (bob, recipient) = await PickupE2E.EnrolAsync(mediator, harness);
        var alice = await harness.NewAliceAsync();
        var opened = await PickupE2E.SendAsync(mediator, alice, recipient, "opens");
        await PickupE2E.SendAsync(mediator, alice, recipient, "slow");
        var budget = harness.Channel.Options.UnpackTimeout;
        budget.Should().BeGreaterThan(lease, "the case under test: a mediator whose lease is shorter than the client's budget");

        var fetch = harness.Pickup.FetchAsync(mediator.Did, bob.Did, limit: 10);
        await slow.EnteredAsync(1);

        // The lease runs out while the client is still opening the delivery: a concurrent (or the
        // next) fetch is handed both messages again, the one already opened included.
        mediator.Clock.Advance(lease + TimeSpan.FromSeconds(1));
        var concurrent = await mediator.FetchAsync(bob, 10);
        concurrent.Should().HaveCount(2, "this library's mediator no longer sets aside a message whose lease ran out");

        await time.WaitForScheduledTimersAsync(1, MediatorTestServer.Timeout);
        time.Advance(budget);
        var batch = await PickupE2E.Within(fetch);

        var timedOut = batch.Items.Single(i => i.Error != null).Error.Should().BeOfType<TimeoutException>().Subject;
        timedOut.Message.Should().Contain("of request '").And.Contain("UnpackTimeout").And.NotContain(recipient.Did).And.NotContain(alice.Did);
        var taken = batch.Items.Single(i => i.Error is null);
        PickupE2E.ShouldBeAlicesNote(taken, alice, recipient, opened);
        concurrent.Select(m => m.Id).Should().Contain(taken.Id, "the message the client opened reached the concurrent fetch too: at-least-once");

        // The client acknowledged the opened one after its lease ran out (and after the concurrent
        // fetch leased it again): the mediator still deleted it. The slow one stays.
        (await mediator.StatusAsync(bob)).MessageCount.Should().Be(1, "the late acknowledgement was accepted; the slow message stays queued");
    }

    private static Task<MediatorClientHarness> HarnessAsync(MediatorTestServer mediator, QaManualTimeProvider time, SlowSeam slow)
        => MediatorClientHarness.CreateAsync(mediator, o =>
        {
            o.Time = time;
            o.ConfigureServices = services => services.AddSingleton(sp =>
            {
                var client = sp.GetRequiredService<DidCommClient>();
                return new MediatorClientChannel(client, new MediatorClientOptions(), receivesInbound: false, time, sp.GetService<ILoggerFactory>(),
                    send: null, resolveEndpoint: null, unpack: (text, ct) => slow.UnpackAsync(client, text, ct));
            });
        });

    /// <summary>The real unpack; a message whose content is "slow" then waits until its budget cancels it.</summary>
    private sealed class SlowSeam
    {
        private int _entered;

        public async Task<DeliveredUnpack> UnpackAsync(DidCommClient client, string text, CancellationToken ct)
        {
            var unpacked = await client.UnpackDeliveredAsync(text, ct);
            if (unpacked.Result.Message.Body?["content"]?.GetValue<string>() == "slow")
            {
                Interlocked.Increment(ref _entered);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return unpacked;
        }

        public Task EnteredAsync(int count)
            => MediatorTestServer.WaitUntilAsync(() => Volatile.Read(ref _entered) >= count, $"{count} slow message(s) being opened");
    }
}
