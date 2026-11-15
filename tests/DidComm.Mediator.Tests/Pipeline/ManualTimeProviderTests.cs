using AwesomeAssertions;
using DidComm.Mediator.Tests.Client;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// The test clock itself (#119). <see cref="ManualTimeProvider.Advance"/> used to end with
/// <c>_now = target</c>, so an Advance made while it ran — from a timer it fired, or by a fake
/// mediator's handler on another thread — was overwritten and the clock stepped back (a live stream
/// treats a reading below a stored one as due at once, and a timer set before the step was pushed
/// further away). Forced here with a timer callback that advances and joins.
/// </summary>
public sealed class ManualTimeProviderTests
{
    private static readonly TimeSpan Outer = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Inner = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_advance_made_while_another_one_fires_a_timer_is_kept_and_the_clock_never_steps_back(bool otherThread)
    {
        var clock = new ManualTimeProvider();
        var start = clock.GetUtcNow();
        var startTicks = clock.GetTimestamp();
        DateTimeOffset inside = default;
        using var timer = clock.CreateTimer(_ =>
        {
            if (otherThread)
                Join(Task.Run(() => clock.Advance(Inner)));
            else
                clock.Advance(Inner);
            inside = clock.GetUtcNow();
        }, null, Outer, Timeout.InfiniteTimeSpan);

        clock.Advance(Outer).Should().Be(1);

        inside.Should().Be(start + Outer + Inner);
        clock.GetUtcNow().Should().Be(start + Outer + Inner, "the outer Advance keeps the inner one's progress (it used to end at start + {0})", Outer);
        clock.GetElapsedTime(startTicks).Should().Be(Outer + Inner);
    }

    [Fact]
    public void Timers_the_inner_advance_reaches_fire_once_and_the_outer_one_counts_only_its_own()
    {
        var clock = new ManualTimeProvider();
        var fired = new List<string>();
        int? innerFired = null;
        using var first = clock.CreateTimer(_ =>
        {
            fired.Add("first");
            innerFired = clock.Advance(Inner);
        }, null, Outer, Timeout.InfiniteTimeSpan);
        using var second = clock.CreateTimer(_ => fired.Add("second"), null, Outer + TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

        clock.Advance(Outer).Should().Be(1);

        innerFired.Should().Be(1);
        fired.Should().Equal("first", "second");
        clock.ScheduledTimers.Should().Be(0);
    }

    [Fact]
    public void Advance_returns_how_many_timers_it_fired()
    {
        var clock = new ManualTimeProvider();
        using var once = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        using var every = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1)).Should().Be(0);
        clock.Advance(TimeSpan.FromTicks(1)).Should().Be(1, "the periodic timer's first tick");
        clock.Advance(TimeSpan.FromSeconds(2)).Should().Be(3, "the one-shot timer and two periodic ticks");
    }

    [Fact]
    public async Task WaitForTimerAsync_is_met_only_by_a_timer_created_after_the_mark_with_that_due_time_and_still_scheduled()
    {
        var clock = new ManualTimeProvider();
        var interval = TimeSpan.FromSeconds(1);
        using var older = clock.CreateTimer(_ => { }, null, interval, Timeout.InfiniteTimeSpan);
        var mark = clock.Mark();

        var waiting = clock.WaitForTimerAsync(interval, mark, PickupTest.Timeout);
        waiting.IsCompleted.Should().BeFalse("a timer created before the mark does not count");
        using var other = clock.CreateTimer(_ => { }, null, interval - TimeSpan.FromTicks(1), Timeout.InfiniteTimeSpan);
        waiting.IsCompleted.Should().BeFalse("a timer of another due time does not count");
        using var wanted = clock.CreateTimer(_ => { }, null, interval, Timeout.InfiniteTimeSpan);
        await PickupTest.Within(waiting);

        clock.Advance(interval).Should().Be(3);
        clock.WaitForTimerAsync(interval, mark, PickupTest.Timeout).IsCompleted.Should().BeFalse("a timer that fired is no longer scheduled");
    }

    private static void Join(Task task)
    {
        if (!task.Wait(PickupTest.Timeout))
            throw new TimeoutException("the other thread's Advance did not finish");
    }
}
