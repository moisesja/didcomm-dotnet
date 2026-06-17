using System.Collections.Concurrent;

namespace DidComm.Threading;

/// <summary>
/// Process-local, thread-safe <see cref="IThreadStateStore"/>. Suitable for single-instance
/// agents; replace with a distributed store (Redis, Cosmos, etc.) for horizontally-scaled
/// mediators where the same thread can be served by different processes.
/// </summary>
/// <remarks>
/// The store is <b>bounded</b> (issue #21). Thread states are keyed on the inbound message's
/// <c>thid</c>/<c>id</c>/<c>pthid</c>, which on a plaintext or anoncrypt envelope are
/// attacker-controlled and <i>not</i> cryptographically authenticated. An unbounded dictionary
/// would let a peer flood fresh ids and grow the store until the process OOMs. To prevent that,
/// retained entries are capped at <see cref="DefaultMaxEntries"/> (overridable via the ctor) and
/// the oldest-touched entries are evicted (approximate-LRU) once the cap is reached. A thread is
/// touched on every access, so one that is actively receiving is not evicted; note this holds only
/// within the eviction window — a thread left idle while a peer floods more than (cap − low-water)
/// fresh ids can still age out (the approximate-LRU tradeoff).
/// </remarks>
public sealed class InMemoryThreadStateStore : IThreadStateStore
{
    /// <summary>
    /// Default cap on retained thread states. Large enough that no realistic legitimate
    /// concurrent-thread workload on a single-instance agent is ever evicted, small enough that
    /// worst-case memory stays bounded to a few MB instead of growing without limit (issue #21).
    /// </summary>
    public const int DefaultMaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, ThreadState> _states = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly int _lowWaterMark;
    private long _tick;     // Interlocked monotonic clock for approximate-LRU stamping.
    private int _evicting;  // single-flight guard: 1 while an eviction pass is running, else 0.
    private long _evictionPasses; // diagnostics: number of EvictDownToLowWater passes that ran.

    /// <summary>Create a store bounded at <see cref="DefaultMaxEntries"/>.</summary>
    public InMemoryThreadStateStore() : this(DefaultMaxEntries)
    {
    }

    /// <summary>Create a store bounded at <paramref name="maxEntries"/> retained thread states.</summary>
    /// <param name="maxEntries">Hard cap on retained thread states. Must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEntries"/> is not positive.</exception>
    public InMemoryThreadStateStore(int maxEntries)
    {
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                maxEntries,
                "Thread-state store capacity must be > 0; the cap bounds memory to prevent a flood of " +
                "unauthenticated thread ids from exhausting the process (issue #21).");
        }

        _maxEntries = maxEntries;
        // Trim to ~90% so eviction is amortized over ~10% of inserts rather than firing on every over-cap insert.
        _lowWaterMark = Math.Max(1, maxEntries - Math.Max(1, maxEntries / 10));
    }

    /// <summary>Number of currently-retained thread states. Exposed for tests.</summary>
    internal int Count => _states.Count;

    /// <summary>Number of eviction passes run so far. Exposed for tests (the single-flight guard).</summary>
    internal long EvictionPasses => Interlocked.Read(ref _evictionPasses);

    /// <inheritdoc />
    public ThreadState GetOrCreate(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        var state = _states.GetOrAdd(
            thid,
            static (id, self) => new ThreadState(id) { LastTouchedTick = Interlocked.Increment(ref self._tick) },
            this);
        state.LastTouchedTick = Interlocked.Increment(ref _tick); // touch on access (approximate-LRU)

        // Single-flight eviction: only ONE thread sorts+trims at a time; concurrent inserters that
        // also see the store over capacity skip the pass instead of each running their own
        // O(n log n) snapshot-sort. Without this guard, N concurrent inserts over the cap trigger up
        // to N redundant full sorts per burst — a CPU-amplification DoS (issue #21 red-team). The
        // store may transiently exceed the cap by the number of in-flight inserts during a pass; the
        // next insert after the flag clears trims it back.
        if (_states.Count > _maxEntries && Interlocked.CompareExchange(ref _evicting, 1, 0) == 0)
        {
            try
            {
                EvictDownToLowWater();
            }
            finally
            {
                Volatile.Write(ref _evicting, 0);
            }
        }

        return state;
    }

    /// <inheritdoc />
    public ThreadState? Get(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        if (!_states.TryGetValue(thid, out var state))
        {
            return null;
        }

        state.LastTouchedTick = Interlocked.Increment(ref _tick); // a read counts as activity for LRU
        return state;
    }

    /// <inheritdoc />
    public bool Remove(string thid)
    {
        ArgumentException.ThrowIfNullOrEmpty(thid);
        return _states.TryRemove(thid, out _);
    }

    /// <summary>
    /// Evict the oldest-touched entries until the store is back at the low-water mark. Runs only
    /// when an insert pushes the store over capacity, so steady-state access to existing threads
    /// pays nothing. <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/> is
    /// atomic and idempotent, so concurrent eviction passes are harmless (at worst a slight over-trim).
    /// </summary>
    private void EvictDownToLowWater()
    {
        Interlocked.Increment(ref _evictionPasses);
        var snapshot = _states.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.LastTouchedTick.CompareTo(b.Value.LastTouchedTick));
        var target = snapshot.Length - _lowWaterMark;
        for (var i = 0; i < snapshot.Length && i < target; i++)
        {
            _states.TryRemove(snapshot[i].Key, out _);
        }
    }
}
