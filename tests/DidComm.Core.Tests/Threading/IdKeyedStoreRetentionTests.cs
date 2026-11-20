using System.Runtime.CompilerServices;
using DidComm.Consistency;
using DidComm.Protocols.ProblemReport;
using DidComm.Tests.Protocols.Routing;
using DidComm.Threading;
using AwesomeAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Tests.Threading;

/// <summary>
/// The worst case of the two stores keyed by a message's thread ids: the dispatcher's thread store
/// (<c>thid</c>, else <c>id</c>) and the problem-report cascade budget (<c>pthid</c>). Unpack bounds
/// every such id at 512 characters (FR-MSG-02/11), so each store at its entry cap holds a bounded
/// amount, measured here with keys of exactly that length.
/// </summary>
/// <remarks>
/// Opt-in (<c>DIDCOMM_ADV98_PROBES=1</c>), like the forward retention probe: a heap delta also
/// counts what other work in the same process frees or allocates meanwhile, so on a CI runner it can
/// read well under (or over) what the store holds with no change in the store. The deterministic
/// tests are the regression: an id over 512 characters never reaches a store
/// (<c>MessageIdLengthTests</c>, <c>MessageIdLengthEndpointTests</c>), and a flood through the
/// endpoint fills the thread store with 512-character keys only (<c>MessageIdLengthFloodTests</c>).
/// This probe is the measurement behind the stores' documented bound.
/// </remarks>
[Collection(ForwardRetentionProbeCollection.Name)]
[Trait("Category", "Adv98Probe")]
public sealed class IdKeyedStoreRetentionTests
{
    // The bound the stores' docs state, per store at the default 10 000 entries (measured about
    // 11.8 MB for the thread store and 11.3 MB for the cascade budget on arm64, whose 64-bit object
    // layout x64 shares: a 512-character key is 1 048 bytes, plus about 130 bytes of entry).
    private const long DocumentedBound = 13L * 1000 * 1000;

    private readonly ITestOutputHelper _out;

    public IdKeyedStoreRetentionTests(ITestOutputHelper output) => _out = output;

    // Distinct, mixed case (the stores compare ignoring case), exactly `length` characters.
    private static string Key(int i, int length)
    {
        var prefix = i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + "-";
        return string.Create(length, prefix, static (span, p) =>
        {
            p.AsSpan().CopyTo(span);
            for (var j = p.Length; j < span.Length; j++)
                span[j] = j % 2 == 0 ? 'a' : 'B';
        });
    }

    [HeapProbeFact]
    public void The_thread_store_at_its_cap_with_512_character_keys_holds_less_than_the_documented_bound()
    {
        var baseline = LiveBytes();
        var store = FillThreadStore(2 * InMemoryThreadStateStore.DefaultMaxEntries, IdentifierLimits.MaxMessageIdLength);
        var retained = LiveBytes() - baseline;
        var count = store.Count;
        GC.KeepAlive(store);

        _out.WriteLine($"thread store: {count:N0} entries of 512 characters retain {retained:N0} B ({retained / count:N0} B each)");
        count.Should().BeLessThanOrEqualTo(InMemoryThreadStateStore.DefaultMaxEntries);
        retained.Should().BeLessThanOrEqualTo(DocumentedBound);
        retained.Should().BeGreaterThan(count * 2L * IdentifierLimits.MaxMessageIdLength, "the probe measures the keys it keeps (L-070)");
    }

    [HeapProbeFact]
    public void The_cascade_budget_at_its_cap_with_512_character_pthids_holds_less_than_the_documented_bound()
    {
        var baseline = LiveBytes();
        var budget = FillCascadeBudget(2 * CascadeBudgetStore.DefaultMaxEntries, IdentifierLimits.MaxMessageIdLength);
        var retained = LiveBytes() - baseline;
        var count = budget.Count;
        GC.KeepAlive(budget);

        _out.WriteLine($"cascade budget: {count:N0} entries of 512 characters retain {retained:N0} B ({retained / count:N0} B each)");
        count.Should().BeLessThanOrEqualTo(CascadeBudgetStore.DefaultMaxEntries);
        retained.Should().BeLessThanOrEqualTo(DocumentedBound);
        retained.Should().BeGreaterThan(count * 2L * IdentifierLimits.MaxMessageIdLength, "the probe measures the keys it keeps (L-070)");
    }

    [HeapProbeFact]
    public void Negative_control_keys_four_times_as_long_as_unpack_allows_overshoot_the_bound()
    {
        // The stores themselves take any key: the bound comes from unpack's 512-character limit. A
        // direct caller with 2048-character keys shows the measurement would catch a missing limit.
        var baseline = LiveBytes();
        var store = FillThreadStore(InMemoryThreadStateStore.DefaultMaxEntries, 4 * IdentifierLimits.MaxMessageIdLength);
        var retained = LiveBytes() - baseline;
        GC.KeepAlive(store);

        _out.WriteLine($"control: {store.Count:N0} entries of 2048 characters retain {retained:N0} B");
        retained.Should().BeGreaterThan(DocumentedBound);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InMemoryThreadStateStore FillThreadStore(int keys, int length)
    {
        var store = new InMemoryThreadStateStore();
        for (var i = 0; i < keys; i++)
            store.GetOrCreate(Key(i, length));
        return store;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CascadeBudgetStore FillCascadeBudget(int keys, int length)
    {
        var budget = new CascadeBudgetStore();
        for (var i = 0; i < keys; i++)
            budget.RecordErrorReport(Key(i, length), threshold: 5, repliable: true);
        return budget;
    }

    /// <summary>
    /// Live bytes after full, compacting collections (the LOH too): the heap size the last full
    /// blocking GC left, less its free space. <see cref="GC.GetTotalMemory(bool)"/> is not used: with
    /// the .NET 10 region allocator it reads about twice the live size of the keys here.
    /// </summary>
    private static long LiveBytes()
    {
        for (var i = 0; i < 2; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}

/// <summary>A <see cref="FactAttribute"/> that runs only when <c>DIDCOMM_ADV98_PROBES=1</c> (the switch for the heap probes).</summary>
internal sealed class HeapProbeFactAttribute : FactAttribute
{
    public HeapProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DIDCOMM_ADV98_PROBES") != "1")
            Skip = "A heap measurement that other work in the process disturbs: set DIDCOMM_ADV98_PROBES=1 to run it.";
    }
}
