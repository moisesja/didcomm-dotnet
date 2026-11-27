using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="SlowMemory"/> (#111, I-9..I-12, D-2, D-3, D-8): the pause doubles up to its cap; a clock
/// reading below the strike's is due; each partition evicts only its own least recently seen entry;
/// the least recently used partition goes; and the key holds the mediator, the mailbox and the
/// attachment id exactly. Clocks are manual or set by hand; nothing sleeps.
/// </summary>
public sealed class SlowMemoryTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(40);
    private static readonly DeliveryScope Bob = new("did:example:mediator", "did:example:bob");

    // ---- M1: the pause doubles, up to the cap ----

    [Fact]
    public void M1_the_pause_doubles_with_each_strike_up_to_the_cap()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);

        memory.PauseAfter(1).Should().Be(T0);
        memory.PauseAfter(2).Should().Be(2 * T0);
        memory.PauseAfter(3).Should().Be(Cap);
        memory.PauseAfter(4).Should().Be(Cap);
        memory.PauseAfter(int.MaxValue).Should().Be(Cap);

        // Through the strikes themselves: each pause runs from the last strike.
        var expected = new[] { T0, 2 * T0, Cap, Cap };
        foreach (var pause in expected)
        {
            memory.Strike(Bob, "q1");
            Held(memory, "q1").Should().BeTrue("the pause has just started");
            time.Advance(pause - TimeSpan.FromTicks(1));
            Held(memory, "q1").Should().BeTrue("one tick before the pause of {0} ends", pause);
            time.Advance(TimeSpan.FromTicks(1));
            Held(memory, "q1").Should().BeFalse("the pause of {0} has ended", pause);
        }
    }

    [Fact]
    public void A_strike_while_the_message_is_still_paused_adds_nothing_and_one_after_the_pause_doubles_it()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, "q1");
        time.Advance(T0 / 2);
        memory.Strike(Bob, "q1"); // the same slow episode, seen by a delivery that ran at the same time

        time.Advance(T0 / 2);
        Held(memory, "q1").Should().BeFalse("one episode is one strike: the first pause, from the first strike, has ended");

        memory.Strike(Bob, "q1"); // slow again after its pause
        time.Advance(2 * T0 - TimeSpan.FromTicks(1));
        Held(memory, "q1").Should().BeTrue("the second episode doubles the pause");
        time.Advance(TimeSpan.FromTicks(1));
        Held(memory, "q1").Should().BeFalse();
    }

    // ---- M2: a reading below the strike's is due; one pause after the strike's reading it is due too ----

    [Fact]
    public void M2_a_reading_below_the_strikes_is_due_and_a_stepped_back_clock_never_holds_back_past_the_pause()
    {
        var clock = new SetClock(TimeSpan.FromHours(5));
        var memory = new SlowMemory(clock, T0, Cap, q: 4, r: 4);

        memory.Strike(Bob, "q1");
        Held(memory, "q1").Should().BeTrue("the control: a strike holds the message back");

        clock.Now -= TimeSpan.FromSeconds(1);
        Held(memory, "q1").Should().BeFalse("a reading one second below the strike's counts as due");
        clock.Now -= TimeSpan.FromHours(2);
        Held(memory, "q1").Should().BeFalse("and so does one two hours below it");

        // Back where the strike was: held back again. One pause after the strike's reading: due.
        clock.Now = TimeSpan.FromHours(5);
        Held(memory, "q1").Should().BeTrue();
        clock.Now = TimeSpan.FromHours(5) + T0;
        Held(memory, "q1").Should().BeFalse();
    }

    // ---- M3, M4: eviction inside a partition ----

    [Fact]
    public void M3_churn_in_one_mailbox_evicts_only_that_mailboxs_entries()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 8);
        var carol = new DeliveryScope("did:example:mediator", "did:example:carol");
        var kept = Enumerable.Range(0, 4).Select(i => "bob-" + i).ToArray();
        foreach (var id in kept)
            memory.Strike(Bob, id);

        for (var i = 0; i < 100; i++)
            memory.Strike(carol, "carol-" + i);

        memory.HeldBack(Bob, kept).Should().OnlyContain(h => h, "Carol's churn evicts only Carol's entries");
        memory.Count.Should().Be(8, "four per partition");
        memory.HeldBack(carol, Enumerable.Range(96, 4).Select(i => (string?)("carol-" + i)).ToArray()).Should().OnlyContain(h => h, "Carol keeps her four most recent");
    }

    [Fact]
    public void M4_an_entry_seen_in_a_delivery_outlives_one_struck_after_it_but_seen_less_recently()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);
        for (var i = 0; i < 4; i++)
            memory.Strike(Bob, "q" + i);

        // q0 is delivered again (and held back): it is now the most recently seen.
        Held(memory, "q0").Should().BeTrue();
        memory.Strike(Bob, "q4");

        Held(memory, "q0").Should().BeTrue("seen most recently, it is kept");
        Held(memory, "q1").Should().BeFalse("the least recently seen entry is the one evicted");
        memory.Count.Should().Be(4);
    }

    // ---- M5: eviction of whole partitions ----

    [Fact]
    public void M5_one_partition_more_than_the_bound_drops_the_least_recently_used_one()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 3);
        var scopes = Enumerable.Range(0, 4).Select(i => new DeliveryScope("did:example:mediator", "did:example:local-" + i)).ToArray();
        for (var i = 0; i < 3; i++)
            memory.Strike(scopes[i], "q1");

        // Mailbox 0 receives a delivery: its partition is now the most recently used.
        memory.HeldBack(scopes[0], new[] { "q1" }).Single().Should().BeTrue();
        memory.Strike(scopes[3], "q1");

        memory.Partitions.Should().Be(3);
        memory.HeldBack(scopes[0], new[] { "q1" }).Single().Should().BeTrue("used most recently, it is kept");
        memory.HeldBack(scopes[1], new[] { "q1" }).Single().Should().BeFalse("the least recently used partition is the one dropped");
        memory.HeldBack(scopes[2], new[] { "q1" }).Single().Should().BeTrue();
        memory.HeldBack(scopes[3], new[] { "q1" }).Single().Should().BeTrue();
    }

    // ---- M6 and D-8: the key ----

    [Fact]
    public void M6_the_mediator_and_the_mailbox_are_in_the_key()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, "q1");

        Held(memory, "q1").Should().BeTrue();
        memory.HeldBack(new DeliveryScope("did:example:other-mediator", Bob.LocalDid), new[] { "q1" }).Single()
            .Should().BeFalse("another mediator's attachment id q1 is another message");
        memory.HeldBack(new DeliveryScope(Bob.MediatorDid, "did:example:carol"), new[] { "q1" }).Single()
            .Should().BeFalse("another mailbox's q1 is another message");
    }

    [Fact]
    public void Attachment_ids_compare_exactly_and_a_null_id_is_never_held_back_or_remembered()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, "Msg-1");
        memory.Strike(Bob, null);

        memory.HeldBack(Bob, new[] { "Msg-1", "msg-1", "MSG-1", " Msg-1", null }).Should().Equal(true, false, false, false, false);
        memory.Count.Should().Be(1);
    }

    [Fact]
    public void A_message_that_opens_is_forgotten_and_an_empty_partition_goes_with_it()
    {
        var time = new ManualTimeProvider();
        var memory = new SlowMemory(time, T0, Cap, q: 4, r: 4);
        memory.Strike(Bob, "q1");
        memory.Strike(Bob, "q2");

        memory.Forget(Bob, "q1");
        memory.Count.Should().Be(1);
        memory.Partitions.Should().Be(1);
        memory.Forget(Bob, "q2");
        memory.Forget(Bob, "unknown");
        memory.Forget(Bob, null);

        memory.Count.Should().Be(0);
        memory.Partitions.Should().Be(0);
        Held(memory, "q1").Should().BeFalse();
    }

    [Fact]
    public void Asking_about_a_delivery_creates_nothing()
    {
        var memory = new SlowMemory(new ManualTimeProvider(), T0, Cap, q: 4, r: 4);

        memory.HeldBack(Bob, Enumerable.Range(0, 100).Select(i => (string?)("q" + i)).ToArray()).Should().OnlyContain(h => !h);

        memory.Count.Should().Be(0);
        memory.Partitions.Should().Be(0);
    }

    private static bool Held(SlowMemory memory, string id) => memory.HeldBack(Bob, new[] { id }).Single();

    /// <summary>A clock whose reading the test sets by hand, forward or back.</summary>
    private sealed class SetClock(TimeSpan start) : TimeProvider
    {
        public TimeSpan Now { get; set; } = start;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now.Ticks;
    }
}
