using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Pickup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// QA round 3 (#110, direct push): a store outside its contract (it drops the push mark, or judges
/// expiry by a clock of its own) at the edges of <c>MessagePickupOptions.MaxDeliveryBatch</c>, which
/// is also the number of pushes per run. QA1-1's first guard (an operation remembered the ids it
/// took) did not survive the per-run yield at batch 1 (QA3-1, QA3-2); it was replaced by one rule:
/// any direct push that does not deliver ends its run, with no yield, and pauses the recipient's
/// direct pushes, and a record found expired at send time ends the run. Seam-driven: the scripted
/// sender and directory of <see cref="DirectWorld"/>, the host-store doubles of
/// <see cref="DirectPushStateQaTests"/>, the manual clock.
/// </summary>
public sealed class DirectPushBatchBoundQaTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public enum InnerStore
    {
        InMemory,
        Reference,
    }

    private static IMessageStore Inner(InnerStore inner) => inner == InnerStore.InMemory ? new InMemoryMessageStore() : new ReferenceMessageStore();

    // QA-110-QA3-1. With MaxDeliveryBatch = 1 an operation is one push. Before the fix the run ended
    // after the first POST with the yield (the grant was woken again), so the second take of the same
    // record, which the per-operation guard would have recognised, never happened inside the
    // operation that took it: a store that drops the push mark got the refused record POSTed again at
    // every yield, without end. Now the refusal ends the run with no yield and pauses the recipient's
    // direct pushes. The endpoint accepts once the refused record has been posted Cap times, so a
    // looping product ends.
    [Theory]
    [InlineData(InnerStore.InMemory)]
    [InlineData(InnerStore.Reference)]
    public async Task QA3_1_With_one_push_per_run_a_store_that_drops_the_push_mark_still_gets_a_refused_record_posted_at_most_once_per_wake_up(InnerStore inner)
    {
        const int Cap = 5;
        var store = new MarkDroppingStore(Inner(inner));
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = 1, s => s.AddSingleton<IMessageStore>(store));
        direct.Fixture.Dispatcher.PerRun.Should().Be(1);
        var grant = await direct.GrantAsync("qa3-batch-one");
        var posts = 0;
        direct.Sends.OnSend = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) > Cap);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 31));

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        Volatile.Read(ref posts).Should().Be(1, "one wake-up is at most one attempt of a record even when the store lost the mark (IMessageStore: 'one send per wake-up'), whatever MaxDeliveryBatch is");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
    }

    // QA-110-QA3-2. The same edge with a store whose clock lags (QA1-3): the step finds the record
    // expired and gives it back unsent. Before the fix ("Expired, go on") the run then yielded at
    // batch 1, and the next operation took the same record again, for as long as the clocks
    // disagreed. Now a record found expired at send time ends the run: one take per wake-up.
    [Fact]
    public async Task QA3_2_With_one_push_per_run_a_store_whose_clock_lags_makes_one_wake_up_take_an_expired_record_a_bounded_number_of_times()
    {
        var store = new LaggingClockStore(new InMemoryMessageStore(), TimeSpan.FromSeconds(5));
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = 1, s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa3-batch-one-lag");
        var expires = direct.Fixture.Time.GetUtcNow().ToUnixTimeSeconds() + 2;
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 32), new PickupDeliveryOptions(null, expires, AllowLive: false));
        direct.Fixture.Time.Advance(TimeSpan.FromSeconds(3)); // expired on the mediator's clock, not yet on the store's
        store.CatchUpAfter = 6;

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        direct.Sends.Sent.Should().BeEmpty("an expired message is never sent");
        store.PushTakes.Should().Be(1, "one wake-up takes a record it found expired once, not until the clocks agree");
    }

    // At any batch size: the refusal ends the run at once, long before the run's bound (no yield, no
    // second take, no second POST).
    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(200)]
    public async Task With_two_or_more_pushes_per_run_a_store_that_drops_the_push_mark_gets_a_refused_record_posted_once_per_wake_up(int batch)
    {
        var store = new MarkDroppingStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        direct.Fixture.Dispatcher.PerRun.Should().Be(batch);
        var grant = await direct.GrantAsync("qa3-batch-" + batch);
        var posts = 0;
        direct.Sends.OnSend = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) > 5);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 33));

        direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
        await direct.Fixture.WaitForPushOutcomesAsync(1, Wait);
        await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);

        Volatile.Read(ref posts).Should().Be(1);
        store.PushTakes.Should().ContainSingle("taken and sent once: the refusal ended the run");
        direct.Fixture.PushOutcomeLog.Should().Equal(PushOutcomes.Failed);
    }

    // The bound the mediator promises a store that drops the mark (IMessageStore): at most one POST of
    // a refused record per pause, at batch 1 and 10. Wake-ups during the pause post nothing (the live
    // step still runs, and finds no session); the first one after it posts the record once more.
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task A_store_that_drops_the_push_mark_gets_a_refused_record_posted_at_most_once_per_pause(int batch)
    {
        var store = new MarkDroppingStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa3-pause-" + batch);
        var posts = 0;
        direct.Sends.OnSend = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) > 5);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 34));
        var pause = 2 * new MediatorOptions().PushTimeout;

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            for (var wake = 0; wake < 3; wake++)
            {
                direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
                await direct.Fixture.WaitForPushOutcomesAsync(3 * (cycle - 1) + 1 + wake, Wait); // a cycle: one Failed, then two skips
                await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            }
            Volatile.Read(ref posts).Should().Be(cycle, "pause {0}: one POST, however many wake-ups", cycle);
            direct.Fixture.Time.Advance(pause);
        }
        direct.Fixture.PushOutcomeLog.Where(o => o == PushOutcomes.Failed).Should().HaveCount(3);
        direct.Fixture.PushOutcomeLog.Where(o => o == PushOutcomes.SkippedCooldown).Should().HaveCount(6);
    }

    // A store that drops the push mark and an endpoint that refuses with a 4xx (no pause, ADV-110-1a-4):
    // the run goes on to the next record, which is the same record again, until the run's bound
    // (MaxDeliveryBatch) ends it without the yield. At most MaxDeliveryBatch POSTs per wake-up, never an
    // endless loop (QA3-1's shape at batch 1, and at 10). The endpoint accepts after Cap POSTs, so a
    // looping product ends.
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task A_store_that_drops_the_push_mark_gets_a_4xx_refused_record_posted_at_most_MaxDeliveryBatch_times_per_wake_up(int batch)
    {
        const int Cap = 50;
        var store = new MarkDroppingStore(new InMemoryMessageStore());
        await using var direct = await DirectWorld.StartAsync(o => o.Pickup.MaxDeliveryBatch = batch, s => s.AddSingleton<IMessageStore>(store));
        var grant = await direct.GrantAsync("qa3-refused-" + batch);
        var posts = 0;
        direct.Sends.OnAnswer = (_, _, _) => Task.FromResult(Interlocked.Increment(ref posts) > Cap ? DirectSendResult.Delivered : DirectSendResult.Refused);
        await direct.Fixture.StoreAsync(grant.Recipient, PushFixture.Payload(64, 35));

        for (var wake = 1; wake <= 2; wake++)
        {
            direct.Fixture.Dispatcher.Wake(grant.Peer).Should().BeTrue();
            await direct.Fixture.WaitForPushOutcomesAsync(wake * batch, Wait);
            await direct.Fixture.Dispatcher.WaitForIdleAsync(Wait);
            Volatile.Read(ref posts).Should().Be(wake * batch, "wake-up {0}: at most MaxDeliveryBatch POSTs, then the run ends without the yield", wake);
        }
        direct.Fixture.PushOutcomeLog.Should().OnlyContain(o => o == PushOutcomes.Failed, "a refusal pauses nothing");
    }
}
