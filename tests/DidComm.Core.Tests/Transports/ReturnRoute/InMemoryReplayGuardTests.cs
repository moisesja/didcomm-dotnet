using DidComm.Facade;
using DidComm.Transports.ReturnRoute;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Transports.ReturnRoute;

/// <summary>
/// FR-RR-08 guard contract on a fixed clock: key is (peer, id); window, skew and the retention
/// property; timestamp-free handling; saturation without eviction; the id cap; concurrency (L-044).
/// </summary>
public sealed class InMemoryReplayGuardTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const string Alice = "did:example:alice";
    private const string Carol = "did:example:carol";

    // These suites predate ReplayProtectionOptions.ClockSkew (#105) and run at ClockSkew = 0 unless a
    // test asks for a skew, so they are also the "ClockSkew = 0 reproduces the old rule" differential.
    // The skew is set after construction, as a host that mutates its options later would set it, so a
    // value Validate() refuses (negative, beyond int.MaxValue seconds) can still reach the arithmetic.
    private static InMemoryReplayGuard Guard(ReplayProtectionOptions? options = null, TimeSpan? skew = null)
    {
        options ??= new ReplayProtectionOptions();
        options.ClockSkew = TimeSpan.Zero;
        var guard = new InMemoryReplayGuard(options);
        options.ClockSkew = skew ?? TimeSpan.Zero;
        return guard;
    }

    private static long S(DateTimeOffset t) => t.ToUnixTimeSeconds();

    [Fact]
    public void Key_is_peer_and_id()
    {
        var g = Guard();

        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Duplicate);
        g.TryAdmit(Alice, "m2", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Carol, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(3);
        g.PeerCount.Should().Be(2);
    }

    [Fact]
    public void Window_edges_leave_no_gap_between_cached_and_too_old()
    {
        var g = Guard();
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit(Alice, "m1", S(T), T.AddSeconds(299)).Should().Be(ReplayAdmission.Duplicate);
        g.TryAdmit(Alice, "m1", S(T), T.AddSeconds(300)).Should().Be(ReplayAdmission.Duplicate, "present until created + Window inclusive");
        g.Count.Should().Be(1);
        g.TryAdmit(Alice, "m1", S(T), T.AddSeconds(301)).Should().Be(ReplayAdmission.TooOld);
        g.Count.Should().Be(1, "age rejection happens before the purge; the entry is still held");

        // The next insert after expiry purges it.
        g.TryAdmit(Carol, "x", S(T.AddSeconds(301)), T.AddSeconds(301)).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(1);
        g.PeerCount.Should().Be(1);
    }

    [Fact]
    public void Skew_extends_retention_and_bounds_future_dating()
    {
        var g = Guard(skew: TimeSpan.FromSeconds(30));
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit(Alice, "m1", S(T), T.AddSeconds(330)).Should().Be(ReplayAdmission.TooOld);
        g.Count.Should().Be(1, "still present at created + Window + skew exactly");
        g.TryAdmit(Carol, "y", S(T.AddSeconds(330)), T.AddSeconds(330)).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(2, "purge is strictly earlier than now");
        g.TryAdmit(Carol, "z", S(T.AddSeconds(331)), T.AddSeconds(331)).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(2, "m1 purged on the first insert after its expiry");

        g.TryAdmit(Alice, "f1", S(T.AddSeconds(30)), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "f2", S(T.AddSeconds(31)), T).Should().Be(ReplayAdmission.FutureDated);
    }

    [Fact]
    public void Negative_skew_set_after_validation_behaves_as_zero()
    {
        var g = Guard(skew: TimeSpan.FromSeconds(-30));

        g.TryAdmit(Alice, "f1", S(T.AddSeconds(1)), T).Should().Be(ReplayAdmission.FutureDated);
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "m1", S(T), T.AddSeconds(300)).Should().Be(ReplayAdmission.Duplicate);
    }

    [Fact]
    public void Timestamp_free_is_refused_by_default_and_retained_for_Window_when_relaxed()
    {
        Guard().TryAdmit(Alice, "m1", null, T).Should().Be(ReplayAdmission.MissingCreatedTime);

        var relaxed = Guard(new ReplayProtectionOptions { RequireCreatedTime = false });
        relaxed.TryAdmit(Alice, "m1", null, T).Should().Be(ReplayAdmission.Admitted);
        relaxed.TryAdmit(Alice, "m1", null, T.AddSeconds(299)).Should().Be(ReplayAdmission.Duplicate);
        relaxed.TryAdmit(Alice, "m1", null, T.AddSeconds(301)).Should().Be(ReplayAdmission.Admitted, "the documented limitation");
    }

    [Fact]
    public void Forward_shaped_ids_are_not_special_here()
    {
        var g = Guard();
        g.TryAdmit(Alice, "forward-1", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "forward-1", S(T), T).Should().Be(ReplayAdmission.Duplicate);
    }

    [Fact]
    public void Concurrent_duplicates_admit_exactly_once()
    {
        // L-044: rendezvous on a CountdownEvent; dedicated threads so the parked callers cannot
        // starve the thread pool.
        const int Tasks = 32;
        var g = Guard();
        var gate = new CountdownEvent(Tasks);
        var arrived = 0;
        var results = new ReplayAdmission[Tasks];

        var threads = Enumerable.Range(0, Tasks).Select(i => new Thread(() =>
        {
            Interlocked.Increment(ref arrived);
            gate.Signal();
            gate.Wait();
            results[i] = g.TryAdmit(Alice, "same", S(T), T);
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

        arrived.Should().Be(Tasks);
        results.Count(r => r == ReplayAdmission.Admitted).Should().Be(1);
        results.Count(r => r == ReplayAdmission.Duplicate).Should().Be(Tasks - 1);
        g.Count.Should().Be(1);
    }

    [Fact]
    public void Saturation_refuses_without_evicting_and_recovers_after_expiry()
    {
        var g = Guard(new ReplayProtectionOptions { MaxEntries = 4, MaxEntriesPerPeer = 4 });
        for (var i = 0; i < 4; i++)
            g.TryAdmit($"did:example:p{i}", "m", S(T), T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit("did:example:new", "m", S(T), T).Should().Be(ReplayAdmission.Saturated);
        g.Count.Should().Be(4);
        g.SaturationCount.Should().Be(1);
        for (var i = 0; i < 4; i++)
            g.TryAdmit($"did:example:p{i}", "m", S(T), T).Should().Be(ReplayAdmission.Duplicate, "nothing was evicted");

        var later = T.AddSeconds(301);
        g.TryAdmit("did:example:new", "m", S(later), later).Should().Be(ReplayAdmission.Admitted);
        g.Count.Should().Be(1);
        g.SaturationCount.Should().Be(1);
    }

    [Fact]
    public void Per_peer_bound_saturates_one_peer_only()
    {
        var g = Guard(new ReplayProtectionOptions { MaxEntriesPerPeer = 2 });
        g.TryAdmit(Alice, "a", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "b", S(T), T).Should().Be(ReplayAdmission.Admitted);

        g.TryAdmit(Alice, "c", S(T), T).Should().Be(ReplayAdmission.Saturated);
        g.TryAdmit(Carol, "c", S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, "a", S(T), T).Should().Be(ReplayAdmission.Duplicate, "a duplicate is answered before the bound");
    }

    [Fact]
    public void Fresh_DID_flood_is_bounded_and_the_first_peer_survives()
    {
        var g = Guard(new ReplayProtectionOptions { MaxEntries = 100, MaxEntriesPerPeer = 10 });
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Admitted);

        var saturated = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (g.TryAdmit($"did:example:flood{i}", "m", S(T), T) == ReplayAdmission.Saturated)
                saturated++;
        }

        g.PeerCount.Should().BeLessThanOrEqualTo(100);
        g.Count.Should().Be(100);
        saturated.Should().Be(901);
        g.SaturationCount.Should().Be(901);
        g.TryAdmit(Alice, "m1", S(T), T).Should().Be(ReplayAdmission.Duplicate);
    }

    [Fact]
    public void Message_id_cap_is_enforced_before_storing()
    {
        var g = Guard();

        g.TryAdmit(Alice, new string('i', 512), S(T), T).Should().Be(ReplayAdmission.Admitted);
        g.TryAdmit(Alice, new string('i', 513), S(T), T).Should().Be(ReplayAdmission.MessageIdTooLong);
        g.Count.Should().Be(1);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var g = Guard();
        var a = () => g.TryAdmit("", "m", S(T), T);
        a.Should().Throw<ArgumentException>();
        var b = () => g.TryAdmit(Alice, "", S(T), T);
        b.Should().Throw<ArgumentException>();
        var c = () => new InMemoryReplayGuard(null!);
        c.Should().Throw<ArgumentNullException>();
        var d = () => new InMemoryReplayGuard(null!, logger: null);
        d.Should().Throw<ArgumentNullException>();
    }
}
