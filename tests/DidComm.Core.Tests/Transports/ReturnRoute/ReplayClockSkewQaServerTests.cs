using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// #105 QA pass (server side), kept as regressions: independent tests of the FR-RR-08 guard's own clock skew,
/// <see cref="ReplayProtectionOptions.ClockSkew"/>. Boundaries in whole seconds for many skews and
/// windows, sub-second clock readings and skews, clock advancement second by second through and past
/// retention (monotonic, backwards, forward-then-back), the retention / age-rejection seam, a
/// randomized comparison against an independent model of the documented rule, concurrency, and
/// validation. Three tests began as probes of defects that predate #105 (the same results on
/// <c>main</c>) and now pin their fix: clock readings that reach the guard out of order, a clock that
/// steps back after a purge, and settings changed after construction.
/// </summary>
public sealed class ReplayClockSkewQaServerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string Alice = "did:example:qa-alice";
    private const string Noise = "did:example:qa-noise";

    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();

    private static DateTimeOffset At(long epochSeconds, int milliseconds = 0)
        => DateTimeOffset.FromUnixTimeSeconds(epochSeconds).AddMilliseconds(milliseconds);

    private static InMemoryReplayGuard Guard(long skewSeconds, long windowSeconds = 300)
        => new(new ReplayProtectionOptions
        {
            ClockSkew = TimeSpan.FromSeconds(skewSeconds),
            Window = TimeSpan.FromSeconds(windowSeconds),
        });

    public static TheoryData<long, long> SkewsAndWindows()
    {
        var data = new TheoryData<long, long>();
        foreach (var skew in new long[] { 0, 1, 2, 59, 60, 61, 3600, 365L * 86400, int.MaxValue })
            foreach (var window in new long[] { 1, 2, 300, int.MaxValue })
                data.Add(skew, window);
        return data;
    }

    // ---- 1. boundaries -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(SkewsAndWindows))]
    public void Every_edge_in_whole_seconds_with_any_sub_second_reading(long skew, long window)
    {
        var guard = Guard(skew, window);
        var n = 0;
        foreach (var ms in new[] { 0, 1, 499, 500, 999 })
        {
            var now = T0.AddMilliseconds(ms);
            var nowS = S(T0);
            S(now).Should().Be(nowS, "the reading floors to the second");

            ReplayAdmission Admit(long created) => guard.TryAdmit(Alice, "m" + n++, created, now);

            Admit(nowS + skew + 1).Should().Be(ReplayAdmission.FutureDated, "now + ClockSkew + 1 (skew {0}, {1} ms)", skew, ms);
            Admit(nowS + skew).Should().Be(ReplayAdmission.Admitted, "now + ClockSkew is inclusive (skew {0}, {1} ms)", skew, ms);
            if (skew >= 1)
                Admit(nowS + skew - 1).Should().Be(ReplayAdmission.Admitted);
            Admit(nowS).Should().Be(ReplayAdmission.Admitted);
            Admit(nowS - window + 1).Should().Be(ReplayAdmission.Admitted);
            Admit(nowS - window).Should().Be(ReplayAdmission.Admitted, "now − Window is inclusive (window {0}, {1} ms)", window, ms);
            Admit(nowS - window - 1).Should().Be(ReplayAdmission.TooOld, "now − Window − 1 (window {0}, {1} ms)", window, ms);
        }
    }

    [Theory]
    [InlineData(1L, 0)]                          // 1 tick
    [InlineData(5_000_000L, 0)]                  // 0.5 s
    [InlineData(9_999_999L, 0)]                  // 1 s − 1 tick
    [InlineData(10_000_000L, 1)]                 // 1 s
    [InlineData(19_999_999L, 1)]                 // 2 s − 1 tick
    [InlineData(599_999_999L, 59)]               // 60 s − 1 tick
    [InlineData(600_000_000L, 60)]               // 60 s
    [InlineData(605_000_000L, 60)]               // 60.5 s
    public void A_sub_second_skew_is_floored_to_whole_seconds(long skewTicks, long effectiveSeconds)
    {
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromTicks(skewTicks) });
        foreach (var ms in new[] { 0, 400, 999 })
        {
            var now = T0.AddMilliseconds(ms);
            guard.TryAdmit(Alice, "in" + ms, S(T0) + effectiveSeconds, now).Should().Be(ReplayAdmission.Admitted);
            guard.TryAdmit(Alice, "out" + ms, S(T0) + effectiveSeconds + 1, now).Should().Be(ReplayAdmission.FutureDated,
                "a fraction of skew never adds a second, even with a fractional clock reading ({0} ms)", ms);
        }
    }

    [Fact]
    public void A_clock_reading_one_tick_before_a_second_boundary_still_reads_the_earlier_second()
    {
        var guard = Guard(60);
        var justBefore = T0.AddTicks(-1);              // reads S(T0) − 1
        var justAt = T0;                               // reads S(T0)
        guard.TryAdmit(Alice, "a", S(T0) + 59, justBefore).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "b", S(T0) + 60, justBefore).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Alice, "c", S(T0) + 60, justAt).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "d", S(T0) - 301, justBefore).Should().Be(ReplayAdmission.Admitted, "now − Window with now = S(T0) − 1");
        guard.TryAdmit(Alice, "e", S(T0) - 301, justAt).Should().Be(ReplayAdmission.TooOld);
    }

    [Fact]
    public void Readings_before_the_Unix_epoch_floor_as_well()
    {
        // 1969-12-31T23:59:59.5Z reads −1 (floor), not 0 (truncation).
        var guard = Guard(0, 1);
        var now = DateTimeOffset.FromUnixTimeSeconds(0).AddMilliseconds(-500);
        guard.TryAdmit(Alice, "a", -1, now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "b", 0, now).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Alice, "c", -2, now).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "d", -3, now).Should().Be(ReplayAdmission.TooOld);
    }

    [Fact]
    public void The_default_admits_one_to_sixty_seconds_ahead_that_ClockSkew_zero_refuses()
    {
        // The bug being fixed: a peer 1–59 s ahead was FutureDated at the old default (0).
        var fixedGuard = new InMemoryReplayGuard(new ReplayProtectionOptions());
        var strictGuard = Guard(0);
        for (var ahead = 1; ahead <= 61; ahead++)
        {
            fixedGuard.TryAdmit(Alice, "m" + ahead, S(T0) + ahead, T0.AddMilliseconds(250))
                .Should().Be(ahead <= 60 ? ReplayAdmission.Admitted : ReplayAdmission.FutureDated, "{0} s ahead", ahead);
            strictGuard.TryAdmit(Alice, "m" + ahead, S(T0) + ahead, T0.AddMilliseconds(250))
                .Should().Be(ReplayAdmission.FutureDated, "{0} s ahead at ClockSkew 0", ahead);
        }
    }

    // ---- 2./3. clock advancement and the retention / age seam -----------------------------------

    public static TheoryData<long, long, long> Lifecycles()
    {
        var data = new TheoryData<long, long, long>();
        foreach (var skew in new long[] { 0, 1, 2, 59, 60, 61 })
            foreach (var window in new long[] { 1, 2, 5, 300 })
                foreach (var offset in new[] { -window, -1, 0, 1, skew - 1, skew }.Where(o => o >= -window && o <= skew).Distinct())
                    data.Add(skew, window, offset);
        return data;
    }

    [Theory]
    [MemberData(nameof(Lifecycles))]
    public void Swept_second_by_second_an_admitted_message_is_never_admitted_again_and_is_held_until_age_rejection_starts(long skew, long window, long createdOffset)
    {
        var guard = Guard(skew, window);
        var admittedAt = S(T0);
        var created = admittedAt + createdOffset;
        guard.TryAdmit(Alice, "m", created, T0.AddMilliseconds(737)).Should().Be(ReplayAdmission.Admitted);

        var lastHeld = long.MinValue;
        var firstTooOld = long.MaxValue;
        for (var t = admittedAt; t <= created + window + skew + 3; t++)
        {
            foreach (var ms in new[] { 0, 999 })
            {
                var now = At(t, ms);
                // Another sender's traffic at this instant drives the purge (always admissible: created = now).
                guard.TryAdmit(Noise, $"n{t}.{ms}", t, now).Should().Be(ReplayAdmission.Admitted);
                var held = guard.PeerCount == 2;
                var replay = guard.TryAdmit(Alice, "m", created, now);

                replay.Should().NotBe(ReplayAdmission.Admitted, "a replay at {0}s from created (skew {1}, window {2})", t - created, skew, window);
                replay.Should().Be(t > created + window ? ReplayAdmission.TooOld : ReplayAdmission.Duplicate, "at {0}s from created", t - created);
                if (held)
                    lastHeld = t;
                if (replay == ReplayAdmission.TooOld)
                    firstTooOld = Math.Min(firstTooOld, t);
                if (!held)
                    replay.Should().Be(ReplayAdmission.TooOld, "the seam: an entry no longer held must already be too old ({0}s from created)", t - created);
            }
        }

        lastHeld.Should().Be(created + window + skew, "held until created + Window + ClockSkew inclusive");
        firstTooOld.Should().Be(created + window + 1, "age rejection starts once now > created + Window");
        (lastHeld + 1).Should().BeGreaterThanOrEqualTo(firstTooOld, "no instant at which it is neither held nor too old");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(60L)]
    public void The_clock_moving_backwards_while_the_entry_is_held_never_readmits(long skew)
    {
        var guard = Guard(skew, 300);
        var created = S(T0) + skew;
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);

        // Walk forward to the last held second, then back second by second to well before admission.
        for (var t = S(T0); t <= created + 300 + skew; t++)
        {
            guard.TryAdmit(Noise, "f" + t, t, At(t));
            guard.TryAdmit(Alice, "m", created, At(t)).Should().NotBe(ReplayAdmission.Admitted);
        }
        for (var t = created + 300 + skew; t >= S(T0) - 400; t--)
        {
            guard.TryAdmit(Noise, "b" + t, t, At(t));
            var replay = guard.TryAdmit(Alice, "m", created, At(t, 999));
            replay.Should().NotBe(ReplayAdmission.Admitted, "the clock moved back to {0}s from created", t - created);
            replay.Should().Be(t > created + 300 ? ReplayAdmission.TooOld
                : t < created - skew ? ReplayAdmission.FutureDated
                : ReplayAdmission.Duplicate);
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(60L)]
    public void A_backward_step_of_more_than_ClockSkew_after_a_normal_purge_does_not_readmit_the_replay(long skew)
    {
        // Was a defect probe (also on main): the clock runs forward normally until other traffic purges
        // the entry at created + Window + ClockSkew + 1; then it steps back by ClockSkew + 1 seconds (an
        // NTP step, or two in-flight requests whose readings reach the lock out of order). The replay
        // used to be neither held nor too old.
        var guard = Guard(skew, 300);
        var created = S(T0);
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);

        var purgeAt = created + 300 + skew + 1;
        guard.TryAdmit(Noise, "purge", purgeAt, At(purgeAt)).Should().Be(ReplayAdmission.Admitted);
        guard.PeerCount.Should().Be(1, "the entry was purged by ordinary forward progress");

        var back = purgeAt - (skew + 1);                // = created + Window: not yet too old
        guard.TryAdmit(Alice, "m", created, At(back)).Should().NotBe(ReplayAdmission.Admitted,
            "a backward step of ClockSkew + 1 = {0} s after the purge must not re-admit a captured request", skew + 1);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(60L)]
    public void A_backward_step_of_at_most_ClockSkew_after_a_purge_cannot_readmit(long skew)
    {
        // The other half of the characterization: a step of ClockSkew or less after any purge is safe.
        for (var step = 0L; step <= skew; step++)
        {
            var guard = Guard(skew, 300);
            var created = S(T0);
            guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
            for (var purgeAt = created + 300 + skew + 1; purgeAt <= created + 300 + skew + 3; purgeAt++)
            {
                guard.TryAdmit(Noise, "p" + purgeAt, purgeAt, At(purgeAt));
                guard.TryAdmit(Alice, "m", created, At(purgeAt - step)).Should().Be(ReplayAdmission.TooOld, "step {0} after a purge at {1}", step, purgeAt - created);
            }
        }
    }

    [Fact]
    public void A_forward_jump_past_retention_and_back_refuses_the_replay_and_fresh_messages_until_the_clock_catches_up()
    {
        // The W-1 limitation of #91 (forward jump past retention, then back) is closed; the price is
        // that, back at T0, messages stamped then are refused until the clock is within
        // Window + ClockSkew of the jump's instant again (here: 3600 - 360 seconds later).
        var guard = Guard(60, 300);
        var created = S(T0) + 60;
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Noise, "jump", S(T0) + 3600, At(S(T0) + 3600)).Should().Be(ReplayAdmission.Admitted);
        guard.PeerCount.Should().Be(1);
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.TooOld, "it may have been forgotten");
        guard.TryAdmit(Alice, "fresh", S(T0), T0).Should().Be(ReplayAdmission.TooOld, "availability, never integrity");
        var caughtUp = S(T0) + 3600 - 360;
        guard.TryAdmit(Alice, "fresh-later", caughtUp, At(caughtUp)).Should().Be(ReplayAdmission.Admitted);
    }

    [Fact]
    public void A_forward_jump_within_retention_and_back_keeps_the_entry()
    {
        var guard = Guard(60, 300);
        var created = S(T0) + 60;
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Noise, "jump", created + 360, At(created + 360)).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "m", created, At(created + 360)).Should().Be(ReplayAdmission.TooOld);
        guard.PeerCount.Should().Be(2, "created + Window + ClockSkew is still held");
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Duplicate);
    }

    [Fact]
    public void Skew_lowered_or_raised_on_the_options_after_admission_never_opens_a_gap()
    {
        foreach (var (before, after) in new[] { (60L, 0L), (0L, 60L), (60L, 3600L), (3600L, 0L), (60L, -60L) })
        {
            var options = new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(before) };
            var guard = new InMemoryReplayGuard(options);
            var created = S(T0) + before;
            guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
            options.ClockSkew = TimeSpan.FromSeconds(after);
            for (var t = S(T0) - 2; t <= created + 300 + Math.Max(before, after) + 3; t++)
            {
                guard.TryAdmit(Noise, "n" + t, t, At(t));
                guard.TryAdmit(Alice, "m", created, At(t)).Should().NotBe(ReplayAdmission.Admitted, "skew {0} → {1}, at {2}s from created", before, after, t - created);
            }
        }
    }

    [Fact]
    public void Window_raised_on_the_options_after_admission_does_not_open_a_gap()
    {
        // Was a defect probe (also on main): a Window raised after admission kept retention at
        // created + oldWindow + skew while age rejection moved to created + newWindow. The guard now
        // copies its settings when it is built.
        var options = new ReplayProtectionOptions { Window = TimeSpan.FromSeconds(300) };
        var guard = new InMemoryReplayGuard(options);
        var created = S(T0);
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
        options.Window = TimeSpan.FromSeconds(600);

        var readmitted = new List<long>();
        for (var t = created; t <= created + 610; t++)
        {
            guard.TryAdmit(Noise, "n" + t, t, At(t));
            if (guard.TryAdmit(Alice, "m", created, At(t)) == ReplayAdmission.Admitted)
                readmitted.Add(t - created);
        }
        readmitted.Should().BeEmpty("a captured request must be admitted at most once");
    }

    // ---- model comparison: random walks of the clock, senders, ids, timestamps ------------------

    /// <summary>The documented FR-RR-08 rule, written from the XML docs, independent of the guard's code.</summary>
    private sealed class Model
    {
        private readonly Dictionary<(string Peer, string Id), long> _entries = new();
        private long _purgedAt = long.MinValue;

        public required ReplayProtectionOptions Options { get; init; }

        public int Count => _entries.Count;

        public int PeerCount => _entries.Keys.Select(k => k.Peer).Distinct().Count();

        private static long FloorSeconds(DateTimeOffset t)
        {
            var ticks = t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
            var q = ticks / TimeSpan.TicksPerSecond;
            return ticks % TimeSpan.TicksPerSecond < 0 ? q - 1 : q;
        }

        private static long FloorSeconds(TimeSpan t)
        {
            var q = t.Ticks / TimeSpan.TicksPerSecond;
            return t.Ticks % TimeSpan.TicksPerSecond < 0 ? q - 1 : q;
        }

        public ReplayAdmission Admit(string peer, string id, long? created, DateTimeOffset now)
        {
            if (id.Length > Options.MaxMessageIdLength)
                return ReplayAdmission.MessageIdTooLong;
            var nowS = FloorSeconds(now);
            var w = FloorSeconds(Options.Window);
            var s = Math.Max(0, FloorSeconds(Options.ClockSkew));
            if (created is long c)
            {
                if (c < nowS - w)
                    return ReplayAdmission.TooOld;
                if (c > nowS + s)
                    return ReplayAdmission.FutureDated;
            }
            else if (Options.RequireCreatedTime)
            {
                return ReplayAdmission.MissingCreatedTime;
            }

            // The latest purge instant: a reading behind it purges nothing more.
            _purgedAt = Math.Max(_purgedAt, nowS);
            foreach (var key in _entries.Where(e => e.Value < _purgedAt).Select(e => e.Key).ToList())
                _entries.Remove(key);
            long expiry;
            if (created is long stamped)
            {
                expiry = stamped + w + s;
                if (expiry < _purgedAt)
                    return ReplayAdmission.TooOld; // may have been admitted and forgotten
            }
            else
            {
                expiry = _purgedAt + w;
            }
            if (_entries.ContainsKey((peer, id)))
                return ReplayAdmission.Duplicate;
            if (_entries.Count >= Options.MaxEntries || _entries.Keys.Count(k => k.Peer == peer) >= Options.MaxEntriesPerPeer)
                return ReplayAdmission.Saturated;
            _entries[(peer, id)] = expiry;
            return ReplayAdmission.Admitted;
        }
    }

    [Theory]
    [InlineData(1, 0L, 5L, true, false)]
    [InlineData(2, 1L, 5L, true, false)]
    [InlineData(3, 3L, 7L, true, false)]
    [InlineData(4, 60L, 300L, true, false)]
    [InlineData(5, 3L, 7L, false, false)]
    [InlineData(6, 0L, 5L, true, true)]
    [InlineData(7, 3L, 7L, true, true)]
    [InlineData(8, 60L, 300L, false, true)]
    public void Random_walks_match_the_documented_rule_step_for_step(int seed, long skew, long window, bool requireCreated, bool monotonic)
    {
        var rng = new Random(seed);
        ReplayProtectionOptions NewOptions() => new()
        {
            ClockSkew = TimeSpan.FromSeconds(skew),
            Window = TimeSpan.FromSeconds(window),
            RequireCreatedTime = requireCreated,
            MaxEntries = 4,
            MaxEntriesPerPeer = 2,
        };
        var guard = new InMemoryReplayGuard(NewOptions());
        var model = new Model { Options = NewOptions() };
        var peers = new[] { "did:example:p0", "did:example:p1", "did:example:p2" };
        var createdOf = new Dictionary<(string, string), long?>();
        var admittedTimestamped = new HashSet<(string, string)>();
        var nowTicks = T0.AddMilliseconds(rng.Next(1000)).UtcTicks;

        for (var step = 0; step < 6000; step++)
        {
            // Mostly small steps; sometimes a jump past retention; backwards only when not monotonic.
            var r = rng.Next(100);
            long dtSeconds = r < 85 ? rng.Next(0, 3) : r < 95 ? rng.Next((int)(window + skew - 2), (int)(window + skew + 3)) : -rng.Next(1, (int)(window + skew + 3));
            var delta = dtSeconds * TimeSpan.TicksPerSecond + rng.Next(-5_000_000, 5_000_000);
            if (monotonic)
                delta = Math.Abs(delta);
            nowTicks += delta;
            var now = new DateTimeOffset(nowTicks, TimeSpan.Zero);
            var peer = peers[rng.Next(peers.Length)];
            var id = "m" + rng.Next(10);
            var key = (peer, id);
            if (!createdOf.TryGetValue(key, out var created) || rng.Next(3) == 0)
            {
                // A fresh message under this key (or a sender reusing an id): timestamp around the clock.
                created = rng.Next(8) == 0 ? (long?)null : now.ToUnixTimeSeconds() + rng.Next((int)-window - 3, (int)skew + 4);
                createdOf[key] = created;
            }

            var expected = model.Admit(peer, id, created, now);
            var actual = guard.TryAdmit(peer, id, created, now);
            actual.Should().Be(expected, "step {0}: {1}/{2} created {3} at {4:O}", step, peer, id, created, now);
            guard.Count.Should().Be(model.Count, "step {0}", step);
            guard.PeerCount.Should().Be(model.PeerCount, "step {0}", step);

            // Independent of the model, on every walk (monotonic or not): a timestamped (sender, id,
            // created) is admitted once.
            if (actual == ReplayAdmission.Admitted && created is long c)
                admittedTimestamped.Add((peer, id + "@" + c)).Should().BeTrue("step {0}: replay of {1}/{2} created {3} admitted twice", step, peer, id, c);
            seen[actual] = seen.GetValueOrDefault(actual) + 1;
        }

        // The walk must actually reach every outcome it can, or the comparison proves little.
        var reachable = new List<ReplayAdmission> { ReplayAdmission.Admitted, ReplayAdmission.Duplicate, ReplayAdmission.TooOld, ReplayAdmission.FutureDated, ReplayAdmission.Saturated };
        if (requireCreated)
            reachable.Add(ReplayAdmission.MissingCreatedTime);
        seen.Keys.Should().Contain(reachable);
        seen[ReplayAdmission.Duplicate].Should().BeGreaterThan(20);
        seen[ReplayAdmission.Admitted].Should().BeGreaterThan(300);
    }

    private readonly Dictionary<ReplayAdmission, int> seen = new();

    // ---- 6. concurrency -------------------------------------------------------------------------

    [Theory]
    [InlineData(1L)]
    [InlineData(60L)]
    [InlineData(int.MaxValue)]
    public void Many_concurrent_admissions_of_one_future_dated_message_admit_exactly_one(long skew)
    {
        const int Threads = 48;
        for (var round = 0; round < 25; round++)
        {
            var guard = Guard(skew);
            var results = new ReplayAdmission[Threads];
            using var start = new Barrier(Threads);
            var threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                // Different sub-second readings of the same second; created exactly at the bound.
                results[i] = guard.TryAdmit(Alice, "m", S(T0) + skew, T0.AddMilliseconds(i * 20));
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            results.Count(r => r == ReplayAdmission.Admitted).Should().Be(1, "round {0}", round);
            results.Count(r => r == ReplayAdmission.Duplicate).Should().Be(Threads - 1);
            guard.Count.Should().Be(1);
        }
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(60L)]
    public void Concurrent_replays_and_purges_at_the_boundary_with_readings_within_the_skew_never_readmit(long skew)
    {
        // Replays read the clock at created + Window (the last second before age rejection); other
        // senders' traffic reads it up to ClockSkew seconds later and purges. Any interleaving of
        // readings within ClockSkew of each other keeps the entry, so no replay is admitted.
        const int Threads = 32;
        for (var round = 0; round < 25; round++)
        {
            var guard = Guard(skew, 300);
            var created = S(T0);
            guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
            var results = new ReplayAdmission[Threads];
            using var start = new Barrier(Threads);
            var threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                if (i % 2 == 0)
                {
                    results[i] = guard.TryAdmit(Alice, "m", created, At(created + 300, 999));
                }
                else
                {
                    var late = created + 300 + skew;      // the latest reading still inside the skew of the replays'
                    results[i] = guard.TryAdmit(Noise, "n" + i, late, At(late, 999));
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            results.Where((_, i) => i % 2 == 0).Should().AllSatisfy(r => r.Should().Be(ReplayAdmission.Duplicate), "round {0}", round);
        }
    }

    [Fact]
    public void Concurrent_readings_more_than_ClockSkew_apart_do_not_readmit_at_ClockSkew_zero()
    {
        // Was a defect probe (also on main at its default ExpiresClockSkew = 0): two in-flight
        // requests read the clock one second apart around created + Window, and the later reading
        // reaches the lock first and purges. This is the deterministic interleaving.
        var guard = Guard(0, 300);
        var created = S(T0);
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);

        var earlyReading = At(created + 300, 999);     // replay copy: reads the clock first...
        var lateReading = At(created + 301, 0);        // ...any other request reads it 1 ms later
        guard.TryAdmit(Noise, "other", created + 301, lateReading).Should().Be(ReplayAdmission.Admitted, "reaches the lock first and purges");
        guard.TryAdmit(Alice, "m", created, earlyReading).Should().NotBe(ReplayAdmission.Admitted,
            "the replay's clock read 1 ms before the purge's; it must not be admitted a second time");
    }

    [Fact]
    public void Mutating_ClockSkew_concurrently_with_admissions_throws_nothing_and_never_double_admits()
    {
        var options = new ReplayProtectionOptions();
        var guard = new InMemoryReplayGuard(options);
        var skews = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(-60), TimeSpan.MaxValue, TimeSpan.MinValue, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(30) };
        using var stop = new CancellationTokenSource();
        var mutator = new Thread(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
                options.ClockSkew = skews[i++ % skews.Length];
        });
        mutator.Start();

        const int Threads = 16;
        const int Messages = 400;
        var admitted = new int[Messages];
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(Threads);
        var threads = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            for (var m = 0; m < Messages; m++)
            {
                try
                {
                    // Even m: created = now (admissible at any skew). Odd m: 30 s ahead (only when skew ≥ 30).
                    var created = S(T0) + (m % 2 == 0 ? 0 : 30);
                    var result = guard.TryAdmit(Alice, "m" + m, created, T0);
                    if (result == ReplayAdmission.Admitted)
                        Interlocked.Increment(ref admitted[m]);
                    else if (m % 2 == 0)
                        result.Should().Be(ReplayAdmission.Duplicate);
                    else
                        result.Should().BeOneOf(ReplayAdmission.Duplicate, ReplayAdmission.FutureDated);
                }
                catch (Exception ex)
                {
                    faults.Enqueue(ex);
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        stop.Cancel();
        mutator.Join();

        faults.Should().BeEmpty();
        admitted.Where((_, m) => m % 2 == 0).Should().AllSatisfy(n => n.Should().Be(1));
        admitted.Should().AllSatisfy(n => n.Should().BeLessThanOrEqualTo(1));
    }

    // ---- 7. validation --------------------------------------------------------------------------

    public static TheoryData<TimeSpan, bool> ValidationCases() => new()
    {
        { TimeSpan.MinValue, false },
        { TimeSpan.FromDays(-1), false },
        { TimeSpan.FromSeconds(-1), false },
        { TimeSpan.FromTicks(-1), false },
        { TimeSpan.Zero, true },
        { TimeSpan.FromTicks(1), true },
        { TimeSpan.FromMilliseconds(999), true },
        { TimeSpan.FromSeconds(60), true },
        { TimeSpan.FromSeconds(int.MaxValue), true },
        { TimeSpan.FromSeconds(int.MaxValue) + TimeSpan.FromMilliseconds(500), false },
        { TimeSpan.FromSeconds(int.MaxValue + 1L), false },
        { TimeSpan.FromDays(100_000), false },
        { TimeSpan.MaxValue, false },
    };

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void Validate_names_ClockSkew_and_never_echoes_the_value(TimeSpan skew, bool valid)
    {
        var options = new ReplayProtectionOptions { ClockSkew = skew };
        Action validate = options.Validate;
        Func<InMemoryReplayGuard> construct = () => new InMemoryReplayGuard(options, logger: null);

        if (valid)
        {
            validate.Should().NotThrow();
            construct.Should().NotThrow();
            return;
        }

        foreach (var act in new Action[] { validate, () => { construct(); } })
        {
            var message = act.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain("ReplayProtectionOptions.ClockSkew");
            message.Should().NotContain(skew.ToString());
            message.Should().NotContain(skew.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            message.Should().NotMatchRegex("[0-9]", "the value is never echoed");
        }
    }

    [Fact]
    public void Validate_still_checks_Window_first_and_the_other_bounds_after_ClockSkew()
    {
        var both = new ReplayProtectionOptions { Window = TimeSpan.Zero, ClockSkew = TimeSpan.FromSeconds(-1) };
        both.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*Window*");

        var skewAndEntries = new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(-1), MaxEntries = 0 };
        skewAndEntries.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*ClockSkew*");

        var entriesOnly = new ReplayProtectionOptions { MaxEntries = 0 };
        entriesOnly.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*MaxEntries*");
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void A_negative_skew_set_after_construction_has_no_effect(long ticks)
    {
        // The guard copies the settings it validated: the 60 s default stays in force.
        var options = new ReplayProtectionOptions();
        var guard = new InMemoryReplayGuard(options);
        options.ClockSkew = ticks == long.MinValue ? TimeSpan.MinValue : TimeSpan.FromTicks(ticks);
        var created = S(T0);
        guard.TryAdmit(Alice, "plus60", created + 60, T0).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Alice, "plus61", created + 61, T0).Should().Be(ReplayAdmission.FutureDated);
        guard.TryAdmit(Alice, "m", created, T0).Should().Be(ReplayAdmission.Admitted);
        for (var t = created; t <= created + 363; t++)
        {
            guard.TryAdmit(Noise, "n" + t, t, At(t));
            guard.TryAdmit(Alice, "m", created, At(t)).Should().Be(t > created + 300 ? ReplayAdmission.TooOld : ReplayAdmission.Duplicate);
            guard.Count.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void An_invalid_skew_set_after_construction_does_not_make_later_calls_throw()
    {
        var options = new ReplayProtectionOptions();
        var guard = new InMemoryReplayGuard(options);
        foreach (var skew in new[] { TimeSpan.MaxValue, TimeSpan.MinValue, TimeSpan.FromSeconds(int.MaxValue + 1L) })
        {
            options.ClockSkew = skew;
            var act = () => guard.TryAdmit(Alice, "x" + skew.Ticks, S(T0), T0);
            act.Should().NotThrow();
            var far = () => guard.TryAdmit(Alice, "far" + skew.Ticks, long.MaxValue, DateTimeOffset.MaxValue);
            far.Should().NotThrow();
            far().Should().NotBe(ReplayAdmission.Admitted, "already admitted or refused; never admitted twice");
        }
    }
}
