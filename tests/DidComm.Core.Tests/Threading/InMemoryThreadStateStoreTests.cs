using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

public sealed class InMemoryThreadStateStoreTests
{
    [Fact]
    public void GetOrCreate_returns_same_instance_per_thid()
    {
        var store = new InMemoryThreadStateStore();
        var first = store.GetOrCreate("thread-a");
        var second = store.GetOrCreate("thread-a");
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Different_threads_do_not_share_state()
    {
        var store = new InMemoryThreadStateStore();
        var a = store.GetOrCreate("thread-a");
        var b = store.GetOrCreate("thread-b");
        a.Should().NotBeSameAs(b);
        a.AcceptLang = new[] { "fr", "en" };
        b.AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Accept_lang_preference_does_not_leak_across_concurrent_threads()
    {
        // FR-I18N-02 acceptance test: a preference set on one thread MUST NOT influence another.
        var store = new InMemoryThreadStateStore();
        store.GetOrCreate("thread-fr").AcceptLang = new[] { "fr" };
        var concurrent = store.GetOrCreate("thread-en");
        concurrent.AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Remove_drops_state_so_next_GetOrCreate_starts_fresh()
    {
        // FR-I18N-02: "applies … until the current protocol thread (thid) ends".
        var store = new InMemoryThreadStateStore();
        store.GetOrCreate("thread-a").AcceptLang = new[] { "fr" };
        store.Remove("thread-a").Should().BeTrue();
        store.Get("thread-a").Should().BeNull();
        store.GetOrCreate("thread-a").AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Get_returns_null_for_unknown_thread()
    {
        var store = new InMemoryThreadStateStore();
        store.Get("nope").Should().BeNull();
    }

    [Fact]
    public void Empty_thid_throws()
    {
        var store = new InMemoryThreadStateStore();
        ((Action)(() => store.GetOrCreate(string.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Get(string.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Remove(string.Empty))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Store_is_bounded_under_a_flood_of_fresh_thids()
    {
        // Issue #21: a peer flooding fresh, unauthenticated thids must not grow the store without
        // limit. With a small cap, inserting far more distinct ids keeps the count at/under the cap.
        var store = new InMemoryThreadStateStore(maxEntries: 100);
        for (var i = 0; i < 1_000; i++)
        {
            store.GetOrCreate($"thid-{i}");
        }

        store.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Oldest_untouched_threads_are_evicted_newest_retained()
    {
        var store = new InMemoryThreadStateStore(maxEntries: 100);
        for (var i = 0; i < 1_000; i++)
        {
            store.GetOrCreate($"thid-{i}");
        }

        // The most recently created id survives; an early one has aged out.
        store.Get("thid-999").Should().NotBeNull();
        store.Get("thid-0").Should().BeNull();
    }

    [Fact]
    public void A_live_thread_within_the_cap_survives_a_flood_with_state_intact()
    {
        // A legitimate multi-message thread is touched on every access, so approximate-LRU never
        // evicts it even while an attacker floods junk ids around it.
        var store = new InMemoryThreadStateStore(maxEntries: 100);
        var live = store.GetOrCreate("live");
        live.AcceptLang = new[] { "fr" };

        for (var i = 0; i < 1_000; i++)
        {
            store.GetOrCreate($"junk-{i}");
            store.GetOrCreate("live"); // keep the live thread warm, as real traffic would
        }

        var stillLive = store.Get("live");
        stillLive.Should().NotBeNull();
        stillLive!.AcceptLang.Should().Equal("fr");
    }

    [Fact]
    public void Non_positive_capacity_throws()
    {
        ((Action)(() => _ = new InMemoryThreadStateStore(0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new InMemoryThreadStateStore(-5))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Concurrent_flood_stays_bounded_and_does_not_throw()
    {
        // NFR-03: shared store under concurrent access. Eviction races must not throw and the
        // final count must respect the cap.
        var store = new InMemoryThreadStateStore(maxEntries: 200);
        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                store.GetOrCreate($"w{worker}-thid-{i}");
            }
        }));

        await Task.WhenAll(tasks);
        store.Count.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task Concurrent_flood_does_not_trigger_an_eviction_storm()
    {
        // Issue #21 red-team: without single-flight eviction, N concurrent inserters over the cap each
        // run their own O(n log n) snapshot-sort, so eviction passes scale with concurrency (a CPU DoS).
        // With the guard, passes track the insert count (~one per cap/10 inserts), not the thread count.
        const int cap = 500;
        const int perWorker = 50_000;
        const int workers = 8;
        var store = new InMemoryThreadStateStore(maxEntries: cap);

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
                store.GetOrCreate($"w{w}-{i}");
        }));
        await Task.WhenAll(tasks);

        // Serial expectation ≈ totalInserts / (cap - lowWater) = 400_000 / 50 ≈ 8_000 passes. A storm
        // would multiply that by up to `workers`. Assert we stay within a small multiple of serial.
        const long serialExpectation = (long)workers * perWorker / (cap / 10);
        store.EvictionPasses.Should().BeLessThan(serialExpectation * 3);
        store.Count.Should().BeLessThanOrEqualTo(cap);
    }
}
