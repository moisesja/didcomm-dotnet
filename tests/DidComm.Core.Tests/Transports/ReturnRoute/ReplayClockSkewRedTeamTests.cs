using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// #105 red-team pass, kept as regressions. RT-1: clock readings that reach the guard out of order (a
/// race between the endpoint's clock read and the guard's lock, or a host clock stepping back) used to
/// re-admit a captured message once another request had purged its entry — on <c>main</c> too, with a
/// one-second inversion at its zero skew. RT-2: the cost of the widened future bound. RT-3: a live
/// change of <c>Window</c> used to open a gap; the guard now copies its settings.
/// </summary>
public sealed class ReplayClockSkewRedTeamTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string Alice = "did:example:alice";
    private const string Other = "did:example:other";
    private const long W = 300;

    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();
    private static DateTimeOffset At(long s) => DateTimeOffset.FromUnixTimeSeconds(s);

    // RT-1. The caller reads the clock before TryAdmit takes the lock, so two concurrent callers (or a
    // clock stepped back) present non-monotonic `now`s. A purge driven by the later `now` followed by
    // an admission at an earlier `now` used to re-admit a captured message whenever the two differed
    // by more than ClockSkew (whole seconds) — one second at ClockSkew 0 or any sub-second value.
    [Theory]
    [InlineData(0.0)]   // main's effective default (ExpiresClockSkew = 0)
    [InlineData(0.5)]   // floored to zero
    [InlineData(60.0)]  // the new default: needs a 61 s inversion
    public void A_purge_at_a_later_now_followed_by_an_admission_at_an_earlier_now_readmits_the_message(double skewSeconds)
    {
        var options = new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(skewSeconds) };
        var skew = (long)Math.Floor(skewSeconds);
        var guard = new InMemoryReplayGuard(options);
        var created = S(T);

        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted, "the original delivery");

        // Another in-scope request whose clock read is (skew + 1) s later than the replay's enters the
        // lock first and purges Alice's entry (expiry created + W + skew < its now).
        guard.TryAdmit(Other, "x", created + W + skew + 1, At(created + W + skew + 1)).Should().Be(ReplayAdmission.Admitted);

        // The replay, whose clock read was at created + W (still inside the window), enters the lock next.
        guard.TryAdmit(Alice, "m", created, At(created + W))
            .Should().Be(ReplayAdmission.TooOld, "its entry would already have expired at the latest purge instant, so it may have been forgotten");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(60.0)]
    public void An_inversion_of_exactly_ClockSkew_seconds_is_still_caught(double skewSeconds)
    {
        // The bound is tight: the re-admission needs an inversion of ClockSkew + 1 s, not Window + ClockSkew.
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions { ClockSkew = TimeSpan.FromSeconds(skewSeconds) });
        var skew = (long)skewSeconds;
        var created = S(T);
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit(Other, "x", created + W + skew, At(created + W + skew)).Should().Be(ReplayAdmission.Admitted);

        guard.TryAdmit(Alice, "m", created, At(created + W)).Should().Be(ReplayAdmission.Duplicate);
    }

    // RT-1b. A clock that advances normally (purges happen as it goes) and then steps back by
    // ClockSkew + 1 s used to make a message whose entry was purged in that last stretch admissible
    // again.
    [Fact]
    public void A_normal_clock_that_steps_back_by_ClockSkew_plus_one_second_readmits_messages()
    {
        var options = new ReplayProtectionOptions { ClockSkew = TimeSpan.Zero };
        var guard = new InMemoryReplayGuard(options);
        var created = S(T);
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);

        // The clock advances normally to created + W + 1; any in-scope arrival there purges the entry.
        for (var t = created + 1; t <= created + W + 1; t++)
            guard.TryAdmit(Other, "n" + t, t, At(t));

        // A one-second backward step (NTP step, leap second repeated by the kernel).
        guard.TryAdmit(Alice, "m", created, At(created + W))
            .Should().Be(ReplayAdmission.TooOld, "a 1 s backward step must not re-admit a message at ClockSkew 0");
    }

    // RT-2. The widened future bound as a budget lever: a self-minted, authenticated sender stamps
    // created_time = now + ClockSkew, so the global budget it fills stays full for Window + 2 × ClockSkew
    // (420 s at the defaults) instead of Window (300 s on main, where the future bound was 0).
    [Theory]
    [InlineData(60, 420)]
    [InlineData(0, 300)]
    public void A_flood_stamped_at_the_future_bound_keeps_the_budget_saturated_for_Window_plus_twice_the_skew(int skewSeconds, int heldSeconds)
    {
        const int budget = 8;
        var guard = new InMemoryReplayGuard(new ReplayProtectionOptions
        {
            ClockSkew = TimeSpan.FromSeconds(skewSeconds),
            MaxEntries = budget,
            MaxEntriesPerPeer = budget,
        });
        for (var i = 0; i < budget; i++)
            guard.TryAdmit("did:example:flood", "f" + i, S(T) + skewSeconds, T).Should().Be(ReplayAdmission.Admitted);

        var honestAtEnd = At(S(T) + heldSeconds);
        guard.TryAdmit(Alice, "honest-1", S(honestAtEnd), honestAtEnd)
            .Should().Be(ReplayAdmission.Saturated, "an honest sender is still refused {0} s after the flood", heldSeconds);
        var after = At(S(T) + heldSeconds + 1);
        guard.TryAdmit(Alice, "honest-2", S(after), after).Should().Be(ReplayAdmission.Admitted);
    }

    // RT-3 (pre-existing on main). Raising Window after an entry was admitted let the entry expire
    // under the old Window while the age rule used the new one. The guard now copies its settings.
    [Fact]
    public void Raising_Window_after_admission_has_no_effect_on_the_guard()
    {
        var options = new ReplayProtectionOptions();
        var guard = new InMemoryReplayGuard(options);
        var created = S(T);
        guard.TryAdmit(Alice, "m", created, T).Should().Be(ReplayAdmission.Admitted);

        var later = At(created + W + 60 + 1);
        guard.TryAdmit(Other, "purge", S(later), later).Should().Be(ReplayAdmission.Admitted);
        options.Window = TimeSpan.FromMinutes(10);

        guard.TryAdmit(Alice, "m", created, later)
            .Should().Be(ReplayAdmission.TooOld, "the guard still judges age with the Window it was built with");
    }
}
