using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="NegotiatedVersionCache"/> (#96 design D-10): keyed by DID subject, an hour on the
/// injected clock's timestamps, at most its capacity with no insert when full (expired entries make
/// room, a live one is never evicted), a clock that steps back expires an entry, only a negotiated
/// version is stored.
/// </summary>
public sealed class NegotiatedVersionCacheTests
{
    private const string M1 = "did:example:m1";
    private const string M2 = "did:example:m2";

    [Fact]
    public void A_remembered_version_is_found_by_did_subject()
    {
        var cache = new NegotiatedVersionCache(new ManualTimeProvider());

        cache.TryGet(M1, out _).Should().BeFalse();
        cache.Set(M1, CoordinateMediationVersion.V2);

        cache.TryGet(M1, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);
        cache.TryGet(M1 + "#key-1", out version).Should().BeTrue("the key is the DID subject (L-034)");
        cache.TryGet(M2, out _).Should().BeFalse();
        cache.TryGet("not-a-did", out _).Should().BeFalse();
    }

    [Fact]
    public void An_entry_is_used_for_less_than_its_time_to_live()
    {
        var time = new ManualTimeProvider();
        var cache = new NegotiatedVersionCache(time);
        cache.Set(M1, CoordinateMediationVersion.V3);

        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(1));
        cache.TryGet(M1, out _).Should().BeTrue();

        time.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(M1, out _).Should().BeFalse("at one hour it has expired");
        cache.Count.Should().Be(0, "an expired entry is dropped when it is found expired");
    }

    [Fact]
    public void A_full_cache_takes_no_new_entry_but_updates_a_held_one()
    {
        var cache = new NegotiatedVersionCache(new ManualTimeProvider(), capacity: 2);
        cache.Set(M1, CoordinateMediationVersion.V3);
        cache.Set(M2, CoordinateMediationVersion.V3);

        cache.Set("did:example:m3", CoordinateMediationVersion.V2);
        cache.TryGet("did:example:m3", out _).Should().BeFalse();
        cache.Count.Should().Be(2);

        cache.Set(M1, CoordinateMediationVersion.V2);
        cache.TryGet(M1, out var version).Should().BeTrue();
        version.Should().Be(CoordinateMediationVersion.V2);
        cache.TryGet(M2, out _).Should().BeTrue("a live entry is never evicted");
    }

    [Fact]
    public void Expired_entries_make_room_in_a_full_cache()
    {
        var time = new ManualTimeProvider();
        var cache = new NegotiatedVersionCache(time, capacity: 2);
        cache.Set(M1, CoordinateMediationVersion.V3);
        time.Advance(TimeSpan.FromMinutes(30));
        cache.Set(M2, CoordinateMediationVersion.V3);
        time.Advance(TimeSpan.FromMinutes(31)); // M1 is 61 minutes old, M2 31

        cache.Set("did:example:m3", CoordinateMediationVersion.V2);

        cache.TryGet("did:example:m3", out _).Should().BeTrue();
        cache.TryGet(M2, out _).Should().BeTrue();
        cache.TryGet(M1, out _).Should().BeFalse();
    }

    [Fact]
    public void The_default_bound_is_64_mediators_for_an_hour()
    {
        var cache = new NegotiatedVersionCache(new ManualTimeProvider());
        for (var i = 0; i < 70; i++)
            cache.Set($"did:example:m{i}", CoordinateMediationVersion.V3);

        cache.Count.Should().Be(64);
        cache.Capacity.Should().Be(64);
        cache.TimeToLive.Should().Be(TimeSpan.FromHours(1));
        cache.TryGet("did:example:m63", out _).Should().BeTrue();
        cache.TryGet("did:example:m64", out _).Should().BeFalse();
    }

    [Fact]
    public void A_clock_that_steps_back_expires_an_entry()
    {
        var time = new SteppingTime();
        var cache = new NegotiatedVersionCache(time);
        time.Ticks = 10_000_000;
        cache.Set(M1, CoordinateMediationVersion.V3);

        time.Ticks = 5_000_000;

        cache.TryGet(M1, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(CoordinateMediationVersion.Negotiate)]
    [InlineData((CoordinateMediationVersion)9)]
    public void Only_a_negotiated_version_is_stored(CoordinateMediationVersion version)
    {
        var cache = new NegotiatedVersionCache(new ManualTimeProvider());

        FluentActions.Invoking(() => cache.Set(M1, version)).Should().Throw<ArgumentOutOfRangeException>();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void A_zero_capacity_cache_remembers_nothing()
    {
        var cache = new NegotiatedVersionCache(new ManualTimeProvider(), capacity: 0);

        cache.Set(M1, CoordinateMediationVersion.V3);

        cache.TryGet(M1, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_use_keeps_the_bound()
    {
        var cache = new NegotiatedVersionCache(TimeProvider.System, capacity: 8);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                cache.Set($"did:example:m{(t * 200 + i) % 40}", CoordinateMediationVersion.V3);
                cache.TryGet($"did:example:m{i % 40}", out _);
            }
        })));

        cache.Count.Should().BeLessThanOrEqualTo(8);
    }

    /// <summary>A clock whose timestamp the test sets, forwards or backwards.</summary>
    private sealed class SteppingTime : TimeProvider
    {
        public long Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref Ticks);
    }
}
