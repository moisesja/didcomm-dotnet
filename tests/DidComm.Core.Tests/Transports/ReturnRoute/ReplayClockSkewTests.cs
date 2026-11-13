using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// #105 — the replay rule's own clock-skew tolerance, <see cref="ReplayProtectionOptions.ClockSkew"/>
/// (FR-RR-08 (1) and (4)): the default, the boundaries in whole seconds, the retention/age seam for a
/// <c>created_time</c> ahead of the clock, validation, and a value mutated after validation.
/// </summary>
public sealed class ReplayClockSkewTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Alice = "did:example:alice";
    private const string Noise = "did:example:noise";

    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void The_default_is_sixty_seconds()
    {
        new ReplayProtectionOptions().ClockSkew.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData(0, ReplayAdmission.Admitted)]
    [InlineData(1, ReplayAdmission.Admitted)]
    [InlineData(59, ReplayAdmission.Admitted)]
    [InlineData(60, ReplayAdmission.Admitted)]
    [InlineData(61, ReplayAdmission.FutureDated)]
    [InlineData(600, ReplayAdmission.FutureDated)]
    [InlineData(-300, ReplayAdmission.Admitted)]
    [InlineData(-301, ReplayAdmission.TooOld)]
    public void With_default_options_a_created_time_up_to_sixty_seconds_ahead_is_admitted(int offsetSeconds, ReplayAdmission expected)
    {
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions());

        guard.TryAdmit(Alice, "m", S(T) + offsetSeconds, T).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(30, 30)]
    [InlineData(60, 0)]
    [InlineData(60, 30)]
    [InlineData(60, 59)]
    [InlineData(60, 60)]
    [InlineData(-1, 0)]   // the default options object, untouched
    [InlineData(-1, 59)]
    [InlineData(-1, 60)]
    public void A_message_admitted_with_a_future_created_time_is_never_admitted_again(int skewSeconds, int createdAheadSeconds)
    {
        // FR-RR-08 (4) for created ahead of the clock: from before admission to past the retention,
        // swept second by second with a purge-triggering insert from another sender at each step, the
        // replay is FutureDated, then Duplicate until created + Window, then TooOld — never Admitted —
        // and the entry itself is held until created + Window + ClockSkew.
        var options = new ReplayProtectionOptions();
        if (skewSeconds >= 0)
            options.ClockSkew = TimeSpan.FromSeconds(skewSeconds);
        var skew = (long)options.ClockSkew.TotalSeconds;
        var window = (long)options.Window.TotalSeconds;
        var guard = new InMemoryReplayGuard(options);
        var created = S(T) + createdAheadSeconds;
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);

        for (var t = created - skew - 3; t <= created + window + skew + 3; t++)
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(t);
            guard.TryAdmit(Noise, "n" + t, t, now).Should().Be(ReplayAdmission.Admitted);
            var expected = t < created - skew ? ReplayAdmission.FutureDated
                : t > created + window ? ReplayAdmission.TooOld
                : ReplayAdmission.Duplicate;
            guard.TryAdmit(Alice, "m", created, now).Should().Be(expected, "at {0}s from created", t - created);
            HoldsAlice(guard).Should().Be(t <= created + window + skew, "the entry is kept until created + Window + ClockSkew inclusive ({0}s from created)", t - created);
        }
    }

    // The noise sender holds entries throughout; a second sender bucket means Alice's entry is held.
    private static bool HoldsAlice(InMemoryReplayGuard guard) => guard.PeerCount == 2;

    [Fact]
    public void ClockSkew_zero_restores_the_strict_future_bound_and_the_Window_only_retention()
    {
        var options = new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero };
        var guard = new InMemoryReplayGuard(options);

        guard.TryAdmit(Alice, "now", S(T), T).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "plus1", S(T) + 1, T).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Alice, "now", S(T), T.AddSeconds(300)).Should().Be(ReplayAdmission.Duplicate);
        guard.TryAdmit(Alice, "now", S(T), T.AddSeconds(301)).Should().Be(ReplayAdmission.TooOld);
        guard.TryAdmit(Noise, "purge", S(T) + 301, T.AddSeconds(301)).Should().Be(ReplayAdmission.Admitted);
        guard.Count.Should().Be(1, "at ClockSkew 0 the entry expired at created + Window");
    }

    [Fact]
    public void A_sub_second_skew_counts_as_zero()
    {
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromMilliseconds(999) });

        guard.TryAdmit(Alice, "now", S(T), T).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "plus1", S(T) + 1, T).Should().Be(ReplayAdmission.FutureDated);
    }

    [Fact]
    public void The_clock_is_judged_in_whole_seconds_so_a_fraction_past_the_bound_is_still_inside()
    {
        // ToUnixTimeSeconds floors: at T + 0.9 s the clock reads T, so created = T + 60 is inside.
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions());

        guard.TryAdmit(Alice, "edge", S(T) + 60, T.AddMilliseconds(900)).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "past", S(T) + 61, T.AddMilliseconds(900)).Should().Be(ReplayAdmission.FutureDated);
    }

    public static TheoryData<TimeSpan, bool> SkewValues => new()
    {
        { TimeSpan.Zero, true },
        { TimeSpan.FromMilliseconds(500), true },
        { TimeSpan.FromSeconds(60), true },
        { TimeSpan.FromSeconds(int.MaxValue), true },
        { TimeSpan.FromTicks(-1), false },
        { TimeSpan.FromSeconds(-1), false },
        { TimeSpan.MinValue, false },
        { TimeSpan.FromSeconds(int.MaxValue + 1L), false },
        { TimeSpan.MaxValue, false },
    };

    [Theory]
    [MemberData(nameof(SkewValues))]
    public void Validate_accepts_zero_to_int_MaxValue_seconds_and_refuses_the_rest(TimeSpan skew, bool valid)
    {
        var options = new ReplayProtectionOptions { ClockSkew = skew };

        var validate = () => options.Validate();
        var construct = () => new InMemoryReplayGuard(options);

        if (valid)
        {
            validate.Should().NotThrow();
            construct.Should().NotThrow();
        }
        else
        {
            validate.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(ReplayProtectionOptions.ClockSkew));
            construct.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(nameof(ReplayProtectionOptions.ClockSkew));
        }
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(3600)]
    public void Changing_ClockSkew_or_Window_after_construction_has_no_effect_and_leaves_no_gap(int changedTo)
    {
        // The guard copies the validated settings. Read live, a negative skew would end retention
        // before age rejection begins, and a raised Window would move age rejection past retention.
        var options = new ReplayProtectionOptions();
        var guard = new InMemoryReplayGuard(options);
        options.ClockSkew = TimeSpan.FromSeconds(changedTo);
        options.Window = TimeSpan.FromSeconds(300 + Math.Abs(changedTo));
        var created = S(T);
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "early", created + 61, T).Should().Be(ReplayAdmission.FutureDated, "still the 60 s it was built with");

        for (var t = created; t <= created + 360 + 3; t++)
        {
            var now = DateTimeOffset.FromUnixTimeSeconds(t);
            guard.TryAdmit(Noise, "n" + t, t, now);
            guard.TryAdmit(Alice, "m", created, now).Should().Be(t > created + 300 ? ReplayAdmission.TooOld : ReplayAdmission.Duplicate, "at {0}s", t - created);
        }
    }

    [Fact]
    public void The_largest_validated_settings_neither_overflow_nor_admit_a_replay()
    {
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { Window = TimeSpan.FromSeconds(int.MaxValue), ClockSkew = TimeSpan.FromSeconds(int.MaxValue) });
        var now = DateTimeOffset.MaxValue;
        var atBound = S(now) + int.MaxValue;
        guard.TryAdmit(Alice, "validated-max", atBound, now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "validated-max-plus1", atBound + 1, now).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Noise, "purge", S(now), now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "validated-max", atBound, now).Should().Be(ReplayAdmission.Duplicate, "a wrapped expiry would have been purged");
        guard.TryAdmit(Alice, "overflow", long.MaxValue, now).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Alice, "underflow", long.MinValue, DateTimeOffset.MinValue).Should().Be(ReplayAdmission.TooOld);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(60, 61)]
    [InlineData(60, 300)]
    [InlineData(60, 360)]
    public void Clock_readings_that_reach_the_guard_out_of_order_never_readmit_a_purged_entry(int skewSeconds, int inversionSeconds)
    {
        // The race the red-team pass found: the endpoint reads the clock before the guard's lock, so a
        // later reading (another sender's request) can purge first, and the replay, read earlier,
        // arrives at the edge of its window after its entry is gone. Equally a host clock stepping back.
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(skewSeconds) });
        var created = S(T);
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);

        // The replay read the clock at created + Window, the last second it is young enough at.
        var replayReading = created + 300;
        var purgeReading = replayReading + inversionSeconds;
        guard.TryAdmit(Noise, "later", purgeReading, DateTimeOffset.FromUnixTimeSeconds(purgeReading)).Should().Be(ReplayAdmission.Admitted);

        var replay = guard.TryAdmit(Alice, "m", created, DateTimeOffset.FromUnixTimeSeconds(replayReading));
        replay.Should().Be(purgeReading > created + 300 + skewSeconds ? ReplayAdmission.TooOld : ReplayAdmission.Duplicate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void A_reading_behind_the_latest_by_at_most_Window_plus_skew_still_admits_fresh_messages(int skewSeconds)
    {
        // The guarantee costs honest traffic nothing until the clock steps back by more than
        // Window + ClockSkew: a fresh message stamped at the earlier reading is admitted.
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(skewSeconds) });
        var latest = S(T);
        guard.TryAdmit(Noise, "latest", latest, T).Should().Be(ReplayAdmission.Admitted);

        var back = latest - 300 - skewSeconds;
        guard.TryAdmit(Alice, "fresh", back, DateTimeOffset.FromUnixTimeSeconds(back)).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "fresh-one-second-further", back - 1, DateTimeOffset.FromUnixTimeSeconds(back - 1)).Should().Be(ReplayAdmission.TooOld);
    }

    [Fact]
    public void Concurrent_admissions_of_one_future_dated_message_admit_exactly_one()
    {
        const int N = 16;
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions());
        using var start = new Barrier(N);
        var results = new ReplayAdmission[N];

        var threads = Enumerable.Range(0, N).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            results[i] = guard.TryAdmit(Alice, "m", S(T) + 59, T);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        results.Count(r => r == ReplayAdmission.Admitted).Should().Be(1);
        results.Count(r => r == ReplayAdmission.Duplicate).Should().Be(N - 1);
        guard.Count.Should().Be(1);
    }

    [Fact]
    public void Retention_of_a_message_stamped_at_the_future_bound_is_Window_plus_twice_the_skew_from_admission()
    {
        // The documented budget cost: an entry stamped now + ClockSkew is held until
        // now + Window + 2 × ClockSkew (420 s at the defaults) and is then purged.
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions());
        guard.TryAdmit(Alice, "ahead", S(T) + 60, T).Should().Be(ReplayAdmission.Admitted);

        guard.TryAdmit(Noise, "a", S(T) + 420, T.AddSeconds(420)).Should().Be(ReplayAdmission.Admitted);
        guard.PeerCount.Should().Be(2, "still held at admission + Window + 2 × ClockSkew");
        guard.TryAdmit(Noise, "b", S(T) + 421, T.AddSeconds(421)).Should().Be(ReplayAdmission.Admitted);
        guard.PeerCount.Should().Be(1, "purged one second later");
    }
}
