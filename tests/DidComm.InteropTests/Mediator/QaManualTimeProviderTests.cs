using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// The test clock itself (#119): it never steps back, even when an <see cref="QaManualTimeProvider.Advance"/>
/// that started first finishes last (re-entered from a timer callback, or held in a callback while
/// another thread advances; each interleaving is forced, never left to the scheduler), and its two
/// rendezvous helpers say which timer an advance fires.
/// </summary>
public sealed class QaManualTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_advance_made_from_a_timer_callback_that_goes_further_is_not_stepped_back()
    {
        var time = new QaManualTimeProvider(Start);
        using var timer = time.CreateTimer(_ => time.Advance(TimeSpan.FromSeconds(60)), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(10));

        time.GetUtcNow().Should().Be(Start + TimeSpan.FromSeconds(65), "the inner advance went from +5 s to +65 s; the outer one's target, +10 s, is behind it");
    }

    [Fact]
    public async Task An_advance_held_in_a_callback_while_another_thread_advances_further_does_not_step_the_clock_back()
    {
        var time = new QaManualTimeProvider(Start);
        using var inCallback = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var timer = time.CreateTimer(_ =>
        {
            inCallback.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // a hang guard: the test releases it below
        }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);

        var first = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1))); // target +1 s, held at +0.5 s
        inCallback.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(60)); // from +0.5 s to +60.5 s
        release.Set();
        await first;

        time.GetUtcNow().Should().Be(Start + TimeSpan.FromSeconds(60.5), "the first advance finished last with a target behind the clock");
    }

    [Fact]
    public void Sequential_advances_move_forward_by_exactly_what_was_asked_and_count_every_firing()
    {
        var time = new QaManualTimeProvider(Start);
        using var periodic = time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7));
        var last = time.GetUtcNow();
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            time.GetUtcNow().Should().Be(last + TimeSpan.FromSeconds(3));
            last = time.GetUtcNow();
        }

        // Due at +5, +12, … +54: eight firings in 60 s.
        time.TimersFired.Should().Be(8);
    }

    [Fact]
    public void HasOnlyTimerDueIn_is_true_only_for_a_single_scheduled_timer_due_exactly_that_far_ahead()
    {
        var time = new QaManualTimeProvider(Start);
        time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1)).Should().BeFalse("no timer");

        using var cycle = time.CreateTimer(_ => { }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1)).Should().BeTrue();
        time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)).Should().BeFalse();
        time.HasOnlyTimerDueIn(TimeSpan.FromSeconds(30)).Should().BeFalse();

        using (time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan))
            time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1)).Should().BeFalse("a second timer is scheduled");
        time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1)).Should().BeTrue("the second one was disposed");

        using var stopped = time.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        time.HasOnlyTimerDueIn(TimeSpan.FromMinutes(1)).Should().BeTrue("a timer that is not scheduled does not count");

        time.Advance(TimeSpan.FromSeconds(20));
        time.HasOnlyTimerDueIn(TimeSpan.FromSeconds(40)).Should().BeTrue("measured from the clock's reading now");
    }

    [Fact]
    public void TimersFired_counts_the_firings_of_one_advance()
    {
        var time = new QaManualTimeProvider(Start);
        using var a = time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        using var b = time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        using var later = time.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(61), Timeout.InfiniteTimeSpan);

        time.Advance(TimeSpan.FromSeconds(60));

        time.TimersFired.Should().Be(2, "the timer due at +61 s did not come due");
    }
}
