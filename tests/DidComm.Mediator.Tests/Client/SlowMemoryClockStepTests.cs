using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="SlowMemory.Strike"/> when the clock's reading is below the entry's last strike (#111 fix
/// round 2, RT2B-2): it is the slow episode the entry already counts, not a new one. The entry keeps
/// its strikes, is stamped with the lower reading, and counts as seen. Also a clock that keeps
/// stepping back, one whose every reading is lower than the one before, and one that steps forward.
/// The clock's readings are set by hand; nothing sleeps and no timer is used.
/// </summary>
public sealed class SlowMemoryClockStepTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(640);
    private static readonly TimeSpan Start = TimeSpan.FromHours(5);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly DeliveryScope Bob = new("did:example:mediator", "did:example:bob");

    // ---- the strikes ----

    /// <summary>
    /// The strikes: a message with two strikes (pause 20 min) fails once more after the clock stepped
    /// back one minute. It keeps two strikes: the pause it is then in is 20 min long, not the third
    /// strike's 40 min.
    /// </summary>
    [Fact]
    public void A_repeat_failure_read_below_the_last_strike_keeps_the_strikes()
    {
        var (clock, memory) = WithTwoStrikes("q1");
        var second = clock.Now;

        clock.Now = second - TimeSpan.FromMinutes(1);
        Held(memory, "q1").Should().BeFalse("a reading below the strike's counts as due (I-12)");
        memory.Strike(Bob, "q1"); // opened once more, it fails again

        clock.Now = second - TimeSpan.FromMinutes(1) + memory.PauseAfter(2);
        Held(memory, "q1").Should().BeFalse("the same episode: the pause is the second strike's {0}, not the third's {1}", memory.PauseAfter(2), memory.PauseAfter(3));
        memory.PauseAfter(3).Should().BeGreaterThan(memory.PauseAfter(2), "the control: a third strike would hold it back longer");
    }

    /// <summary>
    /// Two deliveries of one message, each failing, whose clock readings reach the memory out of
    /// order (each reads the clock before it takes the memory's lock): one episode, one strike.
    /// </summary>
    [Fact]
    public void Two_strikes_whose_readings_arrive_out_of_order_are_one_episode()
    {
        var clock = new HandClock(Start);
        var memory = new SlowMemory(clock, T0, Cap, q: 4, r: 4);

        clock.Now = Start + TimeSpan.FromMilliseconds(5);
        memory.Strike(Bob, "q1"); // the delivery that read the clock later reaches the memory first
        clock.Now = Start;
        memory.Strike(Bob, "q1"); // the one that read it 5 ms earlier

        clock.Now = Start + T0 - Tick;
        Held(memory, "q1").Should().BeTrue("one tick before the first pause ends");
        clock.Now = Start + T0;
        Held(memory, "q1").Should().BeFalse("one episode is one strike: the first pause, {0}, has ended", T0);
    }

    // ---- the stamp ----

    /// <summary>
    /// The stamp: the repeat failure stamps the entry with the lower reading. So the message is held
    /// back again at once (a clock step costs one more opening, not one at every delivery until the
    /// readings are back where they were), and its pause, of the same length, ends exactly that long
    /// after the lower reading.
    /// </summary>
    [Fact]
    public void A_repeat_failure_read_below_the_last_strike_is_stamped_with_the_lower_reading()
    {
        var (clock, memory) = WithTwoStrikes("q1");
        var lower = clock.Now - TimeSpan.FromMinutes(1);

        clock.Now = lower;
        Held(memory, "q1").Should().BeFalse("a reading below the strike's counts as due (I-12)");
        memory.Strike(Bob, "q1");

        Held(memory, "q1").Should().BeTrue("held back again at once, from the lower reading");
        clock.Now = lower + memory.PauseAfter(2) - Tick;
        Held(memory, "q1").Should().BeTrue("one tick before the pause ends, counted from the lower reading");
        clock.Now = lower + memory.PauseAfter(2);
        Held(memory, "q1").Should().BeFalse("the pause, counted from the lower reading, has ended");
    }

    // ---- the eviction order ----

    /// <summary>
    /// The eviction order: the repeat failure counts as seen. In a full partition of four, the entry
    /// struck first fails again after the clock stepped back; the next new entry then evicts the
    /// least recently seen of the others, not that one.
    /// </summary>
    [Fact]
    public void A_repeat_failure_read_below_the_last_strike_counts_as_seen()
    {
        var clock = new HandClock(Start);
        var memory = new SlowMemory(clock, T0, Cap, q: 4, r: 4);
        for (var i = 0; i < 4; i++)
            memory.Strike(Bob, "q" + i);

        clock.Now = Start - TimeSpan.FromMinutes(1);
        memory.Strike(Bob, "q0"); // the same episode: now the most recently seen
        memory.Strike(Bob, "q4"); // one more than the partition keeps

        // Read where the first strikes were stamped, so every entry still kept is inside its pause.
        clock.Now = Start;
        memory.Count.Should().Be(4);
        memory.HeldBack(Bob, new[] { "q0", "q1", "q2", "q3", "q4" }).Should().Equal(new[] { true, false, true, true, true },
            "q1, the least recently seen, was evicted; q0 was seen by its repeat failure and is kept");
    }

    // ---- clocks that keep going back ----

    /// <summary>
    /// A clock that steps back five times, one minute each, the message failing after every step:
    /// each step costs one opening (due once, then held back again at once), the strikes never grow,
    /// and once the clock runs on the message is held back for the same 20 min from the last, lowest
    /// reading and not a tick longer.
    /// </summary>
    [Fact]
    public void A_clock_that_keeps_stepping_back_costs_one_opening_per_step_and_never_a_longer_pause()
    {
        var (clock, memory) = WithTwoStrikes("q1");

        for (var step = 1; step <= 5; step++)
        {
            clock.Now -= TimeSpan.FromMinutes(1);
            Held(memory, "q1").Should().BeFalse("step {0}: a reading below the last strike's is due", step);
            memory.Strike(Bob, "q1");
            Held(memory, "q1").Should().BeTrue("step {0}: held back again at once", step);
        }

        var lowest = clock.Now;
        clock.Now = lowest + memory.PauseAfter(2) - Tick;
        Held(memory, "q1").Should().BeTrue("one tick before the pause ends");
        clock.Now = lowest + memory.PauseAfter(2);
        Held(memory, "q1").Should().BeFalse("five repeat failures on a clock stepping back added no strike: the pause is still {0}", memory.PauseAfter(2));
    }

    /// <summary>
    /// A clock whose every reading is one second lower than the one before: the message is never held
    /// back (I-12: the memory fails open), however often it fails, and no failure adds a strike. Once
    /// the readings are steady again it is held back for the same 20 min from the lowest reading.
    /// </summary>
    [Fact]
    public void A_clock_whose_readings_keep_falling_never_holds_back_and_adds_no_strike()
    {
        var (clock, memory) = WithTwoStrikes("q1");

        clock.FallPerReading = TimeSpan.FromSeconds(1);
        for (var delivery = 1; delivery <= 5; delivery++)
        {
            Held(memory, "q1").Should().BeFalse("delivery {0}: every reading is below the last strike's", delivery);
            memory.Strike(Bob, "q1");
        }
        clock.FallPerReading = TimeSpan.Zero;

        var lowest = clock.Now; // the reading of the last strike
        Held(memory, "q1").Should().BeTrue("steady again: held back from the lowest reading");
        clock.Now = lowest + memory.PauseAfter(2) - Tick;
        Held(memory, "q1").Should().BeTrue("one tick before the pause ends");
        clock.Now = lowest + memory.PauseAfter(2);
        Held(memory, "q1").Should().BeFalse("no strike was added while the readings fell: the pause is still {0}", memory.PauseAfter(2));
    }

    // ---- a clock that steps forward ----

    /// <summary>
    /// A clock that steps forward. Inside the pause it changes nothing: the message stays held back,
    /// and a failure reported there adds no strike (RT1B-2). Past the pause's end the message is due
    /// at once, never held back longer; a failure then is a new episode, with the next pause counted
    /// from the stepped reading.
    /// </summary>
    [Fact]
    public void A_clock_that_steps_forward_ends_the_pause_no_later_and_a_failure_after_it_is_a_new_strike()
    {
        var clock = new HandClock(Start);
        var memory = new SlowMemory(clock, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, "q1");

        clock.Now = Start + T0 / 2; // a step forward, still inside the pause
        Held(memory, "q1").Should().BeTrue("inside its pause");
        memory.Strike(Bob, "q1"); // another delivery of the same episode
        clock.Now = Start + T0 - Tick;
        Held(memory, "q1").Should().BeTrue("one tick before the first pause ends, counted from the first strike");
        clock.Now = Start + T0;
        Held(memory, "q1").Should().BeFalse("the strike inside the pause added nothing");

        var stepped = Start + TimeSpan.FromHours(2); // a step forward, past the pause
        clock.Now = stepped;
        Held(memory, "q1").Should().BeFalse("past its pause: due, never held back longer");
        memory.Strike(Bob, "q1"); // it fails again: a new episode

        clock.Now = stepped + memory.PauseAfter(2) - Tick;
        Held(memory, "q1").Should().BeTrue("the second strike's pause, counted from the stepped reading");
        clock.Now = stepped + memory.PauseAfter(2);
        Held(memory, "q1").Should().BeFalse("and it ends exactly then");
    }

    // A message struck twice on a steady clock: at the start, and when its first pause has ended. It
    // is then inside its second pause (20 min), stamped with the clock's reading now.
    private static (HandClock Clock, SlowMemory Memory) WithTwoStrikes(string id)
    {
        var clock = new HandClock(Start);
        var memory = new SlowMemory(clock, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, id);
        clock.Now = Start + T0;
        Held(memory, id).Should().BeFalse("precondition: the first pause has ended");
        memory.Strike(Bob, id);
        Held(memory, id).Should().BeTrue("precondition: inside the second pause");
        memory.PauseAfter(2).Should().Be(2 * T0);
        return (clock, memory);
    }

    private static bool Held(SlowMemory memory, string id) => memory.HeldBack(Bob, new[] { id }).Single();

    /// <summary>A clock whose reading the test sets by hand, forward or back; it can also lower every reading by a fixed step.</summary>
    private sealed class HandClock(TimeSpan start) : TimeProvider
    {
        /// <summary>The reading the next read returns (before <see cref="FallPerReading"/> is taken off).</summary>
        public TimeSpan Now { get; set; } = start;

        /// <summary>When above zero, each read first lowers <see cref="Now"/> by this much.</summary>
        public TimeSpan FallPerReading { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            Now -= FallPerReading;
            return Now.Ticks;
        }
    }
}
